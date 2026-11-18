// A local stand-in for the API, used by CI to run the examples without a key and without the network.
// It only imitates what the examples need (see contract/API.md); it is not the API.
//
//   PORT=18081 dotnet run --project tests/FakeApi
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;

var port = 18081;
if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), NumberStyles.None, CultureInfo.InvariantCulture, out var requested)
    && requested > 0)
{
    port = requested;
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();
app.Urls.Add($"http://127.0.0.1:{port}");

var byId = new ConcurrentDictionary<string, JsonObject>();
var byIdempotencyKey = new ConcurrentDictionary<string, (string Body, string Id)>();

app.MapGet("/api/v1/health", () => Results.Json(new { status = "ok", version = "v1" }));

app.MapPost("/api/v1/service-requests", async (HttpContext context) =>
{
    if (!HasTestKey(context))
    {
        return Error(401, "unauthorized", "Llave inválida.");
    }

    string idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
    if (idempotencyKey.Length == 0)
    {
        return Error(400, "idempotency_key_required", "Falta Idempotency-Key.");
    }

    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync(context.RequestAborted);

    JsonObject? input;
    try
    {
        input = JsonNode.Parse(body) as JsonObject;
    }
    catch (System.Text.Json.JsonException)
    {
        input = null;
    }

    if (input is null)
    {
        return Error(400, "invalid_json", "El cuerpo no es JSON válido.");
    }

    if (byIdempotencyKey.TryGetValue(idempotencyKey, out var previous))
    {
        if (previous.Body != body)
        {
            return Error(409, "idempotency_conflict", "Esa Idempotency-Key ya se usó con otro cuerpo.");
        }

        context.Response.Headers["Idempotent-Replayed"] = "true";
        return Results.Json(byId[previous.Id], statusCode: 200);
    }

    var externalRef = input["external_ref"]?.GetValue<string>();
    var id = Guid.NewGuid().ToString();
    var created = new JsonObject
    {
        ["id"] = id,
        ["object"] = "service_request",
        ["status"] = StatusFor(externalRef),
        ["mode"] = "test",
        ["external_ref"] = externalRef,
        ["created_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        ["resolved_at"] = null,
        ["rejection_reason"] = null,
        ["service"] = null,
        ["quote"] = new JsonObject
        {
            ["estimated_price"] = new JsonObject
            {
                ["currency"] = "MXN",
                ["subtotal"] = 864.33,
                ["taxes"] = new JsonArray(new JsonObject
                {
                    ["name"] = "IVA",
                    ["type"] = "tax",
                    ["rate"] = 16,
                    ["amount"] = 138.29,
                }),
                ["tax_total"] = 138.29,
                ["retention_total"] = 0,
                ["total"] = 1002.62,
            },
            ["includes_extras"] = false,
            ["approximate_distance"] = true,
            ["unpriced_reason"] = null,
            ["note"] = "Precio estimado.",
        },
        ["assignment"] = null,
        ["timeline"] = null,
    };

    byId[id] = created;
    byIdempotencyKey[idempotencyKey] = (body, id);
    return Results.Json(created, statusCode: 201);
});

app.MapGet("/api/v1/service-requests/{id}", (string id, HttpContext context) =>
{
    if (!HasTestKey(context))
    {
        return Error(401, "unauthorized", "Llave inválida.");
    }

    return byId.TryGetValue(id, out var found)
        ? Results.Json(found)
        : Error(404, "not_found", "No existe o no es tuya.");
});

app.MapGet("/api/v1/service-requests", (HttpContext context) =>
{
    if (!HasTestKey(context))
    {
        return Error(401, "unauthorized", "Llave inválida.");
    }

    string externalRef = context.Request.Query["external_ref"].ToString();
    if (externalRef.Length == 0)
    {
        return Error(400, "invalid_request", "Falta external_ref.");
    }

    var data = new JsonArray();
    foreach (var item in byId.Values.Where(item => item["external_ref"]?.GetValue<string>() == externalRef))
    {
        data.Add(item.DeepClone());
    }

    return Results.Json(new JsonObject { ["object"] = "list", ["data"] = data });
});

await app.RunAsync();

static bool HasTestKey(HttpContext context) =>
    context.Request.Headers.Authorization.ToString().StartsWith("Bearer glk_test_", StringComparison.Ordinal);

static IResult Error(int status, string code, string message) =>
    Results.Json(new { error = new { code, message } }, statusCode: status);

static string StatusFor(string? externalRef) => externalRef switch
{
    not null when externalRef.StartsWith("TEST-CONFIRMAR", StringComparison.Ordinal) => "confirmed",
    not null when externalRef.StartsWith("TEST-EN-CURSO", StringComparison.Ordinal) => "in_progress",
    not null when externalRef.StartsWith("TEST-TERMINAR", StringComparison.Ordinal) => "completed",
    not null when externalRef.StartsWith("TEST-RECHAZAR", StringComparison.Ordinal) => "rejected",
    not null when externalRef.StartsWith("TEST-CANCELAR", StringComparison.Ordinal) => "cancelled",
    _ => "quoted",
};
