using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Announcer;

/// <summary>
/// The Announcer client.
///
/// <code>
/// var announcer = new AnnouncerClient("ann_...");
///
/// await announcer.SendAsync(new SendEmailRequest
/// {
///     From = "Acme &lt;billing@acme.com&gt;",
///     To = "customer@example.com",
///     Subject = "Your receipt",
///     Text = "Thanks for your order.",
/// });
/// </code>
///
/// Register it once and reuse it — it is thread-safe and holds a connection
/// pool. In a host, prefer <c>services.AddAnnouncer(...)</c>.
/// </summary>
public sealed class AnnouncerClient : IDisposable
{
    /// <summary>This SDK's version, reported in the User-Agent.</summary>
    public const string Version = "0.1.0";

    private const int MaxRetryAfterSeconds = 60;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(8);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly TimeSpan _timeout;
    private readonly int _maxRetries;
    private readonly string _userAgent;
    private readonly IReadOnlyDictionary<string, string> _defaultHeaders;

    /// <summary>Sending mail and reading send history. Works with either key scope.</summary>
    public EmailsResource Emails { get; }

    /// <summary>Registering and verifying sending domains. Needs a full-scoped key.</summary>
    public DomainsResource Domains { get; }

    /// <summary>Issuing and revoking API keys. Needs a full-scoped key.</summary>
    public ApiKeysResource ApiKeys { get; }

    /// <summary>Webhook endpoints, and verifying deliveries. Needs a full-scoped key.</summary>
    public WebhooksResource Webhooks { get; }

    /// <summary>Addresses that hard-bounced or complained.</summary>
    public SuppressionsResource Suppressions { get; }

    /// <summary>Creates a client with an explicit key.</summary>
    /// <param name="apiKey">Your <c>ann_...</c> key.</param>
    /// <param name="options">Base URL, timeout, retries. Optional.</param>
    public AnnouncerClient(string apiKey, AnnouncerOptions? options = null)
        : this(Merge(apiKey, options), httpClient: null) { }

    /// <summary>Creates a client from options, reading the environment for anything unset.</summary>
    /// <param name="options">Configuration.</param>
    /// <param name="httpClient">
    /// Your own <see cref="HttpClient"/> — proxies, custom handlers,
    /// instrumentation. When supplied, disposing this client leaves it alone.
    /// </param>
    public AnnouncerClient(AnnouncerOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _apiKey = options.ResolveApiKey();
        _baseUrl = options.ResolveBaseUrl();
        _timeout = options.Timeout;
        _maxRetries = Math.Max(0, options.MaxRetries);
        _userAgent = string.IsNullOrWhiteSpace(options.UserAgent)
            ? $"announcer-dotnet/{Version}"
            : $"announcer-dotnet/{Version} {options.UserAgent}";
        _defaultHeaders = new Dictionary<string, string>(options.DefaultHeaders);

        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();

        Emails = new EmailsResource(this);
        Domains = new DomainsResource(this);
        ApiKeys = new ApiKeysResource(this);
        Webhooks = new WebhooksResource(this);
        Suppressions = new SuppressionsResource(this);
    }

    /// <summary>
    /// The constructor for DI: an <see cref="IHttpClientFactory"/> client plus
    /// bound options.
    /// </summary>
    /// <remarks>
    /// <see cref="ActivatorUtilitiesConstructorAttribute"/> is required, not
    /// decorative: without it the container sees this and
    /// <see cref="AnnouncerClient(AnnouncerOptions, HttpClient?)"/> as equally
    /// applicable — both take an <see cref="HttpClient"/> and one other
    /// resolvable argument — and refuses to pick.
    /// </remarks>
    /// <param name="httpClient">Supplied by the factory.</param>
    /// <param name="options">Bound configuration.</param>
    [ActivatorUtilitiesConstructor]
    public AnnouncerClient(HttpClient httpClient, IOptions<AnnouncerOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), httpClient) { }

    private static AnnouncerOptions Merge(string apiKey, AnnouncerOptions? options)
    {
        var merged = new AnnouncerOptions
        {
            ApiKey = apiKey,
            BaseUrl = options?.BaseUrl,
            Timeout = options?.Timeout ?? TimeSpan.FromSeconds(30),
            MaxRetries = options?.MaxRetries ?? 2,
            UserAgent = options?.UserAgent,
        };
        if (options is not null)
        {
            foreach (var (name, value) in options.DefaultHeaders)
            {
                merged.DefaultHeaders[name] = value;
            }
        }
        return merged;
    }

    /// <summary>The API root this client talks to.</summary>
    public string BaseUrl => _baseUrl;

    /// <summary>Shorthand for <c>Emails.SendAsync</c> — the one call most integrations make.</summary>
    /// <param name="request">The email to send.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task<SentEmail> SendAsync(SendEmailRequest request, CancellationToken cancellationToken = default)
        => Emails.SendAsync(request, cancellationToken);

    /// <summary>Consumption against this account's limits, plus a 14-day sending series.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Usage> GetUsageAsync(CancellationToken cancellationToken = default)
        => await RequestAsync<Usage>(new ApiRequest(HttpMethod.Get, ApiPaths.Usage), cancellationToken)
            .ConfigureAwait(false);

    // -- transport --------------------------------------------------------

    /// <summary>One API call, before retries.</summary>
    internal sealed record ApiRequest(HttpMethod Method, string Path)
    {
        internal object? Body { get; init; }
        internal IReadOnlyDictionary<string, string?>? Query { get; init; }
        internal IReadOnlyDictionary<string, string>? Headers { get; init; }

        /// <summary>
        /// Treat 409 as retryable. Set for sends carrying an Idempotency-Key,
        /// where a 409 means "the original attempt is still in flight" rather
        /// than a real conflict — waiting is exactly the right response.
        /// </summary>
        internal bool RetryOn409 { get; init; }

        /// <summary>Threaded through so a 422 on the send path can name the address.</summary>
        internal string? Recipient { get; init; }
    }

    /// <summary>Statuses worth trying again. Everything else is the caller's problem.</summary>
    private static bool ShouldRetry(HttpStatusCode status, bool retryOn409) => (int)status switch
    {
        408 or 429 => true,
        409 => retryOn409,
        var code => code >= 500,
    };

    /// <summary>
    /// Exponential backoff with full jitter. The jitter is not decoration:
    /// without it, every client that hit the same rate limit retries in
    /// lockstep and hits it again together.
    /// </summary>
    private static TimeSpan Backoff(int attempt)
    {
        var ceiling = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
        if (ceiling > MaxBackoff) ceiling = MaxBackoff;
        return ceiling * Random.Shared.NextDouble();
    }

    /// <summary>
    /// The server's Retry-After beats our guess — it knows when the per-tenant
    /// window actually rolls.
    /// </summary>
    private static TimeSpan DelayFor(HttpResponseMessage response, int attempt)
    {
        var retryAfter = ReadRetryAfter(response);
        if (retryAfter is { } wait && wait > TimeSpan.Zero)
        {
            var capped = TimeSpan.FromSeconds(MaxRetryAfterSeconds);
            return wait > capped ? capped : wait;
        }
        return Backoff(attempt);
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var delta = response.Headers.RetryAfter?.Delta;
        if (delta is not null) return delta;

        // The API sends whole seconds, which HttpClient sometimes surfaces as a
        // date rather than a delta depending on the parse.
        var date = response.Headers.RetryAfter?.Date;
        if (date is not null)
        {
            var wait = date.Value - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    private static HttpResponseHeaders ReadHeaders(HttpResponseMessage response)
    {
        string? requestId = null;
        if (response.Headers.TryGetValues("x-request-id", out var values))
        {
            requestId = values.FirstOrDefault();
        }
        return new HttpResponseHeaders(requestId, ReadRetryAfter(response));
    }

    private Uri BuildUri(ApiRequest request)
    {
        var builder = new StringBuilder(_baseUrl).Append(request.Path);
        if (request.Query is { Count: > 0 })
        {
            var pairs = request.Query
                .Where(kv => kv.Value is not null)
                .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}")
                .ToArray();
            if (pairs.Length > 0)
            {
                builder.Append('?').Append(string.Join("&", pairs));
            }
        }
        return new Uri(builder.ToString());
    }

    /// <summary>Runs a request that returns a body, retrying transient failures.</summary>
    internal async Task<T> RequestAsync<T>(ApiRequest request, CancellationToken cancellationToken)
    {
        var raw = await SendWithRetriesAsync(request, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new AnnouncerException(
                $"The Announcer API returned an empty body where a {typeof(T).Name} was expected.");
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(raw!, JsonOptions);
            if (value is null)
            {
                throw new AnnouncerException(
                    $"The Announcer API returned JSON null where a {typeof(T).Name} was expected.");
            }
            return value;
        }
        catch (JsonException ex)
        {
            throw new AnnouncerException(
                $"Could not read the Announcer API's response as {typeof(T).Name}: {ex.Message}",
                rawBody: raw, innerException: ex);
        }
    }

    /// <summary>Runs a request whose response body is discarded — the 204s.</summary>
    internal async Task RequestAsync(ApiRequest request, CancellationToken cancellationToken)
        => await SendWithRetriesAsync(request, cancellationToken).ConfigureAwait(false);

    private async Task<string?> SendWithRetriesAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        var uri = BuildUri(request);
        string? payload = null;
        if (request.Body is not null)
        {
            payload = JsonSerializer.Serialize(request.Body, request.Body.GetType(), JsonOptions);
        }

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A fresh message per attempt: HttpRequestMessage cannot be reused.
            using var message = new HttpRequestMessage(request.Method, uri);
            message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            message.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            foreach (var (name, value) in _defaultHeaders)
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
            if (request.Headers is not null)
            {
                foreach (var (name, value) in request.Headers)
                {
                    message.Headers.TryAddWithoutValidation(name, value);
                }
            }
            if (payload is not null)
            {
                message.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            }

            // Linked so the per-attempt timeout works whether we own the
            // HttpClient or it came from IHttpClientFactory with its own.
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_timeout);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(message, timeoutSource.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                // The caller cancelling is not a transient failure.
                cancellationToken.ThrowIfCancellationRequested();

                var failure = ex is HttpRequestException
                    ? new AnnouncerConnectionException(
                        $"Could not reach the Announcer API at {_baseUrl}: {ex.Message}", ex)
                    : new AnnouncerConnectionException(
                        $"The request to the Announcer API timed out after {_timeout.TotalSeconds:0.#}s.", ex);

                if (attempt < _maxRetries)
                {
                    await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw failure;
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return response.StatusCode == HttpStatusCode.NoContent ? null : body;
                }

                if (ShouldRetry(response.StatusCode, request.RetryOn409) && attempt < _maxRetries)
                {
                    await Task.Delay(DelayFor(response, attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw ErrorFactory.Create(
                    response.StatusCode, body, ReadHeaders(response), request.Path, request.Recipient);
            }
        }
    }

    /// <summary>Disposes the underlying <see cref="HttpClient"/> if this client created it.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
