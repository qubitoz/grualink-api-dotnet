using System.Text;
using System.Text.Json;

namespace Grualink;

/// <summary>Configuration of <see cref="WebhookHandler"/>.</summary>
public sealed class WebhookHandlerOptions
{
    /// <summary>Signing secret (<c>whsec_...</c>), or current and previous during your rotation.</summary>
    public required IReadOnlyList<string> Secrets { get; init; }

    /// <summary>
    /// Called once per verified, non-duplicate delivery. Keep it fast (GRUALINK waits 10 seconds): store the
    /// event or enqueue it and do the heavy work later. If it throws, the answer is 500 and the delivery is
    /// retried.
    /// </summary>
    public required Func<WebhookEvent, CancellationToken, Task> OnEvent { get; init; }

    /// <summary>Defaults to an <see cref="InMemoryDeliveryDeduplicator"/>.</summary>
    public IDeliveryDeduplicator? Deduplicator { get; init; }

    public double ToleranceSeconds { get; init; } = Webhooks.DefaultToleranceSeconds;

    /// <summary>Clock in seconds, for tests.</summary>
    public Func<double>? NowSeconds { get; init; }
}

/// <summary>JSON body to answer a delivery with.</summary>
public sealed record WebhookResponseBody(bool Received, bool? Duplicate = null, string? Error = null)
{
    /// <summary>For example <c>{"received":true}</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, GrualinkJson.Options);
}

/// <summary>What to answer to one delivery.</summary>
/// <param name="Status">HTTP status to answer with.</param>
/// <param name="Body">JSON body to answer with.</param>
/// <param name="Reason">
/// Internal detail for your logs; never send it back to the caller. One of the signature reasons, or
/// <c>invalid_payload</c>, <c>delivery_id_mismatch</c>, <c>duplicate</c>, <c>handler_failed</c>.
/// </param>
public sealed record WebhookHandlerResult(int Status, WebhookResponseBody Body, string Reason);

/// <summary>
/// Framework-independent handling of one delivery: verify the signature over the raw body, parse,
/// deduplicate and dispatch. Mount it on any HTTP server that can give you the raw bytes.
/// </summary>
public sealed class WebhookHandler
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly WebhookHandlerOptions _options;
    private readonly IDeliveryDeduplicator _deduplicator;

    public WebhookHandler(WebhookHandlerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.OnEvent);

        _options = options;
        _deduplicator = options.Deduplicator ?? new InMemoryDeliveryDeduplicator();
    }

    /// <param name="rawBody">The RAW body bytes, exactly as received.</param>
    /// <param name="signatureHeader">Value of the <c>X-GruaLink-Signature</c> header.</param>
    /// <param name="deliveryId">Value of the <c>X-GruaLink-Delivery</c> header.</param>
    /// <param name="cancellationToken">Passed to the deduplicator and to <c>OnEvent</c>.</param>
    public async Task<WebhookHandlerResult> HandleAsync(
        ReadOnlyMemory<byte> rawBody,
        string? signatureHeader,
        string? deliveryId,
        CancellationToken cancellationToken = default)
    {
        var verification = Webhooks.VerifyWebhookSignature(
            rawBody.Span, signatureHeader, _options.Secrets, _options.NowSeconds?.Invoke(), _options.ToleranceSeconds);
        if (!verification.Valid)
        {
            return new WebhookHandlerResult(401, new WebhookResponseBody(false, Error: "invalid_signature"), verification.Reason);
        }

        // Only now, with the signature verified, is the body parsed.
        var webhookEvent = Parse(rawBody.Span);
        if (webhookEvent is null)
        {
            return new WebhookHandlerResult(400, new WebhookResponseBody(false, Error: "invalid_payload"), "invalid_payload");
        }

        // The header is not covered by the signature; the id inside the signed body is. They must agree.
        if (deliveryId is null || !string.Equals(deliveryId, webhookEvent.Id, StringComparison.Ordinal))
        {
            return new WebhookHandlerResult(
                400, new WebhookResponseBody(false, Error: "delivery_id_mismatch"), "delivery_id_mismatch");
        }

        if (!await _deduplicator.ClaimAsync(deliveryId, cancellationToken).ConfigureAwait(false))
        {
            return new WebhookHandlerResult(200, new WebhookResponseBody(true, Duplicate: true), "duplicate");
        }

        try
        {
            await _options.OnEvent(webhookEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Forget the id so the retry GRUALINK sends after a 5xx is processed.
            await _deduplicator.ReleaseAsync(deliveryId, CancellationToken.None).ConfigureAwait(false);
            return new WebhookHandlerResult(500, new WebhookResponseBody(false, Error: "handler_failed"), "handler_failed");
        }

        return new WebhookHandlerResult(200, new WebhookResponseBody(true), SignatureVerificationReason.Ok);
    }

    private static WebhookEvent? Parse(ReadOnlySpan<byte> rawBody)
    {
        try
        {
            using var document = JsonDocument.Parse(StrictUtf8.GetString(rawBody));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var parsed = root.Deserialize<WebhookEvent>(GrualinkJson.Options);
            if (parsed is null)
            {
                return null;
            }

            return parsed.Data is null ? parsed with { Data = new WebhookEventData() } : parsed;
        }
        catch (Exception cause) when (cause is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
