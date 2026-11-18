using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Grualink;

/// <summary>
/// Minimal client of the GRUALINK API: create a service request, read it, find it by your reference.
/// Thread-safe; create one and reuse it.
/// </summary>
public sealed class GrualinkClient : IDisposable
{
    public const string DefaultBaseUrl = "https://grualink.mx/api/v1";
    public const int DefaultMaxRetries = 3;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private const string TestKeyPrefix = "glk_test_";
    private const string LiveKeyPrefix = "glk_live_";
    private const int MaxBodyBytes = 16 * 1024;

    // The API never asks to wait more than 60 seconds.
    private const int MaxRetryAfterSeconds = 60;

    // Private and never exposed: not in ToString, not in exceptions, not in what OnRetry receives.
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly TimeSpan _timeout;
    private readonly int _maxRetries;
    private readonly TimeSpan _retryBaseDelay;
    private readonly TimeSpan _retryMaxDelay;
    private readonly Action<RetryInfo>? _onRetry;
    private readonly Func<TimeSpan, CancellationToken, Task> _sleep;
    private readonly Func<double> _random;
    private readonly HttpClient _http;

    public GrualinkClient(GrualinkClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var apiKey = (options.ApiKey ?? "").Trim();
        if (apiKey.Length == 0)
        {
            throw new GrualinkConfigException("ApiKey is required (set GRUALINK_API_KEY).");
        }

        if (!IsApiKey(apiKey))
        {
            // The value is never echoed: it may be a real key with a typo.
            throw new GrualinkConfigException("ApiKey must start with glk_test_ or glk_live_.");
        }

        var baseUrl = (options.BaseUrl ?? DefaultBaseUrl).TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed))
        {
            throw new GrualinkConfigException("BaseUrl is not a valid URL.");
        }

        var local = parsed.Host is "localhost" or "127.0.0.1" or "[::1]";
        if (parsed.Scheme != Uri.UriSchemeHttps && !(parsed.Scheme == Uri.UriSchemeHttp && local))
        {
            throw new GrualinkConfigException("BaseUrl must use https (http is only accepted for localhost).");
        }

        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0 || parsed.UserInfo.Length > 0)
        {
            throw new GrualinkConfigException("BaseUrl must not contain credentials, query or fragment.");
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new GrualinkConfigException("Timeout must be positive.");
        }

        if (options.MaxRetries < 0)
        {
            throw new GrualinkConfigException("MaxRetries must not be negative.");
        }

        if (options.RetryBaseDelay <= TimeSpan.Zero || options.RetryMaxDelay <= TimeSpan.Zero)
        {
            throw new GrualinkConfigException("RetryBaseDelay and RetryMaxDelay must be positive.");
        }

        _apiKey = apiKey;
        _baseUrl = baseUrl;
        _timeout = options.Timeout;
        _maxRetries = options.MaxRetries;
        _retryBaseDelay = options.RetryBaseDelay;
        _retryMaxDelay = options.RetryMaxDelay;
        _onRetry = options.OnRetry;
        _sleep = options.Sleep ?? ((delay, token) => Task.Delay(delay, token));
        _random = options.Random ?? (() => System.Random.Shared.NextDouble());

        // A redirect is never followed: the key must not travel to another address.
        // The timeout is applied per attempt, below; the HttpClient one is switched off.
        var handler = options.HttpMessageHandler
            ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
        _http = new HttpClient(handler, disposeHandler: options.HttpMessageHandler is null)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary><c>test</c> or <c>live</c>, from the key prefix.</summary>
    public string Mode => _apiKey.StartsWith(LiveKeyPrefix, StringComparison.Ordinal) ? "live" : "test";

    /// <summary><c>GET /health</c>. Sent without the key.</summary>
    public async Task<HealthStatus> HealthAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<HealthStatus>(
            HttpMethod.Get, "/health", body: null, idempotencyKey: null, authenticated: false, cancellationToken)
            .ConfigureAwait(false);
        return response.Value;
    }

    /// <summary>
    /// <c>POST /service-requests</c>. The body is serialized once and the exact same bytes and
    /// Idempotency-Key are reused on every retry, so a retry can never create a second request.
    /// </summary>
    /// <param name="input">The service request to send.</param>
    /// <param name="idempotencyKey">
    /// 1 to 255 visible ASCII characters, unique per request you want to create. Store it with your record
    /// before calling: sending it again with the same body returns the original response instead of a
    /// duplicate. Generated (UUID v4) when omitted.
    /// </param>
    /// <param name="cancellationToken">Cancels the call, including the waits between attempts.</param>
    public async Task<CreateServiceRequestResult> CreateServiceRequestAsync(
        ServiceRequestInput input,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var key = idempotencyKey ?? Guid.NewGuid().ToString();
        if (!IsIdempotencyKey(key))
        {
            throw new GrualinkConfigException("idempotencyKey must be 1 to 255 visible ASCII characters.");
        }

        if (input is null)
        {
            throw new GrualinkConfigException("The service request is required.");
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(input, GrualinkJson.Options);
        if (body.Length > MaxBodyBytes)
        {
            throw new GrualinkConfigException("The service request exceeds 16 KB.");
        }

        var response = await SendAsync<ServiceRequest>(
            HttpMethod.Post, "/service-requests", body, key, authenticated: true, cancellationToken)
            .ConfigureAwait(false);
        return new CreateServiceRequestResult(response.Value, response.Status == 200 && response.Replayed, key);
    }

    /// <summary><c>GET /service-requests/{id}</c>.</summary>
    public async Task<ServiceRequest> GetServiceRequestAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new GrualinkConfigException("id is required.");
        }

        var path = "/service-requests/" + Uri.EscapeDataString(id.Trim());
        var response = await SendAsync<ServiceRequest>(
            HttpMethod.Get, path, body: null, idempotencyKey: null, authenticated: true, cancellationToken)
            .ConfigureAwait(false);
        return response.Value;
    }

    /// <summary>
    /// <c>GET /service-requests?external_ref=...</c>: up to 20 of your requests with that reference, newest first.
    /// </summary>
    public async Task<IReadOnlyList<ServiceRequest>> FindServiceRequestsByExternalRefAsync(
        string externalRef,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalRef))
        {
            throw new GrualinkConfigException("externalRef is required.");
        }

        var path = "/service-requests?external_ref=" + Uri.EscapeDataString(externalRef);
        var response = await SendAsync<ServiceRequestList>(
            HttpMethod.Get, path, body: null, idempotencyKey: null, authenticated: true, cancellationToken)
            .ConfigureAwait(false);
        return response.Value.Data ?? [];
    }

    /// <summary>Never shows the key.</summary>
    public override string ToString() => $"GrualinkClient(mode={Mode}, baseUrl={_baseUrl})";

    public void Dispose() => _http.Dispose();

    private static bool IsApiKey(string value)
    {
        if (!value.StartsWith(TestKeyPrefix, StringComparison.Ordinal)
            && !value.StartsWith(LiveKeyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        // Visible ASCII only, so the value is always a valid header.
        return value.Length > TestKeyPrefix.Length && value.All(c => c is >= '\x21' and <= '\x7e');
    }

    private static bool IsIdempotencyKey(string value) =>
        value.Length is >= 1 and <= 255 && value.All(c => c is >= '\x21' and <= '\x7e');

    private static bool IsRetryable(GrualinkException error) => error switch
    {
        GrualinkConnectionException or GrualinkTimeoutException or GrualinkServerException => true,
        // too_many_open_requests only clears when the company resolves a request: waiting a few seconds does not help.
        GrualinkRateLimitException rateLimit => rateLimit.Code != "too_many_open_requests",
        _ => false,
    };

    private static int? ParseRetryAfter(string? value, DateTimeOffset now)
    {
        if (value is null)
        {
            return null;
        }

        var text = value.Trim();
        if (text.Length is >= 1 and <= 9 && text.All(c => c is >= '0' and <= '9'))
        {
            return int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return null;
        }

        return (int)Math.Clamp(Math.Ceiling((date - now).TotalSeconds), 0, int.MaxValue);
    }

    private static string? HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.NonValidated.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string DetailText(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return "";
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
    }

    /// <summary>Replaces the API key with a marker in any text that will leave this class.</summary>
    private string Redact(string text) => text.Replace(_apiKey, "[REDACTED]", StringComparison.Ordinal);

    private async Task<ApiResponse<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        byte[]? body,
        string? idempotencyKey,
        bool authenticated,
        CancellationToken cancellationToken)
        where T : class
    {
        var url = _baseUrl + path;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            GrualinkException failure;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            try
            {
                using var request = new HttpRequestMessage(method, url);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (authenticated)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                }

                if (body is not null)
                {
                    // The same array on every attempt: the bytes of a retry are the bytes of the first call.
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
                }

                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
                var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;

                if (status is >= 200 and < 300)
                {
                    var value = Deserialize<T>(bytes);
                    if (value is not null)
                    {
                        var replayed = HeaderValue(response, "Idempotent-Replayed") == "true";
                        return new ApiResponse<T>(value, status, replayed);
                    }

                    failure = new GrualinkApiException(
                        status, "unexpected_response", "The API answered with a body that is not the expected JSON.");
                }
                else
                {
                    failure = ToApiException(response, status, bytes);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller cancelled: that is not a timeout and is never retried.
                throw;
            }
            catch (OperationCanceledException)
            {
                failure = new GrualinkTimeoutException(
                    $"No response from the GRUALINK API within {_timeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms.");
            }
            catch (Exception cause) when (cause is HttpRequestException or IOException)
            {
                failure = new GrualinkConnectionException(Redact($"Could not reach the GRUALINK API: {cause.Message}"));
            }

            failure.Attempts = attempt;
            if (!IsRetryable(failure) || attempt > _maxRetries)
            {
                throw failure;
            }

            var delay = RetryDelay(attempt, failure);
            _onRetry?.Invoke(new RetryInfo(attempt, delay, failure));
            await _sleep(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static T? Deserialize<T>(byte[] bytes)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, GrualinkJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Exponential backoff with full jitter; <c>Retry-After</c> is a floor when the API sends it.</summary>
    private TimeSpan RetryDelay(int attempt, GrualinkException failure)
    {
        var ceiling = Math.Min(
            _retryMaxDelay.TotalMilliseconds,
            _retryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        var jitter = _random() * ceiling;
        var retryAfter = (failure as GrualinkApiException)?.RetryAfterSeconds;
        var milliseconds = retryAfter is null
            ? jitter
            : (Math.Min(retryAfter.Value, MaxRetryAfterSeconds) * 1000.0) + jitter;
        return TimeSpan.FromMilliseconds(Math.Round(milliseconds, MidpointRounding.AwayFromZero));
    }

    private GrualinkApiException ToApiException(HttpResponseMessage response, int status, byte[] bytes)
    {
        var retryAfterSeconds = ParseRetryAfter(HeaderValue(response, "Retry-After"), DateTimeOffset.UtcNow);
        var code = "unexpected_response";
        var message = $"The API answered HTTP {status.ToString(CultureInfo.InvariantCulture)}.";
        List<ErrorDetail>? details = null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var codeElement)
                && codeElement.ValueKind == JsonValueKind.String)
            {
                code = Redact(codeElement.GetString() ?? "");
                if (error.TryGetProperty("message", out var messageElement)
                    && messageElement.ValueKind == JsonValueKind.String)
                {
                    message = Redact(messageElement.GetString() ?? "");
                }

                if (error.TryGetProperty("details", out var detailsElement)
                    && detailsElement.ValueKind == JsonValueKind.Array)
                {
                    details = [];
                    foreach (var item in detailsElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            details.Add(new ErrorDetail(
                                Redact(DetailText(item, "field")),
                                Redact(DetailText(item, "issue"))));
                        }
                    }
                }
            }
        }
        catch (Exception cause) when (cause is JsonException or InvalidOperationException)
        {
            // Not JSON (for example an HTML page from a proxy): keep the generic message; the body is not echoed.
        }

        return GrualinkApiException.FromStatus(status, code, message, details, retryAfterSeconds);
    }

    private readonly record struct ApiResponse<T>(T Value, int Status, bool Replayed);
}
