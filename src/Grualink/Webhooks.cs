using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Grualink;

/// <summary>Result of <see cref="Webhooks.VerifyWebhookSignature(ReadOnlySpan{byte}, string?, string?, double?, double)"/>.</summary>
/// <param name="Valid">Whether to accept the delivery.</param>
/// <param name="Reason">One of the values of <see cref="SignatureVerificationReason"/>.</param>
public readonly record struct SignatureVerificationResult(bool Valid, string Reason);

/// <summary>The six reasons of the contract, with their wire names.</summary>
public static class SignatureVerificationReason
{
    public const string Ok = "ok";
    public const string MissingSecret = "missing_secret";
    public const string MissingHeader = "missing_header";
    public const string MalformedHeader = "malformed_header";
    public const string TimestampOutOfTolerance = "timestamp_out_of_tolerance";
    public const string SignatureMismatch = "signature_mismatch";
}

/// <summary>Verification of signed webhook deliveries. Pure functions: no I/O and no framework.</summary>
public static class Webhooks
{
    public const string SignatureHeader = "X-GruaLink-Signature";
    public const string DeliveryHeader = "X-GruaLink-Delivery";
    public const string EventHeader = "X-GruaLink-Event";
    public const string TimestampHeader = "X-GruaLink-Timestamp";
    public const string ModeHeader = "X-GruaLink-Mode";

    /// <summary>Deliveries whose signed timestamp differs more than this from the current time are rejected.</summary>
    public const double DefaultToleranceSeconds = 300;

    /// <summary>A delivery body is never larger than 64 KB.</summary>
    public const int MaxWebhookBodyBytes = 64 * 1024;

    /// <summary>
    /// HMAC-SHA256, lowercase hex, of <c>&lt;timestamp&gt;.&lt;raw body&gt;</c> keyed with the secret exactly
    /// as issued, including its <c>whsec_</c> prefix.
    /// </summary>
    public static string ComputeWebhookSignature(string secret, string timestamp, ReadOnlySpan<byte> rawBody)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(timestamp);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret));
        hmac.AppendData(Encoding.UTF8.GetBytes(timestamp + "."));
        hmac.AppendData(rawBody);
        return Convert.ToHexString(hmac.GetHashAndReset()).ToLowerInvariant();
    }

    /// <inheritdoc cref="ComputeWebhookSignature(string, string, ReadOnlySpan{byte})"/>
    public static string ComputeWebhookSignature(string secret, long timestamp, ReadOnlySpan<byte> rawBody) =>
        ComputeWebhookSignature(secret, timestamp.ToString(CultureInfo.InvariantCulture), rawBody);

    /// <summary>Builds the header the way GRUALINK sends it. Useful to test your own receiver locally.</summary>
    public static string BuildSignatureHeader(IEnumerable<string> secrets, long timestamp, ReadOnlySpan<byte> rawBody)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        var header = new StringBuilder();
        header.Append("t=").Append(timestamp.ToString(CultureInfo.InvariantCulture));
        foreach (var secret in secrets)
        {
            header.Append(",v1=").Append(ComputeWebhookSignature(secret, timestamp, rawBody));
        }

        return header.ToString();
    }

    /// <summary>Verifies <c>X-GruaLink-Signature</c> with one secret. See the overload that takes several.</summary>
    /// <param name="rawBody">The RAW request body exactly as received, before any JSON parsing.</param>
    /// <param name="signatureHeader">Value of the <c>X-GruaLink-Signature</c> header.</param>
    /// <param name="secret">Your signing secret, whole (with its <c>whsec_</c> prefix).</param>
    /// <param name="nowSeconds">Current time in seconds since 1970 (UTC). Defaults to the system clock.</param>
    /// <param name="toleranceSeconds">Defaults to 300 seconds.</param>
    public static SignatureVerificationResult VerifyWebhookSignature(
        ReadOnlySpan<byte> rawBody,
        string? signatureHeader,
        string? secret,
        double? nowSeconds = null,
        double toleranceSeconds = DefaultToleranceSeconds)
    {
        return VerifyWebhookSignature(
            rawBody, signatureHeader, secret is null ? Array.Empty<string>() : new[] { secret }, nowSeconds, toleranceSeconds);
    }

    /// <summary>
    /// Verifies <c>X-GruaLink-Signature</c>. Pure and total: it never throws and has no side effects.
    /// Pass two secrets (current and previous) during your own rotation window.
    /// </summary>
    /// <remarks>
    /// Order of checks (shared by every language; the contract vectors pin it):
    /// <list type="number">
    /// <item><description>no secret: <c>missing_secret</c></description></item>
    /// <item><description>header absent or blank: <c>missing_header</c></description></item>
    /// <item><description>no <c>t</c> of 1 to 12 digits, or no well-formed <c>v1</c>: <c>malformed_header</c></description></item>
    /// <item><description>|now - t| greater than the tolerance: <c>timestamp_out_of_tolerance</c></description></item>
    /// <item><description>no <c>v1</c> equals the expected signature: <c>signature_mismatch</c></description></item>
    /// </list>
    /// </remarks>
    public static SignatureVerificationResult VerifyWebhookSignature(
        ReadOnlySpan<byte> rawBody,
        string? signatureHeader,
        IEnumerable<string?>? secrets,
        double? nowSeconds = null,
        double toleranceSeconds = DefaultToleranceSeconds)
    {
        try
        {
            var usable = new List<string>();
            foreach (var secret in secrets ?? [])
            {
                if (!string.IsNullOrEmpty(secret))
                {
                    usable.Add(secret);
                }
            }

            if (usable.Count == 0)
            {
                return Fail(SignatureVerificationReason.MissingSecret);
            }

            if (string.IsNullOrWhiteSpace(signatureHeader))
            {
                return Fail(SignatureVerificationReason.MissingHeader);
            }

            string? timestampText = null;
            var candidates = new List<string>();
            foreach (var part in signatureHeader.Split(','))
            {
                var item = part.Trim();
                var separator = item.IndexOf('=');
                if (separator < 0)
                {
                    continue;
                }

                var name = item[..separator];
                var value = item[(separator + 1)..];
                if (name == "t" && timestampText is null)
                {
                    timestampText = value;
                }
                else if (name == "v1" && IsSignatureV1(value))
                {
                    // Anything that is not exactly 64 lowercase hex characters is ignored, as are unknown schemes.
                    candidates.Add(value);
                }
            }

            if (timestampText is null || !IsTimestamp(timestampText) || candidates.Count == 0)
            {
                return Fail(SignatureVerificationReason.MalformedHeader);
            }

            var now = nowSeconds ?? (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
            var signedAt = long.Parse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture);
            if (Math.Abs(now - signedAt) > toleranceSeconds)
            {
                return Fail(SignatureVerificationReason.TimestampOutOfTolerance);
            }

            // Every pair is compared in constant time and the loops never exit early.
            var matched = false;
            foreach (var secret in usable)
            {
                var expected = Encoding.ASCII.GetBytes(ComputeWebhookSignature(secret, timestampText, rawBody));
                foreach (var candidate in candidates)
                {
                    matched |= CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), expected);
                }
            }

            return matched
                ? new SignatureVerificationResult(true, SignatureVerificationReason.Ok)
                : Fail(SignatureVerificationReason.SignatureMismatch);
        }
        catch (Exception)
        {
            return Fail(SignatureVerificationReason.MalformedHeader);
        }
    }

    /// <summary>
    /// Reads a request body with a hard cap, without ever buffering more than <paramref name="limit"/> bytes.
    /// Returns <c>null</c> when the body is larger.
    /// </summary>
    public static async Task<byte[]?> ReadRawBodyAsync(
        Stream body,
        long? declaredLength = null,
        int limit = MaxWebhookBodyBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (declaredLength > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Creates the framework-independent handler of deliveries. Same as <c>new WebhookHandler(options)</c>.</summary>
    public static WebhookHandler CreateWebhookHandler(WebhookHandlerOptions options) => new(options);

    private static SignatureVerificationResult Fail(string reason) => new(false, reason);

    private static bool IsTimestamp(string value) =>
        value.Length is >= 1 and <= 12 && value.All(c => c is >= '0' and <= '9');

    private static bool IsSignatureV1(string value) =>
        value.Length == 64 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
