# GRUALINK API: ejemplo en C# / .NET

[English](README.en.md)

Ejemplo listo para clonar y correr que integra tu sistema (aseguradora, asistencia, integrador) con la
API de GRUALINK: **pedir una grúa**, **consultar el estatus** y **recibir avisos firmados**.

Guía pública de la API: <https://grualink.mx/api>

Qué trae:

- Un **cliente mínimo** (`src/Grualink/GrualinkClient.cs`): crea solicitudes con `Idempotency-Key`,
  consulta el estatus, excepciones tipadas, reintentos con espera exponencial, tiempo máximo y
  `CancellationToken`. Solo `HttpClient` y `System.Text.Json`: sin dependencias de ejecución.
- La **verificación de avisos** (`src/Grualink/Webhooks.cs`, `WebhookHandler.cs`): verifica la firma
  sobre el cuerpo crudo, valida la ventana de tiempo, soporta el cambio de secreto y descarta duplicados.
- Dos **ejemplos ejecutables** (`examples/`): pedir una grúa y un receptor de avisos con ASP.NET Core
  Minimal API.
- El **kit de contrato** (`contract/`): el contrato de la API en español e inglés y los casos de prueba
  de firma que comparten los ejemplos de todos los lenguajes.

No es un SDK: es código pensado para leerse y copiarse a tu proyecto.

## Requisitos

- .NET 8 (LTS). SDK 8.0 para compilar.
- Una **llave de prueba** (`glk_test_…`). Te la da la empresa de grúas con la que trabajas.
- Para los avisos: el **secreto de firma** (`whsec_…`) que te entrega la misma empresa al registrar la
  dirección de tu sistema.

## Instalación

```bash
git clone <la dirección de este repositorio>
cd grualink-api-dotnet
dotnet build
```

## Variables de entorno

Solo los nombres; los valores van en tu equipo y nunca en el repositorio. Hay una plantilla en
`.env.example` (.NET no la lee por sí solo: exporta las variables o usa tu gestor de secretos).

| Variable | Para qué |
|---|---|
| `GRUALINK_API_KEY` | Tu llave de **prueba** (`glk_test_…`) |
| `GRUALINK_WEBHOOK_SECRET` | Secreto de firma de los avisos (`whsec_…`), completo, con su prefijo |
| `GRUALINK_WEBHOOK_SECRET_PREVIOUS` | Opcional: el secreto anterior mientras dura un cambio de secreto |
| `GRUALINK_API_BASE_URL` | Opcional: otra dirección base (por omisión `https://grualink.mx/api/v1`) |
| `PORT` | Opcional: puerto del receptor de ejemplo (por omisión 8787) |

## Correr los ejemplos

### Pedir una grúa

```bash
export GRUALINK_API_KEY=...   # tu llave de prueba
dotnet run --project examples/RequestTow
```

Con datos ficticios, el ejemplo envía una solicitud, imprime el precio estimado, la vuelve a enviar con
la misma `Idempotency-Key` para mostrar que no se duplica y consulta su estatus. Solo corre con llaves
de prueba.

Con una llave de prueba, el inicio de `external_ref` decide el estatus final: `TEST-CONFIRMAR`,
`TEST-EN-CURSO`, `TEST-TERMINAR`, `TEST-RECHAZAR`, `TEST-CANCELAR`.

### Recibir avisos

```bash
export GRUALINK_WEBHOOK_SECRET=...   # tu secreto de firma
dotnet run --project examples/WebhookServer
```

Queda escuchando en `http://localhost:8787/webhooks/grualink`. GRUALINK solo entrega a direcciones
`https` públicas en el puerto 443: publica el receptor detrás de tu dominio (o de un túnel `https` mientras
pruebas), dale esa dirección a la empresa y pídele que te mande un aviso de prueba.

## Uso en tu código

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
        idempotencyKey: miRegistro.ClaveDeIdempotencia, // guárdala antes de llamar
        cancellationToken);

    var solicitud = result.ServiceRequest;
    Console.WriteLine($"{solicitud.Id} {solicitud.Status} {solicitud.Quote?.EstimatedPrice?.Total} {result.Replayed}");
}
catch (GrualinkValidationException error)
{
    foreach (var detail in error.Details)
    {
        Console.Error.WriteLine($"{detail.Field}: {detail.Issue}");
    }
}
```

Las propiedades de C# corresponden a los campos de la API en `snake_case` (`ExternalRef` es
`external_ref`). Los valores enumerados (`Status`, `Type`, `Category`…) son las mismas cadenas del
contrato y no se traducen.

| Operación | Método |
|---|---|
| Crear | `CreateServiceRequestAsync(input, idempotencyKey)` → `ServiceRequest`, `Replayed`, `IdempotencyKey` |
| Consultar una | `GetServiceRequestAsync(id)` |
| Buscar por tu referencia | `FindServiceRequestsByExternalRefAsync(externalRef)` |
| Estado del servicio | `HealthAsync()` |

### Idempotencia y reintentos

- Cada solicitud lleva una `Idempotency-Key` tuya. Si repites la llamada con la misma clave y el mismo
  cuerpo en 24 horas, la API responde **`200`** con `Idempotent-Replayed: true` y no crea nada
  (`Replayed` es `true`). La misma clave con otro cuerpo responde `409`.
- El cliente reintenta solo lo que tiene sentido: errores de red, tiempo agotado, `5xx` y
  `429 rate_limited` (esperando lo que diga `Retry-After`). Usa espera exponencial con jitter (base
  0.5 s, tope 8 s, 3 reintentos) y reenvía siempre la misma clave y los mismos bytes.
- `Timeout` (15 s por omisión) aplica a cada intento. El `CancellationToken` cancela la llamada completa,
  incluidas las esperas, y no cuenta como tiempo agotado.
- La llave nunca aparece en excepciones ni registros, ni en `ToString()` del cliente.
- No se siguen redirecciones: la llave no viaja a otra dirección.

### Verificar un aviso

```csharp
var result = Webhooks.VerifyWebhookSignature(
    rawBody,                                                           // los bytes tal como llegaron
    signatureHeader: request.Headers["X-GruaLink-Signature"],
    secret: Environment.GetEnvironmentVariable("GRUALINK_WEBHOOK_SECRET")); // completo, con whsec_

if (!result.Valid)
{
    // result.Reason: missing_secret, missing_header, malformed_header,
    // timestamp_out_of_tolerance o signature_mismatch. Solo para tus registros.
}
```

Lo importante:

1. Verifica el **cuerpo crudo**, antes de convertirlo a JSON. Si lo conviertes y lo vuelves a
   serializar, la firma ya no coincide.
2. Usa el secreto **completo**, con su prefijo `whsec_`.
3. Rechaza marcas de tiempo con más de 5 minutos de diferencia.
4. Tras un cambio de secreto llegan dos firmas `v1` durante 24 horas: acepta si cualquiera coincide.
5. Los avisos pueden repetirse: guarda el `id` de los ya procesados y responde `2xx` sin reprocesar.
6. Responde `2xx` en menos de 10 segundos y haz el trabajo pesado después.

### Montarlo en ASP.NET Core

El punto delicado es conseguir el **cuerpo crudo**. `WebhookHandler` no depende de ningún framework:
recibe los bytes y las dos cabeceras y devuelve el estatus y el cuerpo de la respuesta.

Minimal API (es lo que hace `examples/WebhookServer/WebhookApp.cs`): lee `Request.Body` tú mismo y no
declares el cuerpo como parámetro del delegado, porque llegaría ya convertido.

```csharp
var handler = Webhooks.CreateWebhookHandler(new WebhookHandlerOptions
{
    Secrets = [Environment.GetEnvironmentVariable("GRUALINK_WEBHOOK_SECRET")!],
    OnEvent = (webhookEvent, cancellationToken) => cola.EncolarAsync(webhookEvent, cancellationToken),
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

Controladores MVC: la acción no debe llevar `[FromBody]` ni un modelo como parámetro. Lee
`Request.Body` igual que arriba y devuelve
`new ContentResult { StatusCode = result.Status, ContentType = "application/json", Content = result.Body.ToJson() }`.

Con varias instancias, sustituye `InMemoryDeliveryDeduplicator` por una implementación de
`IDeliveryDeduplicator` que use tu base de datos (basta una clave única sobre el `id` del aviso).

## Errores

Todos los errores de la API tienen la forma `{ "error": { "code", "message", "details"? } }`. El `code`
es estable; decide con él, no con el `message`. `GrualinkApiException` expone `Status`, `Code`,
`Message`, `Details`, `RetryAfterSeconds` y `Attempts`.

| HTTP | `code` | Qué pasó | Excepción | ¿Se reintenta? |
|---|---|---|---|---|
| 400 | `invalid_request` | La petición no es válida | `GrualinkInvalidRequestException` | no |
| 400 | `invalid_json` | El cuerpo no es JSON válido | `GrualinkInvalidRequestException` | no |
| 400 | `key_in_query` | La llave se envió en la URL; pide que la revoquen | `GrualinkInvalidRequestException` | no |
| 400 | `idempotency_key_required` | Falta `Idempotency-Key` | `GrualinkInvalidRequestException` | no |
| 400 | `invalid_idempotency_key` | `Idempotency-Key` inválida (1 a 255 caracteres ASCII visibles) | `GrualinkInvalidRequestException` | no |
| 401 | `unauthorized` | Llave ausente, inválida, vencida o revocada | `GrualinkAuthenticationException` | no |
| 403 | `insufficient_scope` | La llave no tiene permiso para esto | `GrualinkPermissionException` | no |
| 403 | `customer_unavailable` | Tu cuenta de cliente con la empresa no está disponible | `GrualinkPermissionException` | no |
| 403 | `live_mode_unavailable` | Las solicitudes reales aún no están habilitadas | `GrualinkPermissionException` | no |
| 403 | `browser_not_allowed` | Llave real usada desde un navegador | `GrualinkPermissionException` | no |
| 404 | `not_found` | No existe o no es tuya | `GrualinkNotFoundException` | no |
| 405 | `method_not_allowed` | Método no permitido para esa ruta | `GrualinkInvalidRequestException` | no |
| 409 | `idempotency_conflict` | Esa `Idempotency-Key` ya se usó con otro cuerpo | `GrualinkIdempotencyConflictException` | no |
| 413 | `payload_too_large` | El cuerpo excede 16 KB | `GrualinkInvalidRequestException` | no |
| 415 | `unsupported_media_type` | El cuerpo debe ir como `application/json` | `GrualinkInvalidRequestException` | no |
| 422 | `validation_error` | Campos inválidos; `Details` dice cuáles | `GrualinkValidationException` | no |
| 429 | `rate_limited` | Demasiadas peticiones; espera `Retry-After` | `GrualinkRateLimitException` | sí |
| 429 | `too_many_open_requests` | Demasiadas solicitudes sin resolver con la empresa | `GrualinkRateLimitException` | no |
| 500 | `internal_error` | Error interno | `GrualinkServerException` | sí |

Además: `GrualinkConnectionException` (sin respuesta, se reintenta), `GrualinkTimeoutException` (tiempo
agotado, se reintenta) y `GrualinkConfigException` (configuración o argumento inválido; no se envió
nada). Todas derivan de `GrualinkException`.

Los nombres son los del contrato (`contract/API.md`, sección 11) con el sufijo `Exception` que usa .NET
en lugar de `Error`: `GrualinkApiError` es aquí `GrualinkApiException`.

El contrato completo, con campos, límites y el orden de las comprobaciones, está en
[`contract/API.es.md`](contract/API.es.md).

## Pruebas

```bash
dotnet test                              # xUnit
dotnet build -warnaserror                # las advertencias son errores
dotnet format --verify-no-changes        # formato
bash scripts/run-examples.sh             # corre los dos ejemplos contra dobles locales (tras compilar en Release)
```

Las pruebas no llaman a la API real: usan un servidor falso local. Cubren todos los casos de
`contract/signature-vectors.json`, cada código de error, los reintentos, la idempotencia y que la llave
no aparezca en excepciones ni registros.

## Estructura

```
contract/                 contrato de la API (es/en), casos de prueba de firma y lista de verificación
src/Grualink/             cliente, excepciones, modelos, verificación de avisos y deduplicación
examples/RequestTow/      pedir una grúa
examples/WebhookServer/   receptor de avisos (ASP.NET Core Minimal API)
tests/Grualink.Tests/     pruebas (xUnit) y servidor falso
tests/FakeApi/            doble local de la API para correr el ejemplo sin llave
scripts/                  run-examples.sh
```

## Licencia

[MIT](LICENSE)
