using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Announcer;

/// <summary>
/// The raw JSON body of an error response. The API produces four different
/// shapes and every one of them has to land on a useful exception:
/// RFC 9457 problem+json, the validation variant with an <c>errors</c> map, the
/// bare <c>{"error": "..."}</c> used by two 409 paths, and — because every
/// member here is optional — the several 404 handlers that send no body at all.
/// </summary>
internal sealed class ProblemBody
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("status")] public int? Status { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("errors")] public Dictionary<string, string[]>? Errors { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }

    /// <summary>Extension member on a suppression refusal: the addresses refused.</summary>
    [JsonPropertyName("suppressed")] public string[]? Suppressed { get; set; }
}

/// <summary>Base class for everything this SDK throws. Catch it to catch them all.</summary>
public class AnnouncerException : Exception
{
    /// <summary>HTTP status, or 0 when the request never reached the API.</summary>
    public int StatusCode { get; }

    /// <summary>The problem's <c>title</c>, when the API sent one.</summary>
    public string? Title { get; }

    /// <summary>The problem's <c>detail</c> — usually the most human-readable part.</summary>
    public string? Detail { get; }

    /// <summary>The response's <c>x-request-id</c>. Quote it in support.</summary>
    public string? RequestId { get; }

    /// <summary>The error body exactly as the API sent it.</summary>
    public string? RawBody { get; }

    /// <summary>Creates an exception.</summary>
    public AnnouncerException(
        string message,
        int statusCode = 0,
        string? title = null,
        string? detail = null,
        string? requestId = null,
        string? rawBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
        RequestId = requestId;
        RawBody = rawBody;
    }
}

/// <summary>401 — the key is missing, malformed, or revoked.</summary>
public sealed class AnnouncerAuthenticationException : AnnouncerException
{
    /// <summary>Creates the exception.</summary>
    public AnnouncerAuthenticationException(string message, int statusCode, string? title, string? detail, string? requestId, string? rawBody)
        : base(message, statusCode, title, detail, requestId, rawBody) { }
}

/// <summary>
/// 403 — the key is valid but not allowed to do this. In practice a
/// send-scoped key touching the management surface, or a <c>From</c> domain
/// that is unregistered or unverified.
/// </summary>
public sealed class AnnouncerPermissionException : AnnouncerException
{
    /// <summary>Creates the exception.</summary>
    public AnnouncerPermissionException(string message, int statusCode, string? title, string? detail, string? requestId, string? rawBody)
        : base(message, statusCode, title, detail, requestId, rawBody) { }
}

/// <summary>404 — no such resource, or it belongs to another account.</summary>
public sealed class AnnouncerNotFoundException : AnnouncerException
{
    /// <summary>Creates the exception.</summary>
    public AnnouncerNotFoundException(string message, int statusCode, string? title, string? detail, string? requestId, string? rawBody)
        : base(message, statusCode, title, detail, requestId, rawBody) { }
}

/// <summary>
/// 409 — a duplicate domain, or a send with this Idempotency-Key still in
/// flight. The in-flight case is retried automatically first; seeing this means
/// the retry budget ran out while the original was still running.
/// </summary>
public sealed class AnnouncerConflictException : AnnouncerException
{
    /// <summary>Creates the exception.</summary>
    public AnnouncerConflictException(string message, int statusCode, string? title, string? detail, string? requestId, string? rawBody)
        : base(message, statusCode, title, detail, requestId, rawBody) { }
}

/// <summary>400 — one or more fields were rejected.</summary>
public sealed class AnnouncerValidationException : AnnouncerException
{
    /// <summary>Field name to the messages explaining why it was rejected.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    /// <summary>Creates the exception.</summary>
    public AnnouncerValidationException(
        string message, int statusCode, string? title, string? detail, string? requestId, string? rawBody,
        IReadOnlyDictionary<string, string[]>? errors)
        : base(message, statusCode, title, detail, requestId, rawBody)
        => Errors = errors ?? new Dictionary<string, string[]>();
}

/// <summary>422 — the request was well-formed but cannot be carried out.</summary>
public class AnnouncerUnprocessableException : AnnouncerException
{
    /// <summary>Creates the exception.</summary>
    public AnnouncerUnprocessableException(string message, int statusCode, string? title, string? detail, string? requestId, string? rawBody)
        : base(message, statusCode, title, detail, requestId, rawBody) { }
}

/// <summary>
/// 422 from a send — every recipient is on this account's suppression list
/// because they hard-bounced or complained before. The attempt is still
/// recorded and still counts against quota. Do not retry: take them off the
/// list, or stop mailing them.
///
/// A send where only <i>some</i> recipients are suppressed does not throw: the
/// rest goes out and the dropped addresses come back in
/// <see cref="SentEmail.Suppressed"/>.
/// </summary>
public sealed class AnnouncerSuppressedRecipientException : AnnouncerUnprocessableException
{
    /// <summary>
    /// Every address the API refused. Prefer this over parsing
    /// <see cref="AnnouncerException.Detail"/>: the API sends it as an RFC 9457
    /// extension member precisely so clients need not read the prose.
    /// </summary>
    public IReadOnlyList<string> Suppressed { get; }

    /// <summary>
    /// The first refused address. Convenience for the single-recipient case;
    /// falls back to the address the SDK sent when the API named none.
    /// </summary>
    public string? Recipient { get; }

    /// <summary>Creates the exception.</summary>
    public AnnouncerSuppressedRecipientException(
        string message, int statusCode, string? title, string? detail, string? requestId, string? rawBody,
        string? recipient, IReadOnlyList<string>? suppressed = null)
        : base(message, statusCode, title, detail, requestId, rawBody)
    {
        Suppressed = suppressed ?? Array.Empty<string>();
        Recipient = Suppressed.Count > 0 ? Suppressed[0] : recipient;
    }
}

/// <summary>429 — a per-second rate limit, or a daily/monthly quota.</summary>
public sealed class AnnouncerRateLimitException : AnnouncerException
{
    /// <summary>How long to wait, from the <c>Retry-After</c> header.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Creates the exception.</summary>
    public AnnouncerRateLimitException(
        string message, int statusCode, string? title, string? detail, string? requestId, string? rawBody,
        TimeSpan? retryAfter)
        : base(message, statusCode, title, detail, requestId, rawBody)
        => RetryAfter = retryAfter;
}

/// <summary>5xx — the API failed. Retried automatically before you see this.</summary>
public sealed class AnnouncerServerException : AnnouncerException
{
    /// <summary>Creates the exception.</summary>
    public AnnouncerServerException(string message, int statusCode, string? title, string? detail, string? requestId, string? rawBody)
        : base(message, statusCode, title, detail, requestId, rawBody) { }
}

/// <summary>The request never got an answer: DNS, TCP, TLS, or the timeout.</summary>
public sealed class AnnouncerConnectionException : AnnouncerException
{
    /// <summary>Creates the exception.</summary>
    public AnnouncerConnectionException(string message, Exception? innerException = null)
        : base(message, innerException: innerException) { }
}

/// <summary>
/// A webhook's <c>X-Announcer-Signature</c> did not check out. Treat the
/// delivery as hostile: do not act on its contents.
/// </summary>
public sealed class AnnouncerSignatureVerificationException : AnnouncerException
{
    /// <summary>Creates the exception.</summary>
    public AnnouncerSignatureVerificationException(string message) : base(message) { }
}

/// <summary>Maps unsuccessful responses onto the right exception type.</summary>
internal static class ErrorFactory
{
    /// <summary>Wording for a status that arrived with no usable body — the bare 404s.</summary>
    private static string GenericMessage(int status) => status switch
    {
        400 => "The request was rejected as invalid.",
        401 => "Invalid or revoked API key.",
        403 => "This API key is not permitted to perform that action.",
        404 => "Not found.",
        409 => "Conflict.",
        422 => "The request could not be processed.",
        429 => "Rate limit exceeded.",
        _ => $"Announcer API returned HTTP {status}.",
    };

    /// <summary>
    /// Picks the most useful sentence out of whichever shape arrived. Order
    /// matters: <c>detail</c> is written for humans, <c>errors</c> is specific
    /// about which field is wrong, and <c>title</c> is generic boilerplate that
    /// only helps when nothing better exists.
    /// </summary>
    private static string MessageFor(int status, ProblemBody? body)
    {
        if (body is not null)
        {
            if (!string.IsNullOrEmpty(body.Detail)) return body.Detail!;
            if (!string.IsNullOrEmpty(body.Error)) return body.Error!;
            if (body.Errors is { Count: > 0 })
            {
                return string.Join("; ", body.Errors.Select(kv => $"{kv.Key}: {string.Join(" ", kv.Value)}"));
            }
            if (!string.IsNullOrEmpty(body.Title)) return body.Title!;
        }
        return GenericMessage(status);
    }

    internal static ProblemBody? ParseBody(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize<ProblemBody>(raw!);
        }
        catch (JsonException)
        {
            // Several 404 handlers send nothing, and a proxy may interpose HTML.
            return null;
        }
    }

    internal static AnnouncerException Create(
        HttpStatusCode statusCode,
        string? rawBody,
        HttpResponseHeaders headers,
        string path,
        string? recipient)
    {
        var status = (int)statusCode;
        var body = ParseBody(rawBody);
        var message = MessageFor(status, body);
        var title = body?.Title;
        var detail = body?.Detail;
        var requestId = headers.RequestId;

        return status switch
        {
            400 => new AnnouncerValidationException(message, status, title, detail, requestId, rawBody, body?.Errors),
            401 => new AnnouncerAuthenticationException(message, status, title, detail, requestId, rawBody),
            403 => new AnnouncerPermissionException(message, status, title, detail, requestId, rawBody),
            404 => new AnnouncerNotFoundException(message, status, title, detail, requestId, rawBody),
            409 => new AnnouncerConflictException(message, status, title, detail, requestId, rawBody),

            // Only the send path can produce a suppression refusal; anything
            // else 422 is a plain unprocessable (domain limit, failed verify).
            422 when path == ApiPaths.Emails
                => new AnnouncerSuppressedRecipientException(
                    message, status, title, detail, requestId, rawBody, recipient, body?.Suppressed),
            422 => new AnnouncerUnprocessableException(message, status, title, detail, requestId, rawBody),

            429 => new AnnouncerRateLimitException(message, status, title, detail, requestId, rawBody, headers.RetryAfter),
            >= 500 => new AnnouncerServerException(message, status, title, detail, requestId, rawBody),
            _ => new AnnouncerException(message, status, title, detail, requestId, rawBody),
        };
    }
}

/// <summary>The response headers this SDK cares about, lifted out for testability.</summary>
internal readonly record struct HttpResponseHeaders(string? RequestId, TimeSpan? RetryAfter);
