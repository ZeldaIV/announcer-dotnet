using System.Net;
using System.Text;

namespace Announcer.Tests;

/// <summary>One canned reply in a queue.</summary>
public sealed record StubResponse
{
    /// <summary>HTTP status. Defaults to 200.</summary>
    public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

    /// <summary>The response body, verbatim.</summary>
    public string Body { get; init; } = "";

    /// <summary>Extra response headers.</summary>
    public IDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>When set, the handler throws this instead of replying.</summary>
    public Exception? Throws { get; init; }
}

/// <summary>What the client actually sent.</summary>
public sealed record RecordedCall(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    string? Body);

/// <summary>
/// An <see cref="HttpMessageHandler"/> that plays back a queue of responses and
/// records every request, so the real client code — retries, headers, error
/// parsing — is exercised without a network.
/// </summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<StubResponse> _responses;
    private readonly List<RecordedCall> _calls = new();

    public StubHandler(params StubResponse[] responses) => _responses = new Queue<StubResponse>(responses);

    public IReadOnlyList<RecordedCall> Calls => _calls;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers)
        {
            headers[name] = string.Join(",", values);
        }

        _calls.Add(new RecordedCall(request.Method, request.RequestUri!, headers, body));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                $"StubHandler: unexpected call number {_calls.Count} to {request.RequestUri}");
        }

        var next = _responses.Dequeue();
        if (next.Throws is not null) throw next.Throws;

        var response = new HttpResponseMessage(next.Status)
        {
            Content = new StringContent(next.Body, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in next.Headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }
}

/// <summary>Builds clients wired to a stub, with retries off unless a test asks.</summary>
public static class TestClient
{
    public const string ApiKey = "ann_test_key";
    public const string BaseUrl = "https://api.example.test";

    public static (AnnouncerClient Client, StubHandler Handler) Create(
        StubResponse[] responses,
        int maxRetries = 0,
        Action<AnnouncerOptions>? configure = null)
    {
        var handler = new StubHandler(responses);
        var options = new AnnouncerOptions
        {
            ApiKey = ApiKey,
            BaseUrl = BaseUrl,
            MaxRetries = maxRetries,
        };
        configure?.Invoke(options);

        var client = new AnnouncerClient(options, new HttpClient(handler));
        return (client, handler);
    }

    /// <summary>Convenience for the common single-response case.</summary>
    public static (AnnouncerClient Client, StubHandler Handler) Create(string body)
        => Create(new[] { new StubResponse { Body = body } });
}
