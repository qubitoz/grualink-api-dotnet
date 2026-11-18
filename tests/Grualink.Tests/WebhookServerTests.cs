using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Grualink.Examples.WebhookServer;
using Microsoft.AspNetCore.Builder;

namespace Grualink.Tests;

public sealed class WebhookServerTests : IAsyncLifetime
{
    private const string Secret = "whsec_TEST_ONLY_FAKE_SECRET_0001";
    private const string Previous = "whsec_TEST_ONLY_FAKE_SECRET_0000";
    private const long Now = 1795000000;

    private readonly List<WebhookEvent> _received = [];
    private readonly List<WebhookHandlerResult> _results = [];
    private readonly HttpClient _http = new();
    private WebApplication _app = null!;
    private string _url = "";
    private bool _failNext;
    private double _clock = Now;
    private int _counter;

    public async Task InitializeAsync()
    {
        _app = WebhookApp.Build(
            new WebhookHandlerOptions
            {
                Secrets = [Secret],
                NowSeconds = () => _clock,
                OnEvent = (webhookEvent, _) =>
                {
                    if (_failNext)
                    {
                        _failNext = false;
                        throw new InvalidOperationException("database is down");
                    }

                    _received.Add(webhookEvent);
                    return Task.CompletedTask;
                },
            },
            onResult: (result, _) => _results.Add(result));
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();
        _url = _app.Urls.First().TrimEnd('/') + WebhookApp.Path;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task A_signed_delivery_is_accepted_and_handed_to_OnEvent()
    {
        var delivery = NewDelivery();

        var (status, json) = await PostAsync(delivery.Body, SignedHeaders(delivery));

        Assert.Equal(200, status);
        Assert.Equal("""{"received":true}""", json);
        var received = Assert.Single(_received);
        Assert.Equal(delivery.Id, received.Id);
        Assert.Equal("service_request.confirmed", received.Type);
        Assert.Equal("test", received.Mode);
        Assert.Equal("quoted", received.Data.PreviousStatus);
        Assert.Equal("confirmed", received.Data.ServiceRequest?.Status);
        Assert.Equal("TEST-CONFIRMAR-ÑU", received.Data.ServiceRequest?.ExternalRef);
        Assert.Equal("ok", Assert.Single(_results).Reason);
    }

    [Fact]
    public async Task A_retry_of_the_same_delivery_answers_200_but_is_processed_once()
    {
        var delivery = NewDelivery();

        var first = await PostAsync(delivery.Body, SignedHeaders(delivery));
        // A retry is signed again, with a new timestamp and the same id.
        var second = await PostAsync(delivery.Body, SignedHeaders(delivery, timestamp: Now + 60));

        Assert.Equal(200, first.Status);
        Assert.Equal(200, second.Status);
        Assert.Equal("""{"received":true,"duplicate":true}""", second.Json);
        Assert.Single(_received);
        Assert.Equal(new[] { "ok", "duplicate" }, _results.Select(r => r.Reason));
    }

    [Fact]
    public async Task When_OnEvent_fails_the_answer_is_500_and_the_retry_is_processed()
    {
        var delivery = NewDelivery();
        _failNext = true;

        var failed = await PostAsync(delivery.Body, SignedHeaders(delivery));
        Assert.Equal(500, failed.Status);
        Assert.Equal("""{"received":false,"error":"handler_failed"}""", failed.Json);
        Assert.DoesNotContain("database", failed.Json);
        Assert.Empty(_received);

        var retried = await PostAsync(delivery.Body, SignedHeaders(delivery));
        Assert.Equal(200, retried.Status);
        Assert.Equal("""{"received":true}""", retried.Json);
        Assert.Single(_received);
    }

    [Fact]
    public async Task During_a_rotation_a_delivery_signed_with_both_secrets_is_accepted()
    {
        var delivery = NewDelivery();

        var (status, _) = await PostAsync(delivery.Body, SignedHeaders(delivery, secrets: ["whsec_TEST_ONLY_FAKE_NEXT", Secret]));
        Assert.Equal(200, status);

        var other = NewDelivery();
        var (rejected, _) = await PostAsync(other.Body, SignedHeaders(other, secrets: ["whsec_TEST_ONLY_FAKE_NEXT", Previous]));
        Assert.Equal(401, rejected);
    }

    [Fact]
    public async Task A_tampered_body_a_missing_signature_and_a_stale_timestamp_are_rejected_with_401()
    {
        var delivery = NewDelivery();
        var headers = SignedHeaders(delivery);

        var tampered = await PostAsync(delivery.Body.Replace("confirmed", "completed", StringComparison.Ordinal), headers);
        var unsigned = await PostAsync(delivery.Body, headers.Where(h => h.Key != Webhooks.SignatureHeader).ToDictionary());
        _clock = Now + 301;
        var stale = await PostAsync(delivery.Body, headers);

        // The caller gets the same answer in every case; the reason stays in the logs.
        foreach (var (status, json) in new[] { tampered, unsigned, stale })
        {
            Assert.Equal(401, status);
            Assert.Equal("""{"received":false,"error":"invalid_signature"}""", json);
        }

        Assert.Equal(
            new[] { "signature_mismatch", "missing_header", "timestamp_out_of_tolerance" },
            _results.Select(r => r.Reason));
        Assert.Empty(_received);

        // Nothing was claimed by the rejected attempts: the genuine delivery is still processed.
        _clock = Now;
        Assert.Equal(200, (await PostAsync(delivery.Body, headers)).Status);
        Assert.Single(_received);
    }

    [Fact]
    public async Task The_body_parsed_and_serialized_again_does_not_verify()
    {
        var delivery = NewDelivery();
        var reserialized = JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(delivery.Body));
        Assert.NotEqual(delivery.Body, reserialized);

        var (status, _) = await PostAsync(reserialized, SignedHeaders(delivery));

        Assert.Equal(401, status);
    }

    [Fact]
    public async Task A_delivery_header_that_disagrees_with_the_signed_id_is_rejected()
    {
        var delivery = NewDelivery();
        var headers = SignedHeaders(delivery);
        headers[Webhooks.DeliveryHeader] = "bbbbbbbb-0000-4000-8000-999999999999";

        var (status, json) = await PostAsync(delivery.Body, headers);

        Assert.Equal(400, status);
        Assert.Equal("""{"received":false,"error":"delivery_id_mismatch"}""", json);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task A_signed_body_that_is_not_an_event_is_a_400()
    {
        foreach (var body in new[] { "[1, 2, 3]", "{\"id\": 7, \"type\": \"x\"}", "not json" })
        {
            var headers = new Dictionary<string, string>
            {
                [Webhooks.DeliveryHeader] = "x",
                [Webhooks.SignatureHeader] = Webhooks.BuildSignatureHeader([Secret], Now, Encoding.UTF8.GetBytes(body)),
            };

            var (status, json) = await PostAsync(body, headers);

            Assert.Equal(400, status);
            Assert.Equal("""{"received":false,"error":"invalid_payload"}""", json);
        }
    }

    [Fact]
    public async Task Other_methods_other_paths_and_oversized_bodies_are_refused()
    {
        var delivery = NewDelivery();

        using var get = await _http.GetAsync(_url);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);

        using var elsewhere = await _http.PostAsync(_url + "/elsewhere", new StringContent(delivery.Body));
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);

        var huge = new string(' ', Webhooks.MaxWebhookBodyBytes) + delivery.Body;
        var (status, json) = await PostAsync(huge, SignedHeaders(delivery with { Body = huge }));
        Assert.Equal(413, status);
        Assert.Equal("""{"received":false,"error":"payload_too_large"}""", json);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task ReadRawBodyAsync_enforces_the_cap_without_a_declared_length()
    {
        using var small = new MemoryStream(new byte[100]);
        using var large = new MemoryStream(new byte[101]);

        Assert.Equal(100, (await Webhooks.ReadRawBodyAsync(small, declaredLength: null, limit: 100))?.Length);
        Assert.Null(await Webhooks.ReadRawBodyAsync(large, declaredLength: null, limit: 100));
        Assert.Null(await Webhooks.ReadRawBodyAsync(Stream.Null, declaredLength: 101, limit: 100));
    }

    [Fact]
    public void InMemoryDeliveryDeduplicator_claim_release_expiry_and_size_cap()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(Now);
        var deduplicator = new InMemoryDeliveryDeduplicator(TimeSpan.FromSeconds(10), maxEntries: 3, now: () => now);

        Assert.True(deduplicator.Claim("a"));
        Assert.False(deduplicator.Claim("a"));

        deduplicator.Release("a");
        Assert.True(deduplicator.Claim("a"));

        now = now.AddSeconds(11);
        Assert.True(deduplicator.Claim("a"));

        // With room for three, claiming a fourth forgets the oldest.
        Assert.True(deduplicator.Claim("b"));
        Assert.True(deduplicator.Claim("c"));
        Assert.True(deduplicator.Claim("d"));
        Assert.False(deduplicator.Claim("d"));
        Assert.False(deduplicator.Claim("c"));
        Assert.True(deduplicator.Claim("a"));
    }

    private Delivery NewDelivery()
    {
        var id = "bbbbbbbb-0000-4000-8000-" + (++_counter).ToString("D12", CultureInfo.InvariantCulture);

        // Deliberately not what a JSON library would write, and with non-ASCII text.
        var body = "{\"id\": \"" + id + "\", \"data\": {\"previous_status\": \"quoted\", \"service_request\": "
            + "{\"id\": \"11111111-2222-4333-8444-555555555555\", \"status\": \"confirmed\", \"external_ref\": \"TEST-CONFIRMAR-ÑU\"}}, "
            + "\"mode\": \"test\", \"type\": \"service_request.confirmed\", \"object\": \"event\", "
            + "\"created_at\": \"2026-11-18T10:05:01.000Z\"}";
        return new Delivery(id, "service_request.confirmed", body);
    }

    private static Dictionary<string, string> SignedHeaders(Delivery delivery, string[]? secrets = null, long timestamp = Now) => new()
    {
        [Webhooks.EventHeader] = delivery.Type,
        [Webhooks.DeliveryHeader] = delivery.Id,
        [Webhooks.TimestampHeader] = timestamp.ToString(CultureInfo.InvariantCulture),
        [Webhooks.ModeHeader] = "test",
        [Webhooks.SignatureHeader] = Webhooks.BuildSignatureHeader(secrets ?? [Secret], timestamp, Encoding.UTF8.GetBytes(delivery.Body)),
    };

    private async Task<(int Status, string Json)> PostAsync(string body, IReadOnlyDictionary<string, string> headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _url);
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await _http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private sealed record Delivery(string Id, string Type, string Body);
}
