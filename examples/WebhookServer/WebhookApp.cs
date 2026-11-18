using Microsoft.Extensions.Primitives;

namespace Grualink.Examples.WebhookServer;

/// <summary>
/// Webhook receiver with ASP.NET Core Minimal API. The one delicate point is the RAW body: the route reads
/// the bytes itself and never binds a model, because a body that was parsed and serialized again no longer
/// matches its signature.
/// </summary>
public static class WebhookApp
{
    public const string Path = "/webhooks/grualink";

    /// <param name="options">Secrets, your event handler and, optionally, your deduplicator.</param>
    /// <param name="onResult">
    /// Called after each delivery with the outcome and its id, for your logs. The body and the secret are never passed.
    /// </param>
    /// <param name="args">Command line arguments, passed to the host.</param>
    public static WebApplication Build(
        WebhookHandlerOptions options,
        Action<WebhookHandlerResult, string?>? onResult = null,
        string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();

        var handler = Webhooks.CreateWebhookHandler(options);

        // Only POST on this path is served: the router answers 405 to other methods and 404 to other paths.
        app.MapPost(Path, async (HttpContext context) =>
        {
            var request = context.Request;
            var rawBody = await Webhooks.ReadRawBodyAsync(
                request.Body, request.ContentLength, Webhooks.MaxWebhookBodyBytes, context.RequestAborted);
            if (rawBody is null)
            {
                await WriteJsonAsync(context, 413, new WebhookResponseBody(false, Error: "payload_too_large"));
                return;
            }

            var deliveryId = First(request.Headers[Webhooks.DeliveryHeader]);
            var result = await handler.HandleAsync(
                rawBody, First(request.Headers[Webhooks.SignatureHeader]), deliveryId, context.RequestAborted);

            // The caller only sees a generic answer; the reason stays on your side.
            onResult?.Invoke(result, deliveryId);
            await WriteJsonAsync(context, result.Status, result.Body);
        });

        return app;
    }

    private static string? First(StringValues values) => values.Count > 0 ? values[0] : null;

    private static Task WriteJsonAsync(HttpContext context, int status, WebhookResponseBody body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(body.ToJson(), context.RequestAborted);
    }
}
