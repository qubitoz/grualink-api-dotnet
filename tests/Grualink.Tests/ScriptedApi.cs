using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Grualink.Tests;

internal sealed record RecordedRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body);

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

internal sealed record ScriptedResponse(int Status)
{
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public string? Body { get; init; }

    public string ContentType { get; init; } = "application/json; charset=utf-8";

    /// <summary>Wait before answering (to exercise the timeout).</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>Close the connection without answering (to exercise network errors).</summary>
    public bool Abort { get; init; }
}

/// <summary>A local stand-in for the API: records what it receives and answers what the test queued.</summary>
internal sealed class ScriptedApi : IAsyncDisposable
{
    public const string ServiceRequestId = "11111111-2222-4333-8444-555555555555";

    public const string ServiceRequestJson = """
        {
          "id": "11111111-2222-4333-8444-555555555555",
          "object": "service_request",
          "status": "quoted",
          "mode": "test",
          "external_ref": "TEST-CONFIRMAR-0001",
          "created_at": "2026-11-18T10:00:00.000Z",
          "resolved_at": null,
          "rejection_reason": null,
          "service": null,
          "quote": {
            "estimated_price": {
              "currency": "MXN",
              "subtotal": 864.33,
              "taxes": [{ "name": "IVA", "type": "tax", "rate": 16, "amount": 138.29 }],
              "tax_total": 138.29,
              "retention_total": 0,
              "total": 1002.62
            },
            "includes_extras": false,
            "approximate_distance": true,
            "unpriced_reason": null,
            "note": "Precio estimado.",
            "a_field_added_later": "ignored"
          },
          "assignment": null,
          "timeline": null
        }
        """;

    private readonly WebApplication _app;
    private readonly object _gate = new();
    private readonly List<RecordedRequest> _requests = [];
    private Func<RecordedRequest, ScriptedResponse>[] _queue = [];
    private int _next;

    private ScriptedApi(WebApplication app)
    {
        _app = app;
    }

    public string BaseUrl { get; private set; } = "";

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public static async Task<ScriptedApi> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var api = new ScriptedApi(app);
        app.Run(api.HandleAsync);
        await app.StartAsync();
        api.BaseUrl = app.Urls.First().TrimEnd('/') + "/api/v1";
        return api;
    }

    public static ScriptedResponse Json(int status, string json, params (string Name, string Value)[] headers) =>
        new(status) { Body = json, Headers = headers.ToDictionary(h => h.Name, h => h.Value) };

    public static ScriptedResponse Html(int status, string html) =>
        new(status) { Body = html, ContentType = "text/html" };

    public static ScriptedResponse Error(
        int status,
        string code,
        string message = "Mensaje de ejemplo.",
        object? details = null,
        params (string Name, string Value)[] headers)
    {
        var error = new Dictionary<string, object?> { ["code"] = code, ["message"] = message };
        if (details is not null)
        {
            error["details"] = details;
        }

        return Json(status, JsonSerializer.Serialize(new Dictionary<string, object?> { ["error"] = error }), headers);
    }

    /// <summary>Queue answers; the last one repeats once the queue is exhausted.</summary>
    public void Reply(params ScriptedResponse[] responses) =>
        Reply(responses.Select(response => (Func<RecordedRequest, ScriptedResponse>)(_ => response)).ToArray());

    public void Reply(params Func<RecordedRequest, ScriptedResponse>[] responses)
    {
        lock (_gate)
        {
            _queue = responses;
            _next = 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _app.StopAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
            // A request that was left waiting on purpose; the host is disposed right below.
        }

        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);

        var recorded = new RecordedRequest(
            context.Request.Method,
            context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path,
            context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            buffer.ToArray());

        Func<RecordedRequest, ScriptedResponse>? next;
        lock (_gate)
        {
            _requests.Add(recorded);
            next = _queue.Length == 0 ? null : _queue[Math.Min(_next, _queue.Length - 1)];
            _next++;
        }

        var answer = next?.Invoke(recorded) ?? Html(500, "no reply queued");
        if (answer.Abort)
        {
            context.Abort();
            return;
        }

        try
        {
            if (answer.Delay > TimeSpan.Zero)
            {
                await Task.Delay(answer.Delay, context.RequestAborted);
            }

            context.Response.StatusCode = answer.Status;
            context.Response.ContentType = answer.ContentType;
            foreach (var (name, value) in answer.Headers ?? new Dictionary<string, string>())
            {
                context.Response.Headers[name] = value;
            }

            await context.Response.WriteAsync(answer.Body ?? "", context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // The client gave up (timeout tests).
        }
    }
}
