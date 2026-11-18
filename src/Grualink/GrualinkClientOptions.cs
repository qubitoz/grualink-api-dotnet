namespace Grualink;

/// <summary>What the client reports before each wait between attempts.</summary>
/// <param name="Attempt">Attempt that just failed, starting at 1.</param>
/// <param name="Delay">Time the client will wait before the next attempt.</param>
/// <param name="Error">Why it failed. Never contains the API key.</param>
public sealed record RetryInfo(int Attempt, TimeSpan Delay, GrualinkException Error);

/// <summary>Configuration of <see cref="GrualinkClient"/>.</summary>
public sealed class GrualinkClientOptions
{
    /// <summary><c>glk_test_...</c> or <c>glk_live_...</c>. Read it from the environment; never hard-code it.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Defaults to <c>https://grualink.mx/api/v1</c>.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Maximum time for each attempt (default 15 seconds).</summary>
    public TimeSpan Timeout { get; init; } = GrualinkClient.DefaultTimeout;

    /// <summary>Retries after the first attempt for 429 <c>rate_limited</c>, 5xx, timeouts and network errors (default 3).</summary>
    public int MaxRetries { get; init; } = GrualinkClient.DefaultMaxRetries;

    /// <summary>First backoff step (default 500 ms).</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Ceiling of the exponential backoff (default 8 seconds).</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Called before each wait. Use it for your own logging.</summary>
    public Action<RetryInfo>? OnRetry { get; init; }

    /// <summary>
    /// Transport to use instead of the built-in one (for tests, or to add a proxy). It must not follow
    /// redirects: the key must not travel to another address. The client does not dispose it.
    /// </summary>
    public HttpMessageHandler? HttpMessageHandler { get; init; }

    /// <summary>Replaces the wait between attempts. For tests.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Sleep { get; init; }

    /// <summary>Source of the jitter, a number in [0, 1). For tests.</summary>
    public Func<double>? Random { get; init; }
}
