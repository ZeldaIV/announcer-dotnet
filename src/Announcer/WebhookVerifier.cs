using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Announcer;

/// <summary>
/// Verifies webhook deliveries. Announcer signs every one with
/// <c>X-Announcer-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt;</c>, where the MAC is
/// HMAC-SHA256 over the literal string <c>"&lt;t&gt;.&lt;raw body&gt;"</c>.
/// </summary>
public static class WebhookVerifier
{
    /// <summary>
    /// How far apart a delivery's timestamp and our clock may be before it is
    /// rejected as a possible replay.
    /// </summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Parses <c>t=&lt;unix&gt;,v1=&lt;hex&gt;</c>, ignoring unknown schemes.</summary>
    private static (long Timestamp, string V1) ParseHeader(string header)
    {
        long? timestamp = null;
        string? v1 = null;

        foreach (var part in header.Split(','))
        {
            var separator = part.IndexOf('=');
            if (separator < 0) continue;

            var name = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();

            if (name == "t" && long.TryParse(value, out var parsed))
            {
                timestamp = parsed;
            }
            else if (name == "v1")
            {
                v1 = value;
            }
        }

        if (timestamp is null || string.IsNullOrEmpty(v1))
        {
            throw new AnnouncerSignatureVerificationException(
                $"Malformed X-Announcer-Signature header: expected \"t=<unix>,v1=<hex>\", got \"{header}\".");
        }
        return (timestamp.Value, v1!);
    }

    /// <summary>
    /// Verifies a delivery and returns the parsed event.
    ///
    /// Pass the <b>raw</b> request body — the exact bytes Announcer sent.
    /// Re-serialising a deserialised object reorders properties and changes
    /// whitespace, and the signature will not match. In ASP.NET Core that means
    /// reading <c>HttpRequest.Body</c> yourself rather than binding a model.
    ///
    /// <code>
    /// app.MapPost("/hooks/announcer", async (HttpRequest request) =>
    /// {
    ///     using var reader = new StreamReader(request.Body);
    ///     var body = await reader.ReadToEndAsync();
    ///
    ///     var evt = WebhookVerifier.Verify(
    ///         body,
    ///         request.Headers["X-Announcer-Signature"],
    ///         secret);
    ///
    ///     return Results.Ok();
    /// });
    /// </code>
    /// </summary>
    /// <param name="rawBody">The exact body Announcer POSTed.</param>
    /// <param name="signatureHeader">The <c>X-Announcer-Signature</c> header.</param>
    /// <param name="secret">The <c>whsec_...</c> secret from <c>Webhooks.CreateAsync</c>.</param>
    /// <param name="tolerance">
    /// Clock-skew allowance. Defaults to <see cref="DefaultTolerance"/>.
    /// <see cref="TimeSpan.Zero"/> disables the check — and with it, replay protection.
    /// </param>
    /// <param name="now">Override the current time. For tests.</param>
    /// <exception cref="AnnouncerSignatureVerificationException">
    /// The header is malformed, the MAC does not match, or the delivery is stale.
    /// </exception>
    public static WebhookEvent Verify(
        string rawBody,
        string? signatureHeader,
        string secret,
        TimeSpan? tolerance = null,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            throw new AnnouncerSignatureVerificationException("Missing X-Announcer-Signature header.");
        }
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new AnnouncerSignatureVerificationException("Missing webhook signing secret.");
        }
        ArgumentNullException.ThrowIfNull(rawBody);

        var (timestamp, v1) = ParseHeader(signatureHeader!);

        // The timestamp is inside the MAC, so this check is what actually stops
        // a captured delivery being replayed at us tomorrow.
        var window = tolerance ?? DefaultTolerance;
        if (window > TimeSpan.Zero)
        {
            var current = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            if (Math.Abs(current - timestamp) > window.TotalSeconds)
            {
                throw new AnnouncerSignatureVerificationException(
                    $"Webhook timestamp is outside the {window.TotalSeconds:0}s tolerance " +
                    $"(signed at {timestamp}, now {current}). Rejecting as a possible replay.");
            }
        }

        var expected = ComputeSignature(secret, timestamp, rawBody);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(v1)))
        {
            throw new AnnouncerSignatureVerificationException(
                "Webhook signature does not match. Check that you are passing the raw request body " +
                "and the secret returned by Webhooks.CreateAsync.");
        }

        try
        {
            var evt = JsonSerializer.Deserialize<WebhookEvent>(rawBody, AnnouncerClient.JsonOptions);
            if (evt is null)
            {
                throw new AnnouncerSignatureVerificationException("Webhook body was JSON null.");
            }
            return evt;
        }
        catch (JsonException ex)
        {
            throw new AnnouncerSignatureVerificationException(
                $"Webhook body is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Computes the hex MAC for a body and timestamp. Exposed because it is the
    /// only sane way to build a signed request in your own tests.
    /// </summary>
    /// <param name="secret">The <c>whsec_...</c> secret.</param>
    /// <param name="unixTimestamp">Seconds since the epoch.</param>
    /// <param name="rawBody">The body being signed.</param>
    public static string ComputeSignature(string secret, long unixTimestamp, string rawBody)
    {
        var payload = Encoding.UTF8.GetBytes($"{unixTimestamp}.{rawBody}");
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
        return Convert.ToHexString(mac).ToLowerInvariant();
    }
}
