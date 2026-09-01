namespace Announcer;

/// <summary>
/// Configuration for <see cref="AnnouncerClient"/>. Bind it from configuration
/// when you register the client with <c>AddAnnouncer</c>.
/// </summary>
public sealed class AnnouncerOptions
{
    /// <summary>Where the hosted API lives.</summary>
    public const string DefaultBaseUrl = "https://mail.misralo.com";

    /// <summary>
    /// Your <c>ann_...</c> key. Falls back to the <c>ANNOUNCER_API_KEY</c>
    /// environment variable when left null.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// API root. Falls back to <c>ANNOUNCER_BASE_URL</c>, then
    /// <see cref="DefaultBaseUrl"/>. Point it at <c>http://localhost:8080</c>
    /// for a local stack.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Per-attempt timeout. Default 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Extra attempts after a retryable failure. Default 2, so three attempts
    /// in all. Zero disables retries.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Appended to the SDK's own User-Agent. Name your application here — it
    /// is what support will look for.
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>Headers added to every request.</summary>
    public IDictionary<string, string> DefaultHeaders { get; } = new Dictionary<string, string>();

    /// <summary>Resolves the key from the options or the environment.</summary>
    internal string ResolveApiKey()
    {
        var key = ApiKey ?? Environment.GetEnvironmentVariable("ANNOUNCER_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "No Announcer API key. Pass one to the constructor — new AnnouncerClient(\"ann_...\") — " +
                "set AnnouncerOptions.ApiKey, or set the ANNOUNCER_API_KEY environment variable.");
        }
        return key!;
    }

    /// <summary>Resolves the base URL from the options, the environment, or the default.</summary>
    internal string ResolveBaseUrl()
    {
        var url = BaseUrl
            ?? Environment.GetEnvironmentVariable("ANNOUNCER_BASE_URL")
            ?? DefaultBaseUrl;
        return url.TrimEnd('/');
    }
}
