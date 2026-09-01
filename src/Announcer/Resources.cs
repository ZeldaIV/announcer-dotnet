using static Announcer.AnnouncerClient;

namespace Announcer;

/// <summary>Sending mail and reading send history. Works with either key scope.</summary>
public sealed class EmailsResource
{
    private readonly AnnouncerClient _client;

    internal EmailsResource(AnnouncerClient client) => _client = client;

    /// <summary>
    /// Sends one email.
    ///
    /// <code>
    /// await announcer.Emails.SendAsync(new SendEmailRequest
    /// {
    ///     From = "Acme &lt;billing@acme.com&gt;",
    ///     To = new[] { "customer@example.com", "partner@example.com" },
    ///     Cc = "accounting@acme.com",
    ///     ReplyTo = "support@acme.com",
    ///     Subject = "Your receipt",
    ///     Text = "Thanks!",
    /// });
    /// </code>
    ///
    /// <c>To</c>, <c>Cc</c> and <c>Bcc</c> each take one address or many.
    /// Everything in To and Cc is one email whose recipients see each other;
    /// Bcc recipients see nobody. At most 50 addresses across the three.
    ///
    /// An <c>Idempotency-Key</c> is generated when
    /// <see cref="SendEmailRequest.IdempotencyKey"/> is null, so the SDK's
    /// automatic retries can never send twice. Set it yourself — an order id, a
    /// job id — to extend that guarantee across process restarts.
    ///
    /// A recipient on your suppression list is dropped and reported in
    /// <see cref="SentEmail.Suppressed"/>; the rest of the message still goes
    /// out. Only when every recipient is suppressed does this throw.
    /// </summary>
    /// <param name="request">The email to send.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="AnnouncerPermissionException">
    /// The <c>From</c> domain is not registered to this account, or not verified.
    /// </exception>
    /// <exception cref="AnnouncerSuppressedRecipientException">
    /// Every recipient hard-bounced or complained before.
    /// </exception>
    /// <exception cref="AnnouncerRateLimitException">
    /// A rate limit or a quota. Quota counts recipients, so one call can
    /// consume several.
    /// </exception>
    public Task<SentEmail> SendAsync(SendEmailRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text) && string.IsNullOrWhiteSpace(request.Html))
        {
            throw new ArgumentException(
                "Provide Text, Html, or both — an email needs a body.", nameof(request));
        }
        if (request.To is null || request.To.Count == 0)
        {
            throw new ArgumentException("Provide at least one To recipient.", nameof(request));
        }

        // Generated when absent so the retry loop below cannot double-send.
        var idempotencyKey = request.IdempotencyKey ?? Guid.NewGuid().ToString("N");

        return _client.RequestAsync<SentEmail>(
            new ApiRequest(HttpMethod.Post, ApiPaths.Emails)
            {
                Body = request,
                Headers = new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey },
                RetryOn409 = true,
                Recipient = request.To.First,
            },
            cancellationToken);
    }

    /// <summary>
    /// Sends the same message to several recipients as <b>separate emails</b>,
    /// one API call each, and returns a result per recipient in the order given.
    ///
    /// This is not the same as putting several addresses in
    /// <see cref="SendEmailRequest.To"/>:
    ///
    /// <list type="bullet">
    /// <item><description><c>To = new[] { a, b }</c> is one email. A and B see
    /// each other in the <c>To:</c> header and it costs one request.</description></item>
    /// <item><description><c>SendManyAsync([a, b], …)</c> is two emails. Neither
    /// knows the other exists, and each gets its own idempotency key and its own
    /// bounce.</description></item>
    /// </list>
    ///
    /// Use this for anything list-shaped — a newsletter, a digest, a fan-out.
    /// A <c>Cc</c> on <paramref name="request"/> is copied on every message, so
    /// a three-recipient batch sends the Cc three copies; that is usually not
    /// what you want.
    /// </summary>
    /// <param name="recipients">The addresses to send to.</param>
    /// <param name="request">
    /// The message. Its <c>To</c> is replaced per recipient; the object you
    /// pass is not modified.
    /// </param>
    /// <param name="stopOnError">
    /// Abandon the remaining recipients after the first failure. Off by
    /// default: one suppressed address should not sink a batch.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IReadOnlyList<BatchSendResult>> SendManyAsync(
        IEnumerable<string> recipients,
        SendEmailRequest request,
        bool stopOnError = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(request);

        var results = new List<BatchSendResult>();
        var index = 0;

        foreach (var recipient in recipients)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Derived rather than shared: one key across the batch would make
            // every recipient after the first an idempotent replay of the
            // first, and only one person would get the mail.
            var key = request.IdempotencyKey is null
                ? null
                : $"{request.IdempotencyKey}-{index}";

            var attempt = request with { To = recipient, IdempotencyKey = key };

            try
            {
                var sent = await SendAsync(attempt, cancellationToken).ConfigureAwait(false);
                results.Add(new BatchSendResult { To = recipient, Result = sent });
            }
            catch (AnnouncerException ex)
            {
                results.Add(new BatchSendResult { To = recipient, Error = ex });
                if (stopOnError) break;
            }

            index++;
        }

        return results;
    }

    /// <summary>Send history, newest first.</summary>
    /// <param name="options">Filters. Null asks for the API's defaults.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task<IReadOnlyList<Message>> ListAsync(
        ListMessagesOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["limit"] = options?.Limit?.ToString(),
            ["status"] = options?.Status,
            ["search"] = options?.Search,
        };

        return _client.RequestAsync<IReadOnlyList<Message>>(
            new ApiRequest(HttpMethod.Get, ApiPaths.Messages) { Query = query },
            cancellationToken);
    }

    /// <summary>The audit trail for one message: every status transition, oldest first.</summary>
    /// <param name="messageId">The message's Announcer id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task<IReadOnlyList<MessageEvent>> GetEventsAsync(
        string messageId,
        CancellationToken cancellationToken = default)
        => _client.RequestAsync<IReadOnlyList<MessageEvent>>(
            new ApiRequest(HttpMethod.Get, $"{ApiPaths.Messages}/{Uri.EscapeDataString(messageId)}/events"),
            cancellationToken);
}

/// <summary>Registering and verifying sending domains. Needs a full-scoped key.</summary>
public sealed class DomainsResource
{
    private readonly AnnouncerClient _client;

    internal DomainsResource(AnnouncerClient client) => _client = client;

    /// <summary>
    /// Registers a sending domain and returns the DNS record to publish.
    ///
    /// Registration generates an RSA keypair, so the API rate-limits it hard —
    /// roughly one a minute. Publish every record in
    /// <see cref="CreatedDomain.Dns"/>, then call <see cref="VerifyAsync"/>.
    /// </summary>
    /// <param name="domain">The domain name, e.g. <c>acme.com</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="AnnouncerConflictException">Already registered.</exception>
    public Task<CreatedDomain> CreateAsync(string domain, CancellationToken cancellationToken = default)
        => _client.RequestAsync<CreatedDomain>(
            new ApiRequest(HttpMethod.Post, ApiPaths.Domains)
            {
                Body = new Dictionary<string, string> { ["domain"] = domain },
            },
            cancellationToken);

    /// <summary>Every domain on the account, newest first.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task<IReadOnlyList<Domain>> ListAsync(CancellationToken cancellationToken = default)
        => _client.RequestAsync<IReadOnlyList<Domain>>(
            new ApiRequest(HttpMethod.Get, ApiPaths.Domains), cancellationToken);

    /// <summary>
    /// The records for a domain, re-derived from the stored public key — so
    /// "what was I supposed to publish?" stays answerable after registration.
    /// </summary>
    /// <param name="domainId">The domain's Announcer id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task<DomainDns> GetDnsAsync(string domainId, CancellationToken cancellationToken = default)
        => _client.RequestAsync<DomainDns>(
            new ApiRequest(HttpMethod.Get, $"{ApiPaths.Domains}/{Uri.EscapeDataString(domainId)}/dns"),
            cancellationToken);

    /// <summary>
    /// Resolves the DKIM record and compares it to the key we issued. This is a
    /// real DNS lookup, not a self-report: it only succeeds on an actual match.
    /// DNS propagation takes minutes to hours — retry rather than re-registering.
    /// </summary>
    /// <param name="domainId">The domain's Announcer id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="AnnouncerUnprocessableException">
    /// The record is missing or does not match.
    /// </exception>
    public Task<DomainVerification> VerifyAsync(string domainId, CancellationToken cancellationToken = default)
        => _client.RequestAsync<DomainVerification>(
            new ApiRequest(HttpMethod.Post, $"{ApiPaths.Domains}/{Uri.EscapeDataString(domainId)}/verify"),
            cancellationToken);

    /// <summary>
    /// Removes the domain and its signing key. Send history survives; sending
    /// from the domain stops immediately.
    /// </summary>
    /// <param name="domainId">The domain's Announcer id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task DeleteAsync(string domainId, CancellationToken cancellationToken = default)
        => _client.RequestAsync(
            new ApiRequest(HttpMethod.Delete, $"{ApiPaths.Domains}/{Uri.EscapeDataString(domainId)}"),
            cancellationToken);
}

/// <summary>Issuing and revoking API keys. Needs a full-scoped key.</summary>
public sealed class ApiKeysResource
{
    private readonly AnnouncerClient _client;

    internal ApiKeysResource(AnnouncerClient client) => _client = client;

    /// <summary>
    /// Issues a key. The secret is in the response and nowhere else — the API
    /// stores only its hash.
    ///
    /// Pass <see cref="KeyScope.Send"/> for anything that only sends mail: a
    /// leaked send key cannot register domains, mint more keys, or reach billing.
    /// </summary>
    /// <param name="name">A name you will recognise in a list.</param>
    /// <param name="scope">One of the <see cref="KeyScope"/> values.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task<CreatedApiKey> CreateAsync(
        string name,
        string scope = KeyScope.Full,
        CancellationToken cancellationToken = default)
        => _client.RequestAsync<CreatedApiKey>(
            new ApiRequest(HttpMethod.Post, ApiPaths.Keys)
            {
                Body = new Dictionary<string, string> { ["name"] = name, ["scope"] = scope },
            },
            cancellationToken);

    /// <summary>Every key on the account. Secrets are never included.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default)
        => _client.RequestAsync<IReadOnlyList<ApiKey>>(
            new ApiRequest(HttpMethod.Get, ApiPaths.Keys), cancellationToken);

    /// <summary>Revokes a key. The row stays, so <c>LastUsedAt</c> remains auditable.</summary>
    /// <param name="keyId">The key's Announcer id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task RevokeAsync(string keyId, CancellationToken cancellationToken = default)
        => _client.RequestAsync(
            new ApiRequest(HttpMethod.Delete, $"{ApiPaths.Keys}/{Uri.EscapeDataString(keyId)}"),
            cancellationToken);
}

/// <summary>Webhook endpoints, and verifying their deliveries.</summary>
public sealed class WebhooksResource
{
    private readonly AnnouncerClient _client;

    internal WebhooksResource(AnnouncerClient client) => _client = client;

    /// <summary>
    /// Registers an endpoint. Maximum two active per account.
    ///
    /// The returned <see cref="CreatedWebhookEndpoint.Secret"/> crosses the
    /// wire exactly once — store it now and pass it to <see cref="Verify"/> on
    /// every delivery.
    /// </summary>
    /// <param name="url">An HTTPS URL Announcer can reach.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task<CreatedWebhookEndpoint> CreateAsync(string url, CancellationToken cancellationToken = default)
        => _client.RequestAsync<CreatedWebhookEndpoint>(
            new ApiRequest(HttpMethod.Post, ApiPaths.Webhooks)
            {
                Body = new Dictionary<string, string> { ["url"] = url },
            },
            cancellationToken);

    /// <summary>Every endpoint on the account, including disabled ones.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task<IReadOnlyList<WebhookEndpoint>> ListAsync(CancellationToken cancellationToken = default)
        => _client.RequestAsync<IReadOnlyList<WebhookEndpoint>>(
            new ApiRequest(HttpMethod.Get, ApiPaths.Webhooks), cancellationToken);

    /// <summary>Disables an endpoint. Pending deliveries stop; history stays auditable.</summary>
    /// <param name="endpointId">The endpoint's Announcer id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task DeleteAsync(string endpointId, CancellationToken cancellationToken = default)
        => _client.RequestAsync(
            new ApiRequest(HttpMethod.Delete, $"{ApiPaths.Webhooks}/{Uri.EscapeDataString(endpointId)}"),
            cancellationToken);

    /// <summary>
    /// Verifies a delivery's signature and returns the parsed event. Pass the
    /// raw request body, not a re-serialised object. Same function as
    /// <see cref="WebhookVerifier.Verify(string, string?, string, TimeSpan?, DateTimeOffset?)"/>,
    /// here for discoverability.
    /// </summary>
    /// <param name="rawBody">The exact bytes Announcer POSTed.</param>
    /// <param name="signatureHeader">The <c>X-Announcer-Signature</c> header.</param>
    /// <param name="secret">The <c>whsec_...</c> secret from <see cref="CreateAsync"/>.</param>
    /// <param name="tolerance">Clock-skew allowance. Default five minutes.</param>
    public WebhookEvent Verify(
        string rawBody,
        string? signatureHeader,
        string secret,
        TimeSpan? tolerance = null)
        => WebhookVerifier.Verify(rawBody, signatureHeader, secret, tolerance);
}

/// <summary>Addresses that hard-bounced or complained.</summary>
public sealed class SuppressionsResource
{
    private readonly AnnouncerClient _client;

    internal SuppressionsResource(AnnouncerClient client) => _client = client;

    /// <summary>Addresses Announcer refuses to send to, newest first.</summary>
    /// <param name="limit">Clamped to 1-200. Null asks for the API's default of 50.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task<IReadOnlyList<Suppression>> ListAsync(
        int? limit = null,
        CancellationToken cancellationToken = default)
        => _client.RequestAsync<IReadOnlyList<Suppression>>(
            new ApiRequest(HttpMethod.Get, ApiPaths.Suppressions)
            {
                Query = new Dictionary<string, string?> { ["limit"] = limit?.ToString() },
            },
            cancellationToken);
}
