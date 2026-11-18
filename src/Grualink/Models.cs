using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grualink;

// Request and response shapes of the public API. Properties map to the wire names in snake_case
// (ExternalRef <-> external_ref). Enumerated values are kept as the strings of the contract.

/// <summary>Body of <c>POST /service-requests</c>. Unknown fields are rejected by the API with <c>validation_error</c>.</summary>
public sealed record ServiceRequestInput
{
    /// <summary>Your own reference (claim or case number), up to 64 characters.</summary>
    public string? ExternalRef { get; init; }

    public required ContactInput Contact { get; init; }

    public VehicleInput? Vehicle { get; init; }

    public required PlaceInput Pickup { get; init; }

    public PlaceInput? Destination { get; init; }

    public ServiceDetailsInput? Service { get; init; }

    /// <summary>Up to 1,000 characters and 20 lines.</summary>
    public string? Notes { get; init; }
}

public sealed record ContactInput
{
    /// <summary>Up to 120 characters.</summary>
    public required string Name { get; init; }

    /// <summary>International format (+52...); 10 digits are taken as a Mexican number.</summary>
    public required string Phone { get; init; }
}

public sealed record PlaceInput
{
    /// <summary>Required, up to 500 characters.</summary>
    public required string Address { get; init; }

    /// <summary>Latitude and longitude go together. Without pickup and destination coordinates there is no estimated price.</summary>
    public double? Lat { get; init; }

    public double? Lng { get; init; }
}

public sealed record VehicleInput
{
    public string? Make { get; init; }

    public string? Model { get; init; }

    public int? Year { get; init; }

    public string? Color { get; init; }

    public string? Plate { get; init; }

    /// <summary>One of: auto, camioneta, motocicleta, camion, otro.</summary>
    public string? Type { get; init; }
}

public sealed record ServiceDetailsInput
{
    public string? Reason { get; init; }

    /// <summary>One of: vial, colision.</summary>
    public string? Category { get; init; }

    /// <summary>One of: A, B, C, D.</summary>
    public string? RequiredClass { get; init; }

    /// <summary>Up to 12 values, no duplicates. See the contract for the accepted values.</summary>
    public IReadOnlyList<string>? RequiredAccessories { get; init; }
}

/// <summary>A service request as the API returns it. Fields the library does not know are ignored.</summary>
public sealed record ServiceRequest
{
    public string Id { get; init; } = "";

    /// <summary>Always <c>service_request</c>.</summary>
    public string Object { get; init; } = "";

    /// <summary>One of: received, quoted, confirmed, in_progress, completed, rejected, cancelled, expired.</summary>
    public string Status { get; init; } = "";

    /// <summary><c>test</c> or <c>live</c>, from the key.</summary>
    public string Mode { get; init; } = "";

    public string? ExternalRef { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>Set when the status is <c>rejected</c>.</summary>
    public string? RejectionReason { get; init; }

    /// <summary>Present once the company registered the service.</summary>
    public RegisteredService? Service { get; init; }

    public Quote? Quote { get; init; }

    /// <summary>Live keys only.</summary>
    public Assignment? Assignment { get; init; }

    /// <summary>Live keys only.</summary>
    public Timeline? Timeline { get; init; }
}

public sealed record RegisteredService
{
    public string? Folio { get; init; }

    public string Status { get; init; } = "";
}

public sealed record Quote
{
    /// <summary><c>null</c> when the request could not be priced automatically; see <see cref="UnpricedReason"/>.</summary>
    public EstimatedPrice? EstimatedPrice { get; init; }

    /// <summary>Always <c>false</c>: the estimate never includes extras such as tolls.</summary>
    public bool IncludesExtras { get; init; }

    public bool? ApproximateDistance { get; init; }

    /// <summary>One of: missing_coordinates, missing_destination, no_zones_configured, missing_vehicle_class.</summary>
    public string? UnpricedReason { get; init; }

    public string? Note { get; init; }
}

public sealed record EstimatedPrice
{
    public string Currency { get; init; } = "";

    public decimal Subtotal { get; init; }

    public IReadOnlyList<TaxLine> Taxes { get; init; } = [];

    public decimal TaxTotal { get; init; }

    public decimal RetentionTotal { get; init; }

    public decimal Total { get; init; }
}

public sealed record TaxLine
{
    public string Name { get; init; } = "";

    /// <summary><c>tax</c> (added) or <c>retention</c> (subtracted).</summary>
    public string Type { get; init; } = "";

    public decimal Rate { get; init; }

    public decimal Amount { get; init; }
}

public sealed record Assignment
{
    public string? OperatorFirstName { get; init; }

    public AssignedTruck? Truck { get; init; }
}

public sealed record AssignedTruck
{
    public string? EconomicNumber { get; init; }

    public string? Plate { get; init; }
}

public sealed record Timeline
{
    public DateTimeOffset? OnTheWayAt { get; init; }

    public DateTimeOffset? OnSiteAt { get; init; }

    public DateTimeOffset? TransportStartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>Answer of <c>GET /health</c>.</summary>
public sealed record HealthStatus
{
    public string Status { get; init; } = "";

    public string Version { get; init; } = "";
}

/// <summary>What <see cref="GrualinkClient.CreateServiceRequestAsync"/> returns.</summary>
/// <param name="ServiceRequest">The service request, new or replayed.</param>
/// <param name="Replayed"><c>true</c> when the API answered 200 with <c>Idempotent-Replayed: true</c> (nothing new was created).</param>
/// <param name="IdempotencyKey">The key that was sent (yours or the generated one).</param>
public sealed record CreateServiceRequestResult(ServiceRequest ServiceRequest, bool Replayed, string IdempotencyKey);

/// <summary>Body of a webhook delivery.</summary>
public sealed record WebhookEvent
{
    /// <summary>Same value as the <c>X-GruaLink-Delivery</c> header and on every retry. Use it to deduplicate.</summary>
    public string Id { get; init; } = "";

    /// <summary>Always <c>event</c>.</summary>
    public string Object { get; init; } = "";

    /// <summary>For example <c>service_request.confirmed</c>. Ignore the types you do not know.</summary>
    public string Type { get; init; } = "";

    public string Mode { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    public WebhookEventData Data { get; init; } = new();
}

public sealed record WebhookEventData
{
    /// <summary>Previous status on status events; <c>null</c> on assigned, on_the_way and on_site.</summary>
    public string? PreviousStatus { get; init; }

    /// <summary>Same representation as <c>GET /service-requests/{id}</c> at that moment.</summary>
    public ServiceRequest? ServiceRequest { get; init; }

    /// <summary>Only on <c>webhook.test</c>.</summary>
    public string? Message { get; init; }
}

internal sealed record ServiceRequestList
{
    public IReadOnlyList<ServiceRequest>? Data { get; init; }
}

internal static class GrualinkJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // The body travels as application/json, never inside HTML: accents and "+" are sent as they are.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
