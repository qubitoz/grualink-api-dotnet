using System.Text.Json;
using System.Text.Json.Nodes;

namespace Grualink.Tests;

public sealed class ClientTests : IAsyncLifetime
{
    // Obviously fake: it only has the prefix the client checks.
    private const string TestKey = "glk_test_FAKE_KEY_FOR_UNIT_TESTS_ONLY";

    private static readonly ServiceRequestInput Input = new()
    {
        ExternalRef = "TEST-CONFIRMAR-0001",
        Contact = new ContactInput { Name = "Persona de Ejemplo", Phone = "+525500000000" },
        Pickup = new PlaceInput { Address = "Calle Ficticia 123, Ciudad Ejemplo", Lat = 20.6767, Lng = -103.3475 },
        Destination = new PlaceInput { Address = "Taller Ficticio, Avenida Ejemplo 456", Lat = 20.7214, Lng = -103.3918 },
    };

    private readonly List<double> _sleeps = [];
    private readonly List<RetryInfo> _retries = [];
    private readonly List<GrualinkClient> _clients = [];
    private ScriptedApi _api = null!;

    public static TheoryData<int, string, Type> ErrorTable => new()
    {
        { 400, "invalid_request", typeof(GrualinkInvalidRequestException) },
        { 400, "invalid_json", typeof(GrualinkInvalidRequestException) },
        { 400, "key_in_query", typeof(GrualinkInvalidRequestException) },
        { 400, "idempotency_key_required", typeof(GrualinkInvalidRequestException) },
        { 400, "invalid_idempotency_key", typeof(GrualinkInvalidRequestException) },
        { 401, "unauthorized", typeof(GrualinkAuthenticationException) },
        { 403, "insufficient_scope", typeof(GrualinkPermissionException) },
        { 403, "customer_unavailable", typeof(GrualinkPermissionException) },
        { 403, "live_mode_unavailable", typeof(GrualinkPermissionException) },
        { 403, "browser_not_allowed", typeof(GrualinkPermissionException) },
        { 404, "not_found", typeof(GrualinkNotFoundException) },
        { 405, "method_not_allowed", typeof(GrualinkInvalidRequestException) },
        { 409, "idempotency_conflict", typeof(GrualinkIdempotencyConflictException) },
        { 413, "payload_too_large", typeof(GrualinkInvalidRequestException) },
        { 415, "unsupported_media_type", typeof(GrualinkInvalidRequestException) },
        { 422, "validation_error", typeof(GrualinkValidationException) },
        { 429, "too_many_open_requests", typeof(GrualinkRateLimitException) },
    };

    public async Task InitializeAsync()
    {
        _api = await ScriptedApi.StartAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        await _api.DisposeAsync();
    }

    // ── Success ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateServiceRequest_sends_the_key_the_idempotency_key_and_the_json_body()
    {
        _api.Reply(ScriptedApi.Json(201, ScriptedApi.ServiceRequestJson));

        var result = await NewClient().CreateServiceRequestAsync(Input, idempotencyKey: "order-0001");

        Assert.Equal(ScriptedApi.ServiceRequestId, result.ServiceRequest.Id);
        Assert.Equal("quoted", result.ServiceRequest.Status);
        Assert.Equal(1002.62m, result.ServiceRequest.Quote?.EstimatedPrice?.Total);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero), result.ServiceRequest.CreatedAt);
        Assert.False(result.Replayed);
        Assert.Equal("order-0001", result.IdempotencyKey);

        var sent = Assert.Single(_api.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/api/v1/service-requests", sent.Path);
        Assert.Equal($"Bearer {TestKey}", sent.Header("Authorization"));
        Assert.Equal("order-0001", sent.Header("Idempotency-Key"));
        Assert.Equal("application/json", sent.Header("Content-Type"));

        var expected = JsonNode.Parse("""
            {
              "external_ref": "TEST-CONFIRMAR-0001",
              "contact": { "name": "Persona de Ejemplo", "phone": "+525500000000" },
              "pickup": { "address": "Calle Ficticia 123, Ciudad Ejemplo", "lat": 20.6767, "lng": -103.3475 },
              "destination": { "address": "Taller Ficticio, Avenida Ejemplo 456", "lat": 20.7214, "lng": -103.3918 }
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(sent.BodyText)), sent.BodyText);
    }

    [Fact]
    public async Task CreateServiceRequest_generates_an_idempotency_key_when_none_is_given()
    {
        _api.Reply(ScriptedApi.Json(201, ScriptedApi.ServiceRequestJson));

        var result = await NewClient().CreateServiceRequestAsync(Input);

        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", result.IdempotencyKey);
        Assert.Equal(result.IdempotencyKey, _api.Requests[0].Header("Idempotency-Key"));
    }

    [Fact]
    public async Task A_repeated_request_is_a_200_with_idempotent_replayed_and_is_reported_as_replayed()
    {
        _api.Reply(
            ScriptedApi.Json(201, ScriptedApi.ServiceRequestJson),
            ScriptedApi.Json(200, ScriptedApi.ServiceRequestJson, ("Idempotent-Replayed", "true")));
        var client = NewClient();

        var first = await client.CreateServiceRequestAsync(Input, idempotencyKey: "order-0002");
        var second = await client.CreateServiceRequestAsync(Input, idempotencyKey: "order-0002");

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.ServiceRequest.Id, second.ServiceRequest.Id);
        Assert.Equal(_api.Requests[0].Body, _api.Requests[1].Body);
    }

    [Fact]
    public async Task A_200_without_the_replay_header_is_not_reported_as_replayed()
    {
        _api.Reply(ScriptedApi.Json(200, ScriptedApi.ServiceRequestJson));

        var result = await NewClient().CreateServiceRequestAsync(Input);

        Assert.False(result.Replayed);
    }

    [Fact]
    public async Task GetServiceRequest_and_FindServiceRequestsByExternalRef()
    {
        _api.Reply(ScriptedApi.Json(200, ScriptedApi.ServiceRequestJson));
        var one = await NewClient().GetServiceRequestAsync(ScriptedApi.ServiceRequestId);
        Assert.Equal("quoted", one.Status);
        Assert.Equal("GET", _api.Requests[0].Method);
        Assert.Equal($"/api/v1/service-requests/{ScriptedApi.ServiceRequestId}", _api.Requests[0].Path);
        Assert.Null(_api.Requests[0].Header("Idempotency-Key"));

        _api.Reply(ScriptedApi.Json(200, $$"""{ "object": "list", "data": [{{ScriptedApi.ServiceRequestJson}}] }"""));
        var list = await NewClient().FindServiceRequestsByExternalRefAsync("SIN 2026/001 ñ&x=1");
        Assert.Single(list);
        Assert.Equal("/api/v1/service-requests?external_ref=SIN%202026%2F001%20%C3%B1%26x%3D1", _api.Requests[1].Path);

        _api.Reply(ScriptedApi.Json(200, """{ "object": "list", "data": [] }"""));
        Assert.Empty(await NewClient().FindServiceRequestsByExternalRefAsync("TEST-NONE"));
    }

    [Fact]
    public async Task Health_is_sent_without_the_key()
    {
        _api.Reply(ScriptedApi.Json(200, """{"status":"ok","version":"v1"}"""));

        var health = await NewClient().HealthAsync();

        Assert.Equal("ok", health.Status);
        Assert.Equal("v1", health.Version);
        Assert.Equal("/api/v1/health", _api.Requests[0].Path);
        Assert.Null(_api.Requests[0].Header("Authorization"));
    }

    // ── Every documented error ───────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ErrorTable))]
    public async Task Each_documented_error_becomes_its_exception_and_is_not_retried(int status, string code, Type exceptionType)
    {
        _api.Reply(ScriptedApi.Error(status, code, "Mensaje de ejemplo."));

        var error = await Assert.ThrowsAnyAsync<GrualinkApiException>(() => NewClient().CreateServiceRequestAsync(Input));

        Assert.IsType(exceptionType, error);
        Assert.Equal(status, error.Status);
        Assert.Equal(code, error.Code);
        Assert.Equal("Mensaje de ejemplo.", error.Message);
        Assert.Equal(1, error.Attempts);
        Assert.Single(_api.Requests);
        Assert.Empty(_sleeps);
    }

    [Fact]
    public async Task A_422_exposes_the_invalid_fields_in_details()
    {
        var details = new[]
        {
            new Dictionary<string, string> { ["field"] = "contact.phone", ["issue"] = "invalid_format" },
            new Dictionary<string, string> { ["field"] = "pickup", ["issue"] = "invalid_value" },
        };
        _api.Reply(ScriptedApi.Error(422, "validation_error", "Hay campos inválidos.", details));

        var error = await Assert.ThrowsAsync<GrualinkValidationException>(() => NewClient().CreateServiceRequestAsync(Input));

        Assert.Equal(
            new[] { new ErrorDetail("contact.phone", "invalid_format"), new ErrorDetail("pickup", "invalid_value") },
            error.Details);
    }

    [Fact]
    public async Task An_error_body_that_is_not_json_becomes_unexpected_response_without_echoing_the_body()
    {
        _api.Reply(ScriptedApi.Html(404, "<html>proxy page</html>"));

        var error = await Assert.ThrowsAsync<GrualinkNotFoundException>(() => NewClient().GetServiceRequestAsync("abc"));

        Assert.Equal("unexpected_response", error.Code);
        Assert.DoesNotContain("proxy page", error.ToString());
    }

    [Fact]
    public async Task A_success_body_that_is_not_json_becomes_unexpected_response()
    {
        _api.Reply(ScriptedApi.Html(200, "<html>proxy page</html>"));

        var error = await Assert.ThrowsAsync<GrualinkApiException>(() => NewClient().GetServiceRequestAsync("abc"));

        Assert.Equal("unexpected_response", error.Code);
        Assert.Equal(200, error.Status);
        Assert.Single(_api.Requests);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_and_the_key_does_not_travel_to_the_new_address()
    {
        _api.Reply(new ScriptedResponse(302)
        {
            Headers = new Dictionary<string, string> { ["Location"] = $"{_api.BaseUrl}/elsewhere" },
        });

        var error = await Assert.ThrowsAsync<GrualinkApiException>(
            () => NewClient().GetServiceRequestAsync(ScriptedApi.ServiceRequestId));

        Assert.Equal(302, error.Status);
        Assert.Single(_api.Requests);
    }

    // ── Retries ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_429_rate_limited_waits_at_least_retry_after_and_retries_with_the_same_key_and_body()
    {
        _api.Reply(
            ScriptedApi.Error(429, "rate_limited", "Demasiadas peticiones.", null, ("Retry-After", "7")),
            ScriptedApi.Json(201, ScriptedApi.ServiceRequestJson));

        var result = await NewClient().CreateServiceRequestAsync(Input, idempotencyKey: "order-0003");

        Assert.Equal(ScriptedApi.ServiceRequestId, result.ServiceRequest.Id);
        Assert.Equal(2, _api.Requests.Count);

        // 7 s from the header plus half (random = 0.5) of the first backoff step (500 ms).
        Assert.Equal(new double[] { 7250 }, _sleeps);
        var retry = Assert.Single(_retries);
        Assert.Equal(1, retry.Attempt);
        var rateLimit = Assert.IsType<GrualinkRateLimitException>(retry.Error);
        Assert.Equal(7, rateLimit.RetryAfterSeconds);
        Assert.Equal("order-0003", _api.Requests[0].Header("Idempotency-Key"));
        Assert.Equal("order-0003", _api.Requests[1].Header("Idempotency-Key"));
        Assert.Equal(_api.Requests[0].Body, _api.Requests[1].Body);
    }

    [Fact]
    public async Task Retry_after_as_a_date_is_understood_and_capped_at_60_seconds()
    {
        var inTenMinutes = DateTimeOffset.UtcNow.AddMinutes(10).ToString("R");
        _api.Reply(
            ScriptedApi.Error(429, "rate_limited", "Demasiadas peticiones.", null, ("Retry-After", inTenMinutes)),
            ScriptedApi.Json(200, ScriptedApi.ServiceRequestJson));

        await NewClient().GetServiceRequestAsync(ScriptedApi.ServiceRequestId);

        Assert.Equal(new double[] { 60250 }, _sleeps);
    }

    [Fact]
    public async Task A_5xx_is_retried_with_exponential_backoff_and_jitter_then_succeeds()
    {
        _api.Reply(
            ScriptedApi.Error(500, "internal_error"),
            ScriptedApi.Html(502, "<html>bad gateway</html>"),
            ScriptedApi.Error(503, "internal_error"),
            ScriptedApi.Json(201, ScriptedApi.ServiceRequestJson));

        var result = await NewClient().CreateServiceRequestAsync(Input);

        Assert.Equal(ScriptedApi.ServiceRequestId, result.ServiceRequest.Id);
        Assert.Equal(4, _api.Requests.Count);

        // random = 0.5 over ceilings of 500, 1000 and 2000 ms.
        Assert.Equal(new double[] { 250, 500, 1000 }, _sleeps);
        Assert.Single(_api.Requests.Select(r => r.Header("Idempotency-Key")).Distinct());
        Assert.Single(_api.Requests.Select(r => r.BodyText).Distinct());
    }

    [Fact]
    public async Task The_backoff_is_capped_and_the_jitter_comes_from_the_injected_random_source()
    {
        _api.Reply(ScriptedApi.Error(500, "internal_error"));

        await Assert.ThrowsAsync<GrualinkServerException>(
            () => NewClient(maxRetries: 6, random: () => 1).GetServiceRequestAsync(ScriptedApi.ServiceRequestId));

        Assert.Equal(new double[] { 500, 1000, 2000, 4000, 8000, 8000 }, _sleeps);
    }

    [Fact]
    public async Task It_gives_up_after_max_retries_and_reports_how_many_attempts_were_made()
    {
        _api.Reply(ScriptedApi.Error(500, "internal_error", "Error interno."));

        var error = await Assert.ThrowsAsync<GrualinkServerException>(
            () => NewClient(maxRetries: 2).CreateServiceRequestAsync(Input));

        Assert.Equal("internal_error", error.Code);
        Assert.Equal(3, error.Attempts);
        Assert.Equal(3, _api.Requests.Count);
        Assert.Equal(2, _sleeps.Count);
    }

    [Fact]
    public async Task Max_retries_zero_disables_retries()
    {
        _api.Reply(ScriptedApi.Error(500, "internal_error"));

        var error = await Assert.ThrowsAsync<GrualinkServerException>(
            () => NewClient(maxRetries: 0).GetServiceRequestAsync(ScriptedApi.ServiceRequestId));

        Assert.Equal(1, error.Attempts);
        Assert.Single(_api.Requests);
    }

    [Fact]
    public async Task A_network_error_is_retried_and_surfaces_as_a_connection_exception()
    {
        _api.Reply(new ScriptedResponse(0) { Abort = true }, ScriptedApi.Json(200, ScriptedApi.ServiceRequestJson));
        var found = await NewClient().GetServiceRequestAsync(ScriptedApi.ServiceRequestId);
        Assert.Equal(ScriptedApi.ServiceRequestId, found.Id);
        Assert.IsType<GrualinkConnectionException>(Assert.Single(_retries).Error);

        _api.Reply(new ScriptedResponse(0) { Abort = true });
        var error = await Assert.ThrowsAsync<GrualinkConnectionException>(
            () => NewClient(maxRetries: 1).GetServiceRequestAsync(ScriptedApi.ServiceRequestId));
        Assert.Equal(2, error.Attempts);
    }

    [Fact]
    public async Task A_slow_answer_hits_the_timeout_is_retried_and_surfaces_as_a_timeout_exception()
    {
        _api.Reply(ScriptedApi.Json(200, ScriptedApi.ServiceRequestJson) with { Delay = TimeSpan.FromSeconds(5) });

        var error = await Assert.ThrowsAsync<GrualinkTimeoutException>(
            () => NewClient(maxRetries: 1, timeout: TimeSpan.FromMilliseconds(400))
                .GetServiceRequestAsync(ScriptedApi.ServiceRequestId));

        Assert.Equal(2, error.Attempts);
        Assert.Equal(2, _api.Requests.Count);
        Assert.Contains("400 ms", error.Message);
    }

    [Fact]
    public async Task Cancelling_the_token_stops_the_call_and_is_not_retried()
    {
        _api.Reply(ScriptedApi.Json(200, ScriptedApi.ServiceRequestJson) with { Delay = TimeSpan.FromSeconds(5) });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewClient().GetServiceRequestAsync(ScriptedApi.ServiceRequestId, cancellation.Token));

        Assert.Single(_api.Requests);
        Assert.Empty(_sleeps);
    }

    // ── Local validation: nothing is sent ────────────────────────────────────────────────────────

    [Fact]
    public async Task Invalid_configuration_and_arguments_fail_before_any_request()
    {
        Assert.Throws<GrualinkConfigException>(() => new GrualinkClient(new GrualinkClientOptions { ApiKey = "" }));
        Assert.Throws<GrualinkConfigException>(() => new GrualinkClient(new GrualinkClientOptions { ApiKey = "not-a-key" }));
        Assert.Throws<GrualinkConfigException>(() => new GrualinkClient(new GrualinkClientOptions { ApiKey = "glk_test_" }));
        Assert.Throws<GrualinkConfigException>(() => new GrualinkClient(new GrualinkClientOptions { ApiKey = "glk_test_FAKE WITH SPACE" }));
        Assert.Throws<GrualinkConfigException>(
            () => new GrualinkClient(new GrualinkClientOptions { ApiKey = TestKey, BaseUrl = "http://example.com/api/v1" }));
        Assert.Throws<GrualinkConfigException>(
            () => new GrualinkClient(new GrualinkClientOptions { ApiKey = TestKey, BaseUrl = $"https://example.com/api/v1?key={TestKey}" }));
        Assert.Throws<GrualinkConfigException>(
            () => new GrualinkClient(new GrualinkClientOptions { ApiKey = TestKey, BaseUrl = "not a url" }));
        Assert.Throws<GrualinkConfigException>(
            () => new GrualinkClient(new GrualinkClientOptions { ApiKey = TestKey, Timeout = TimeSpan.Zero }));
        Assert.Throws<GrualinkConfigException>(
            () => new GrualinkClient(new GrualinkClientOptions { ApiKey = TestKey, MaxRetries = -1 }));

        foreach (var idempotencyKey in new[] { "", "with space", "ñ", new string('x', 256) })
        {
            await Assert.ThrowsAsync<GrualinkConfigException>(
                () => NewClient().CreateServiceRequestAsync(Input, idempotencyKey));
        }

        var tooLarge = Input with { Notes = new string('x', 17 * 1024) };
        await Assert.ThrowsAsync<GrualinkConfigException>(() => NewClient().CreateServiceRequestAsync(tooLarge));
        await Assert.ThrowsAsync<GrualinkConfigException>(() => NewClient().GetServiceRequestAsync(" "));
        await Assert.ThrowsAsync<GrualinkConfigException>(() => NewClient().FindServiceRequestsByExternalRefAsync(""));
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public void A_bad_key_is_never_echoed_by_the_configuration_exception()
    {
        const string Almost = "glk_prod_FAKE_BUT_SHOULD_NOT_BE_ECHOED";

        var error = Assert.Throws<GrualinkConfigException>(() => new GrualinkClient(new GrualinkClientOptions { ApiKey = Almost }));

        Assert.DoesNotContain(Almost, error.ToString());
    }

    [Fact]
    public void The_default_base_url_is_the_public_one_and_the_mode_comes_from_the_key()
    {
        using var test = new GrualinkClient(new GrualinkClientOptions { ApiKey = TestKey });
        using var live = new GrualinkClient(new GrualinkClientOptions { ApiKey = "glk_live_FAKE_KEY_FOR_UNIT_TESTS_ONLY" });

        Assert.Equal("https://grualink.mx/api/v1", GrualinkClient.DefaultBaseUrl);
        Assert.Equal("test", test.Mode);
        Assert.Equal("live", live.Mode);
        Assert.Contains("https://grualink.mx/api/v1", test.ToString());
    }

    // ── The key never leaks ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_api_key_never_appears_in_exceptions_logs_or_the_client_itself()
    {
        var written = new StringWriter();
        var errors = new List<Exception>();
        var client = NewClient(maxRetries: 1, onRetry: info => written.WriteLine($"retrying {info}"));
        written.WriteLine(client);
        written.WriteLine($"{client}");
        written.WriteLine(JsonSerializer.Serialize(client));

        // A server that (wrongly) echoes the Authorization header back in every field of the error.
        ScriptedResponse Echo(RecordedRequest request, int status)
        {
            var authorization = request.Header("Authorization");
            var details = new[]
            {
                new Dictionary<string, string> { ["field"] = $"field {authorization}", ["issue"] = $"issue {authorization}" },
            };
            return ScriptedApi.Error(status, $"code {authorization}", $"message {authorization}", details);
        }

        foreach (var status in new[] { 400, 401, 403, 404, 409, 422, 429, 500 })
        {
            _api.Reply(request => Echo(request, status));
            errors.Add(await Assert.ThrowsAnyAsync<GrualinkApiException>(() => client.CreateServiceRequestAsync(Input)));
        }

        _api.Reply(new ScriptedResponse(0) { Abort = true });
        errors.Add(await Assert.ThrowsAsync<GrualinkConnectionException>(
            () => client.GetServiceRequestAsync(ScriptedApi.ServiceRequestId)));

        _api.Reply(ScriptedApi.Json(200, ScriptedApi.ServiceRequestJson) with { Delay = TimeSpan.FromSeconds(5) });
        errors.Add(await Assert.ThrowsAsync<GrualinkTimeoutException>(
            () => NewClient(maxRetries: 0, timeout: TimeSpan.FromMilliseconds(300))
                .GetServiceRequestAsync(ScriptedApi.ServiceRequestId)));

        // A transport that throws an exception mentioning the key.
        using var leaky = new ThrowingHandler(new HttpRequestException($"connect failed for Bearer {TestKey}"));
        errors.Add(await Assert.ThrowsAsync<GrualinkConnectionException>(
            () => NewClient(maxRetries: 0, handler: leaky).GetServiceRequestAsync(ScriptedApi.ServiceRequestId)));

        Assert.Equal(11, errors.Count);
        Assert.NotEmpty(_retries);
        foreach (var error in errors)
        {
            written.WriteLine(error);
            written.WriteLine(error.Message);
            if (error is GrualinkApiException api)
            {
                Assert.Contains("[REDACTED]", api.Code);
                written.WriteLine($"{api.Status} {api.Code} {string.Join(", ", api.Details)}");
            }
        }

        var output = written.ToString();
        Assert.Contains("[REDACTED]", output);
        Assert.Contains("GrualinkClient(mode=test", output);
        Assert.DoesNotContain(TestKey, output);
        Assert.DoesNotContain("FAKE_KEY_FOR_UNIT_TESTS", output);

        // The requests did carry the key: the check above is not passing because nothing was sent.
        Assert.Equal($"Bearer {TestKey}", _api.Requests[0].Header("Authorization"));
    }

    private GrualinkClient NewClient(
        int maxRetries = GrualinkClient.DefaultMaxRetries,
        TimeSpan? timeout = null,
        Func<double>? random = null,
        Action<RetryInfo>? onRetry = null,
        HttpMessageHandler? handler = null)
    {
        var client = new GrualinkClient(new GrualinkClientOptions
        {
            ApiKey = TestKey,
            BaseUrl = _api.BaseUrl,
            MaxRetries = maxRetries,
            Timeout = timeout ?? GrualinkClient.DefaultTimeout,
            Random = random ?? (() => 0.5),
            HttpMessageHandler = handler,
            Sleep = (delay, _) =>
            {
                _sleeps.Add(delay.TotalMilliseconds);
                return Task.CompletedTask;
            },
            OnRetry = info =>
            {
                _retries.Add(info);
                onRetry?.Invoke(info);
            },
        });
        _clients.Add(client);
        return client;
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
