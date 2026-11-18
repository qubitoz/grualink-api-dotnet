# GRUALINK API: C# / .NET example

[Español](README.md)

A clone-and-run example that integrates your system (insurer, roadside assistance, integrator) with the
GRUALINK API: **request a tow truck**, **read its status** and **receive signed webhooks**.

Public API guide: <https://grualink.mx/api>

What is inside:

- A **minimal client** (`src/Grualink/GrualinkClient.cs`): creates requests with `Idempotency-Key`, reads
  the status, typed exceptions, retries with exponential backoff, a timeout and `CancellationToken`.
  Only `HttpClient` and `System.Text.Json`: no runtime dependencies.
- **Webhook verification** (`src/Grualink/Webhooks.cs`, `WebhookHandler.cs`): verifies the signature
  over the raw body, checks the time window, supports secret rotation and drops duplicates.
- Two **runnable examples** (`examples/`): request a tow truck, and a webhook receiver with ASP.NET Core
  Minimal API.
- The **contract kit** (`contract/`): the API contract in Spanish and English and the signature test
  cases shared by the examples in every language.

It is not an SDK: it is code meant to be read and copied into your project.

## Requirements

- .NET 8 (LTS). SDK 8.0 to build.
- A **test key** (`glk_test_…`). The towing company you work with gives it to you.
- For webhooks: the **signing secret** (`whsec_…`) the same company gives you when it registers the
  address of your system.

## Installation

```bash
git clone <the address of this repository>
cd grualink-api-dotnet
dotnet build
```

## Environment variables

Names only; the values live on your machine and never in the repository. There is a template in
`.env.example` (.NET does not read it by itself: export the variables or use your secret manager).

| Variable | What for |
|---|---|
| `GRUALINK_API_KEY` | Your **test** key (`glk_test_…`) |
| `GRUALINK_WEBHOOK_SECRET` | Webhook signing secret (`whsec_…`), whole, with its prefix |
| `GRUALINK_WEBHOOK_SECRET_PREVIOUS` | Optional: the previous secret while a rotation is in progress |
| `GRUALINK_API_BASE_URL` | Optional: another base URL (default `https://grualink.mx/api/v1`) |
| `PORT` | Optional: port of the example receiver (default 8787) |

## Running the examples

### Request a tow truck

```bash
export GRUALINK_API_KEY=...   # your test key
dotnet run --project examples/RequestTow
```

With fictitious data, the example sends a request, prints the estimated price, sends it again with the
same `Idempotency-Key` to show that nothing is duplicated, and reads its status. It only runs with test
keys.

With a test key, the beginning of `external_ref` decides the final status: `TEST-CONFIRMAR`,
`TEST-EN-CURSO`, `TEST-TERMINAR`, `TEST-RECHAZAR`, `TEST-CANCELAR`.

### Receive webhooks

```bash
export GRUALINK_WEBHOOK_SECRET=...   # your signing secret
dotnet run --project examples/WebhookServer
```

It listens on `http://localhost:8787/webhooks/grualink`. GRUALINK only delivers to public `https`
addresses on port 443: publish the receiver behind your domain (or an `https` tunnel while you test),
give that address to the company and ask them to send you a test event.

## Using it in your code

```csharp
using Grualink;

using var client = new GrualinkClient(new GrualinkClientOptions
{
    ApiKey = Environment.GetEnvironmentVariable("GRUALINK_API_KEY")!,
});

try
{
    var result = await client.CreateServiceRequestAsync(
        new ServiceRequestInput
        {
            ExternalRef = "TEST-CONFIRMAR-0001",
            Contact = new ContactInput { Name = "Persona de Ejemplo", Phone = "+525500000000" },
            Pickup = new PlaceInput { Address = "Av. Juárez 100, Centro, Guadalajara, Jal.", Lat = 20.6767, Lng = -103.3475 },
            Destination = new PlaceInput { Address = "Av. Patria 500, Zapopan, Jal.", Lat = 20.7214, Lng = -103.3918 },
        },
        idempotencyKey: myRecord.IdempotencyKey, // store it before calling
        cancellationToken);

    var serviceRequest = result.ServiceRequest;
    Console.WriteLine($"{serviceRequest.Id} {serviceRequest.Status} {serviceRequest.Quote?.EstimatedPrice?.Total} {result.Replayed}");
}
catch (GrualinkValidationException error)
{
    foreach (var detail in error.Details)
    {
        Console.Error.WriteLine($"{detail.Field}: {detail.Issue}");
    }
}
```

C# properties map to the API fields in `snake_case` (`ExternalRef` is `external_ref`). Enumerated values
(`Status`, `Type`, `Category`…) are the same strings as in the contract and are never translated.

| Operation | Method |
|---|---|
| Create | `CreateServiceRequestAsync(input, idempotencyKey)` → `ServiceRequest`, `Replayed`, `IdempotencyKey` |
| Read one | `GetServiceRequestAsync(id)` |
| Find by your reference | `FindServiceRequestsByExternalRefAsync(externalRef)` |
| Service health | `HealthAsync()` |

### Idempotency and retries

- Every request carries an `Idempotency-Key` of your own. If you repeat the call with the same key and
  the same body within 24 hours, the API answers **`200`** with `Idempotent-Replayed: true` and creates
  nothing (`Replayed` is `true`). The same key with another body answers `409`.
- The client retries only what makes sense: network errors, timeouts, `5xx` and `429 rate_limited`
  (waiting what `Retry-After` says). It uses exponential backoff with jitter (base 0.5 s, cap 8 s, 3
  retries) and always resends the same key and the same bytes.
- `Timeout` (15 s by default) applies to each attempt. The `CancellationToken` cancels the whole call,
  waits included, and does not count as a timeout.
- The key never appears in exceptions or logs, nor in the client's `ToString()`.
- Redirects are not followed: the key does not travel to another address.

### Verifying a webhook

```csharp
var result = Webhooks.VerifyWebhookSignature(
    rawBody,                                                           // the bytes exactly as received
    signatureHeader: request.Headers["X-GruaLink-Signature"],
    secret: Environment.GetEnvironmentVariable("GRUALINK_WEBHOOK_SECRET")); // whole, with whsec_

if (!result.Valid)
{
    // result.Reason: missing_secret, missing_header, malformed_header,
    // timestamp_out_of_tolerance or signature_mismatch. For your logs only.
}
```

What matters:

1. Verify the **raw body**, before turning it into JSON. If you parse it and serialize it again, the
   signature no longer matches.
2. Use the **whole** secret, with its `whsec_` prefix.
3. Reject timestamps more than 5 minutes away.
4. After a secret rotation, two `v1` signatures arrive for 24 hours: accept if either matches.
5. Deliveries can repeat: keep the `id` of the ones you processed and answer `2xx` without processing
   them again.
6. Answer `2xx` within 10 seconds and do the heavy work afterwards.

### Mounting it on ASP.NET Core

The delicate point is getting the **raw body**. `WebhookHandler` depends on no framework: it takes the
bytes and the two headers and returns the status and body of the answer.

Minimal API (this is what `examples/WebhookServer/WebhookApp.cs` does): read `Request.Body` yourself and
do not declare the body as a parameter of the delegate, because it would arrive already parsed.

```csharp
var handler = Webhooks.CreateWebhookHandler(new WebhookHandlerOptions
{
    Secrets = [Environment.GetEnvironmentVariable("GRUALINK_WEBHOOK_SECRET")!],
    OnEvent = (webhookEvent, cancellationToken) => queue.EnqueueAsync(webhookEvent, cancellationToken),
});

app.MapPost("/webhooks/grualink", async (HttpContext context) =>
{
    var rawBody = await Webhooks.ReadRawBodyAsync(context.Request.Body, context.Request.ContentLength);
    if (rawBody is null)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return;
    }

    var result = await handler.HandleAsync(
        rawBody,
        context.Request.Headers[Webhooks.SignatureHeader],
        context.Request.Headers[Webhooks.DeliveryHeader],
        context.RequestAborted);
    context.Response.StatusCode = result.Status;
    context.Response.ContentType = "application/json; charset=utf-8";
    await context.Response.WriteAsync(result.Body.ToJson());
});
```

MVC controllers: the action must not take `[FromBody]` or a model as a parameter. Read `Request.Body` as
above and return
`new ContentResult { StatusCode = result.Status, ContentType = "application/json", Content = result.Body.ToJson() }`.

With several instances, replace `InMemoryDeliveryDeduplicator` with an implementation of
`IDeliveryDeduplicator` backed by your database (a unique key on the event `id` is enough).

## Errors

Every API error has the shape `{ "error": { "code", "message", "details"? } }`. `code` is stable; branch
on it, not on `message`. `GrualinkApiException` exposes `Status`, `Code`, `Message`, `Details`,
`RetryAfterSeconds` and `Attempts`.

| HTTP | `code` | What happened | Exception | Retried? |
|---|---|---|---|---|
| 400 | `invalid_request` | The request is not valid | `GrualinkInvalidRequestException` | no |
| 400 | `invalid_json` | The body is not valid JSON | `GrualinkInvalidRequestException` | no |
| 400 | `key_in_query` | The key was sent in the URL; have it revoked | `GrualinkInvalidRequestException` | no |
| 400 | `idempotency_key_required` | `Idempotency-Key` is missing | `GrualinkInvalidRequestException` | no |
| 400 | `invalid_idempotency_key` | Invalid `Idempotency-Key` (1 to 255 visible ASCII characters) | `GrualinkInvalidRequestException` | no |
| 401 | `unauthorized` | Key missing, invalid, expired or revoked | `GrualinkAuthenticationException` | no |
| 403 | `insufficient_scope` | The key is not allowed to do this | `GrualinkPermissionException` | no |
| 403 | `customer_unavailable` | Your customer account with the company is not available | `GrualinkPermissionException` | no |
| 403 | `live_mode_unavailable` | Live requests are not enabled yet | `GrualinkPermissionException` | no |
| 403 | `browser_not_allowed` | A live key was used from a browser | `GrualinkPermissionException` | no |
| 404 | `not_found` | It does not exist or is not yours | `GrualinkNotFoundException` | no |
| 405 | `method_not_allowed` | Method not allowed for this path | `GrualinkInvalidRequestException` | no |
| 409 | `idempotency_conflict` | That `Idempotency-Key` was already used with another body | `GrualinkIdempotencyConflictException` | no |
| 413 | `payload_too_large` | The body exceeds 16 KB | `GrualinkInvalidRequestException` | no |
| 415 | `unsupported_media_type` | The body must be sent as `application/json` | `GrualinkInvalidRequestException` | no |
| 422 | `validation_error` | Invalid fields; `Details` says which | `GrualinkValidationException` | no |
| 429 | `rate_limited` | Too many requests; wait `Retry-After` | `GrualinkRateLimitException` | yes |
| 429 | `too_many_open_requests` | Too many unresolved requests with the company | `GrualinkRateLimitException` | no |
| 500 | `internal_error` | Internal error | `GrualinkServerException` | yes |

Also: `GrualinkConnectionException` (no response, retried), `GrualinkTimeoutException` (timed out,
retried) and `GrualinkConfigException` (invalid configuration or argument; nothing was sent). All derive
from `GrualinkException`.

The names are the ones of the contract (`contract/API.md`, section 11) with the `Exception` suffix .NET
uses instead of `Error`: `GrualinkApiError` is `GrualinkApiException` here.

The full contract, with fields, limits and the order of checks, is in
[`contract/API.md`](contract/API.md).

## Tests

```bash
dotnet test                              # xUnit
dotnet build -warnaserror                # warnings are errors
dotnet format --verify-no-changes        # formatting
bash scripts/run-examples.sh             # runs both examples against local stand-ins (after a Release build)
```

The tests never call the real API: they use a local fake server. They cover every case of
`contract/signature-vectors.json`, every error code, retries, idempotency and that the key appears in no
exception or log output.

## Layout

```
contract/                 API contract (es/en), signature test cases and checklist
src/Grualink/             client, exceptions, models, webhook verification and deduplication
examples/RequestTow/      request a tow truck
examples/WebhookServer/   webhook receiver (ASP.NET Core Minimal API)
tests/Grualink.Tests/     tests (xUnit) and fake server
tests/FakeApi/            local stand-in of the API, to run the example without a key
scripts/                  run-examples.sh
```

## License

[MIT](LICENSE)
