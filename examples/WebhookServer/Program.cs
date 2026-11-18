// Example webhook receiver with ASP.NET Core Minimal API.
//
//   GRUALINK_WEBHOOK_SECRET=<your whsec_ secret> dotnet run --project examples/WebhookServer
//
// Expose it over https (GRUALINK only delivers to https addresses on port 443) and give that address to the
// towing company. Ask them to send a test event (webhook.test) to check the connection.
using System.Collections.Concurrent;
using System.Globalization;
using Grualink;
using Grualink.Examples.WebhookServer;

var secrets = new[] { "GRUALINK_WEBHOOK_SECRET", "GRUALINK_WEBHOOK_SECRET_PREVIOUS" }
    .Select(name => Environment.GetEnvironmentVariable(name))
    .OfType<string>()
    .Where(secret => secret.Length > 0)
    .ToArray();
if (secrets.Length == 0)
{
    Console.Error.WriteLine("Set GRUALINK_WEBHOOK_SECRET to your signing secret (it starts with whsec_). See .env.example.");
    return 1;
}

var port = 8787;
if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), NumberStyles.None, CultureInfo.InvariantCulture, out var requested)
    && requested > 0)
{
    port = requested;
}

// Stand-in for your own queue or database. Answer fast and do the real work elsewhere.
var queue = new ConcurrentQueue<WebhookEvent>();

var app = WebhookApp.Build(
    new WebhookHandlerOptions
    {
        Secrets = secrets,
        OnEvent = (webhookEvent, _) =>
        {
            queue.Enqueue(webhookEvent);
            var serviceRequest = webhookEvent.Data.ServiceRequest;
            Console.WriteLine(serviceRequest is null
                ? $"[{webhookEvent.Mode}] {webhookEvent.Type}: {webhookEvent.Data.Message}"
                : $"[{webhookEvent.Mode}] {webhookEvent.Type}: request {serviceRequest.Id} ({serviceRequest.ExternalRef ?? "no reference"}) is now \"{serviceRequest.Status}\"");
            return Task.CompletedTask;
        },
    },
    onResult: (result, deliveryId) =>
    {
        // The reason stays in your logs; the caller only sees a generic answer.
        if (result.Reason != SignatureVerificationReason.Ok)
        {
            Console.Error.WriteLine($"Delivery {deliveryId ?? "-"}: {result.Status} ({result.Reason})");
        }
    },
    args: args);

app.Urls.Add($"http://localhost:{port}");
Console.WriteLine($"Listening for GRUALINK webhooks on http://localhost:{port}{WebhookApp.Path}");
await app.RunAsync();
return 0;
