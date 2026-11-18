// End-to-end example: request a tow truck with fictitious data, print the quote and check the status.
//
//   GRUALINK_API_KEY=<your TEST key> dotnet run --project examples/RequestTow
//
// Use a TEST key (glk_test_...). Test requests never become real services.
using System.Globalization;
using Grualink;

var apiKey = Environment.GetEnvironmentVariable("GRUALINK_API_KEY");
if (string.IsNullOrEmpty(apiKey))
{
    Console.Error.WriteLine("Set GRUALINK_API_KEY to your TEST key (it starts with glk_test_). See .env.example.");
    return 1;
}

if (!apiKey.StartsWith("glk_test_", StringComparison.Ordinal))
{
    Console.Error.WriteLine("This example only runs with a TEST key (glk_test_...).");
    return 1;
}

var baseUrl = Environment.GetEnvironmentVariable("GRUALINK_API_BASE_URL");

// With a test key, the prefix of external_ref decides the final status: TEST-CONFIRMAR -> confirmed,
// TEST-EN-CURSO -> in_progress, TEST-TERMINAR -> completed, TEST-RECHAZAR -> rejected, TEST-CANCELAR -> cancelled.
var externalRef = $"TEST-CONFIRMAR-{Guid.NewGuid().ToString("N")[..8]}";
var request = new ServiceRequestInput
{
    ExternalRef = externalRef,
    Contact = new ContactInput { Name = "Persona de Ejemplo", Phone = "+525500000000" },
    Vehicle = new VehicleInput
    {
        Make = "Nissan",
        Model = "Versa",
        Year = 2019,
        Color = "Gris",
        Plate = "ABC123A",
        Type = "auto",
    },
    Pickup = new PlaceInput { Address = "Av. Juárez 100, Centro, Guadalajara, Jal.", Lat = 20.6767, Lng = -103.3475 },
    Destination = new PlaceInput { Address = "Av. Patria 500, Zapopan, Jal.", Lat = 20.7214, Lng = -103.3918 },
    Service = new ServiceDetailsInput { Reason = "No arranca", Category = "vial", RequiredClass = "A" },
    Notes = "Datos ficticios de ejemplo.",
};

try
{
    using var client = new GrualinkClient(new GrualinkClientOptions
    {
        ApiKey = apiKey,
        BaseUrl = string.IsNullOrEmpty(baseUrl) ? null : baseUrl,
        OnRetry = info => Console.Error.WriteLine(
            $"Attempt {info.Attempt} failed ({info.Error.GetType().Name}); retrying in {Milliseconds(info.Delay)} ms"),
    });

    var health = await client.HealthAsync();
    Console.WriteLine($"API status: {health.Status} ({health.Version})");

    // In your system, store this key next to your own record BEFORE calling, and reuse it if you have to retry.
    var idempotencyKey = Guid.NewGuid().ToString();

    var created = await client.CreateServiceRequestAsync(request, idempotencyKey);
    var serviceRequest = created.ServiceRequest;
    Console.WriteLine();
    Console.WriteLine(
        $"Created {serviceRequest.Id} ({serviceRequest.Mode}) with status \"{serviceRequest.Status}\", reference {serviceRequest.ExternalRef}");
    PrintQuote(serviceRequest);

    // Sending it again with the same key and the same body creates nothing new.
    var again = await client.CreateServiceRequestAsync(request, idempotencyKey);
    Console.WriteLine();
    Console.WriteLine(
        $"Sent again with the same Idempotency-Key: replayed={again.Replayed}, same id={again.ServiceRequest.Id == serviceRequest.Id}");

    var current = await client.GetServiceRequestAsync(serviceRequest.Id);
    var folio = current.Service?.Folio is { Length: > 0 } value ? $", folio {value}" : "";
    Console.WriteLine();
    Console.WriteLine($"Status now: \"{current.Status}\"{folio}");

    var byReference = await client.FindServiceRequestsByExternalRefAsync(externalRef);
    Console.WriteLine($"Requests with reference {externalRef}: {byReference.Count}");
    return 0;
}
catch (GrualinkValidationException error)
{
    Console.Error.WriteLine($"Invalid fields: {string.Join(", ", error.Details.Select(d => $"{d.Field} ({d.Issue})"))}");
    return 1;
}
catch (GrualinkApiException error)
{
    Console.Error.WriteLine($"API error {error.Status} {error.Code}: {error.Message}");
    return 1;
}
catch (GrualinkException error)
{
    Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");
    return 1;
}

static string Milliseconds(TimeSpan delay) => delay.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);

static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

static void PrintQuote(ServiceRequest serviceRequest)
{
    var price = serviceRequest.Quote?.EstimatedPrice;
    if (price is null)
    {
        var reason = serviceRequest.Quote?.UnpricedReason ?? "unknown reason";
        Console.WriteLine($"  No estimated price ({reason}); the company will quote it.");
        return;
    }

    Console.WriteLine($"  Subtotal:  {Money(price.Subtotal)} {price.Currency}");
    foreach (var tax in price.Taxes)
    {
        var sign = tax.Type == "retention" ? "-" : "+";
        Console.WriteLine($"  {sign} {tax.Name} ({tax.Rate.ToString(CultureInfo.InvariantCulture)}%): {Money(tax.Amount)}");
    }

    Console.WriteLine($"  Total:     {Money(price.Total)} {price.Currency} (estimate, extras not included)");
}
