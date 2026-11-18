using System.Text;
using System.Text.Json;

namespace Grualink.Tests;

public sealed class SignatureVectorsTests
{
    private static readonly VectorFile Vectors = Load();

    public static TheoryData<string> CaseNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (var vector in Vectors.Cases)
            {
                names.Add(vector.Name);
            }

            return names;
        }
    }

    [Fact]
    public void The_vector_file_has_the_expected_shape()
    {
        Assert.Equal(1, Vectors.Version);
        Assert.Equal(300, Vectors.ToleranceSeconds);
        Assert.True(Vectors.Cases.Count >= 30, "at least 30 cases");
        Assert.Equal(Vectors.Cases.Count, Vectors.Cases.Select(c => c.Name).Distinct().Count());
        Assert.Contains(Vectors.Cases, c => c.Expected.Valid);
        Assert.Contains(Vectors.Cases, c => !c.Expected.Valid);
        Assert.Equal(
            new[]
            {
                SignatureVerificationReason.Ok,
                SignatureVerificationReason.MissingSecret,
                SignatureVerificationReason.MissingHeader,
                SignatureVerificationReason.MalformedHeader,
                SignatureVerificationReason.TimestampOutOfTolerance,
                SignatureVerificationReason.SignatureMismatch,
            },
            Vectors.Reasons);
        foreach (var reason in Vectors.Reasons)
        {
            Assert.Contains(Vectors.Cases, c => c.Expected.Reason == reason);
        }
    }

    [Fact]
    public void Every_case_of_the_file_is_verified()
    {
        // The theory below runs one test per case; this one fails if a case is ever skipped or filtered out.
        var verified = 0;
        foreach (var vector in Vectors.Cases)
        {
            Assert.Equal((vector.Expected.Valid, vector.Expected.Reason), Verify(vector));
            verified++;
        }

        Assert.Equal(Vectors.Cases.Count, verified);
        Assert.Equal(Vectors.Cases.Count, CaseNames.Count);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Vector(string name)
    {
        var vector = Vectors.Cases.Single(c => c.Name == name);
        var rawBody = Convert.FromBase64String(vector.BodyBase64);
        if (vector.Body is not null)
        {
            Assert.Equal(Encoding.UTF8.GetBytes(vector.Body), rawBody);
        }

        var result = Verify(vector);

        Assert.True(
            (vector.Expected.Valid, vector.Expected.Reason) == result,
            $"{vector.Name}: expected {vector.Expected}, got {result}. {vector.Description}");
    }

    [Fact]
    public void The_receiver_may_hold_two_secrets_during_its_own_rotation()
    {
        var vector = Vectors.Cases.Single(c => c.Name == "valid_basic");
        var rawBody = Convert.FromBase64String(vector.BodyBase64);
        var header = vector.Headers["x-grualink-signature"];

        var both = Webhooks.VerifyWebhookSignature(
            rawBody, header, secrets: ["whsec_TEST_ONLY_FAKE_NEXT", vector.Secret], nowSeconds: vector.Now);
        Assert.True(both.Valid);

        var none = Webhooks.VerifyWebhookSignature(rawBody, header, secrets: [], nowSeconds: vector.Now);
        Assert.Equal(new SignatureVerificationResult(false, "missing_secret"), none);

        var blanks = Webhooks.VerifyWebhookSignature(rawBody, header, secrets: [null, ""], nowSeconds: vector.Now);
        Assert.Equal(new SignatureVerificationResult(false, "missing_secret"), blanks);
    }

    [Fact]
    public void Verification_never_throws_on_hostile_input()
    {
        var headers = new[]
        {
            null,
            "",
            "42",
            "{}",
            "=",
            ",,,",
            "t=,v1=",
            "t=99999999999999999999,v1=" + new string('a', 64),
            new string('x', 100_000),
            "t=1,v1=" + new string('a', 64) + new string(',', 5000),
            "t=١,v1=" + new string('a', 64),
        };
        foreach (var header in headers)
        {
            foreach (var secret in new[] { null, "", "42", "whsec_TEST_ONLY_FAKE_SECRET_0001" })
            {
                var result = Webhooks.VerifyWebhookSignature("{}"u8, header, secret: secret, nowSeconds: 1);
                Assert.False(result.Valid);
            }
        }

        Assert.False(Webhooks.VerifyWebhookSignature([], null, secrets: null).Valid);
    }

    [Fact]
    public void The_system_clock_is_used_when_no_time_is_given()
    {
        var body = "{\"id\": \"x\"}"u8.ToArray();
        const string Secret = "whsec_TEST_ONLY_FAKE_A";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var fresh = Webhooks.BuildSignatureHeader([Secret], now, body);
        var stale = Webhooks.BuildSignatureHeader([Secret], now - 3600, body);

        Assert.True(Webhooks.VerifyWebhookSignature(body, fresh, secret: Secret).Valid);
        Assert.Equal("timestamp_out_of_tolerance", Webhooks.VerifyWebhookSignature(body, stale, secret: Secret).Reason);
    }

    [Fact]
    public void BuildSignatureHeader_produces_what_the_verifier_accepts()
    {
        var body = "{\"id\": \"x\"}"u8.ToArray();
        var secrets = new[] { "whsec_TEST_ONLY_FAKE_A", "whsec_TEST_ONLY_FAKE_B" };

        var header = Webhooks.BuildSignatureHeader(secrets, 1795000000, body);

        Assert.Matches("^t=1795000000,v1=[0-9a-f]{64},v1=[0-9a-f]{64}$", header);
        foreach (var secret in secrets)
        {
            Assert.True(Webhooks.VerifyWebhookSignature(body, header, secret: secret, nowSeconds: 1795000001).Valid);
        }
    }

    private static (bool Valid, string Reason) Verify(VectorCase vector)
    {
        var result = Webhooks.VerifyWebhookSignature(
            Convert.FromBase64String(vector.BodyBase64),
            vector.Headers.GetValueOrDefault("x-grualink-signature"),
            secret: vector.Secret,
            nowSeconds: vector.Now,
            toleranceSeconds: Vectors.ToleranceSeconds);
        return (result.Valid, result.Reason);
    }

    private static VectorFile Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "contract", "signature-vectors.json");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        return JsonSerializer.Deserialize<VectorFile>(File.ReadAllBytes(path), options)
            ?? throw new InvalidOperationException("The vector file is empty.");
    }

    private sealed record VectorFile(int Version, double ToleranceSeconds, IReadOnlyList<string> Reasons, IReadOnlyList<VectorCase> Cases);

    private sealed record VectorCase(
        string Name,
        string Description,
        string Secret,
        IReadOnlyDictionary<string, string> Headers,
        string? Body,
        string BodyBase64,
        long Now,
        VectorExpected Expected);

    private sealed record VectorExpected(bool Valid, string Reason);
}
