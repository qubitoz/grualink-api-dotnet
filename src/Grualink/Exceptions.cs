namespace Grualink;

/// <summary>One entry of <c>error.details</c> in a 400/422 response.</summary>
/// <param name="Field">Dotted path of the offending field, for example <c>contact.phone</c>.</param>
/// <param name="Issue">
/// One of: required, invalid_type, too_long, invalid_format, out_of_range, unknown_field, invalid_value, duplicate.
/// </param>
public sealed record ErrorDetail(string Field, string Issue);

/// <summary>Base class of every exception thrown by this library.</summary>
public class GrualinkException : Exception
{
    public GrualinkException(string message)
        : base(message)
    {
    }

    /// <summary>Number of HTTP attempts made before giving up (0 when no request was sent).</summary>
    public int Attempts { get; internal set; }
}

/// <summary>The client was configured or called with an invalid argument. Nothing was sent.</summary>
public sealed class GrualinkConfigException(string message) : GrualinkException(message);

/// <summary>The request never got an HTTP response (DNS, connection reset, TLS...). Retried automatically.</summary>
public sealed class GrualinkConnectionException(string message) : GrualinkException(message);

/// <summary>No response within the configured timeout. Retried automatically.</summary>
public sealed class GrualinkTimeoutException(string message) : GrualinkException(message);

/// <summary>
/// The API answered with an error. <see cref="Code"/> is the stable machine-readable code from
/// <c>{ "error": { "code", "message", "details"? } }</c>; the message may change over time.
/// When the body is not the documented error shape, <see cref="Code"/> is <c>unexpected_response</c>.
/// </summary>
public class GrualinkApiException : GrualinkException
{
    public GrualinkApiException(
        int status,
        string code,
        string message,
        IReadOnlyList<ErrorDetail>? details = null,
        int? retryAfterSeconds = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Details = details ?? [];
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>HTTP status of the answer.</summary>
    public int Status { get; }

    /// <summary>Stable error code. Branch on this, never on the message.</summary>
    public string Code { get; }

    /// <summary>Invalid fields, when the API reports them. Empty otherwise.</summary>
    public IReadOnlyList<ErrorDetail> Details { get; }

    /// <summary>Value of the <c>Retry-After</c> header in seconds, when present.</summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>Maps an HTTP status to the exception class that represents it.</summary>
    public static GrualinkApiException FromStatus(
        int status,
        string code,
        string message,
        IReadOnlyList<ErrorDetail>? details = null,
        int? retryAfterSeconds = null)
    {
        return status switch
        {
            401 => new GrualinkAuthenticationException(status, code, message, details, retryAfterSeconds),
            403 => new GrualinkPermissionException(status, code, message, details, retryAfterSeconds),
            404 => new GrualinkNotFoundException(status, code, message, details, retryAfterSeconds),
            409 => new GrualinkIdempotencyConflictException(status, code, message, details, retryAfterSeconds),
            422 => new GrualinkValidationException(status, code, message, details, retryAfterSeconds),
            429 => new GrualinkRateLimitException(status, code, message, details, retryAfterSeconds),
            >= 500 => new GrualinkServerException(status, code, message, details, retryAfterSeconds),
            400 or 405 or 413 or 415 => new GrualinkInvalidRequestException(status, code, message, details, retryAfterSeconds),
            _ => new GrualinkApiException(status, code, message, details, retryAfterSeconds),
        };
    }
}

/// <summary>400, 405, 413, 415: the request is malformed. Fix the request; do not retry.</summary>
public sealed class GrualinkInvalidRequestException(
    int status, string code, string message, IReadOnlyList<ErrorDetail>? details = null, int? retryAfterSeconds = null)
    : GrualinkApiException(status, code, message, details, retryAfterSeconds);

/// <summary>401 <c>unauthorized</c>: key missing, invalid, expired or revoked (always the same answer).</summary>
public sealed class GrualinkAuthenticationException(
    int status, string code, string message, IReadOnlyList<ErrorDetail>? details = null, int? retryAfterSeconds = null)
    : GrualinkApiException(status, code, message, details, retryAfterSeconds);

/// <summary>403: the key is not allowed to do this (scope, customer unavailable, live mode, browser).</summary>
public sealed class GrualinkPermissionException(
    int status, string code, string message, IReadOnlyList<ErrorDetail>? details = null, int? retryAfterSeconds = null)
    : GrualinkApiException(status, code, message, details, retryAfterSeconds);

/// <summary>404 <c>not_found</c>: it does not exist or does not belong to your key.</summary>
public sealed class GrualinkNotFoundException(
    int status, string code, string message, IReadOnlyList<ErrorDetail>? details = null, int? retryAfterSeconds = null)
    : GrualinkApiException(status, code, message, details, retryAfterSeconds);

/// <summary>409 <c>idempotency_conflict</c>: the Idempotency-Key was already used with a different body.</summary>
public sealed class GrualinkIdempotencyConflictException(
    int status, string code, string message, IReadOnlyList<ErrorDetail>? details = null, int? retryAfterSeconds = null)
    : GrualinkApiException(status, code, message, details, retryAfterSeconds);

/// <summary>422 <c>validation_error</c>: see <see cref="GrualinkApiException.Details"/> for the invalid fields.</summary>
public sealed class GrualinkValidationException(
    int status, string code, string message, IReadOnlyList<ErrorDetail>? details = null, int? retryAfterSeconds = null)
    : GrualinkApiException(status, code, message, details, retryAfterSeconds);

/// <summary>429 <c>rate_limited</c> (retried automatically) or <c>too_many_open_requests</c> (not retried).</summary>
public sealed class GrualinkRateLimitException(
    int status, string code, string message, IReadOnlyList<ErrorDetail>? details = null, int? retryAfterSeconds = null)
    : GrualinkApiException(status, code, message, details, retryAfterSeconds);

/// <summary>5xx: retried automatically; safe because the same Idempotency-Key is reused.</summary>
public sealed class GrualinkServerException(
    int status, string code, string message, IReadOnlyList<ErrorDetail>? details = null, int? retryAfterSeconds = null)
    : GrualinkApiException(status, code, message, details, retryAfterSeconds);
