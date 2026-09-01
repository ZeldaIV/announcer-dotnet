using System.Diagnostics;
using System.Net;
using Xunit;

namespace Announcer.Tests;

public class ErrorsTests
{
    private static readonly SendEmailRequest Basic = new()
    {
        From = "a@acme.test",
        To = "b@example.com",
        Text = "hi",
    };

    [Fact]
    public async Task Unauthorized_BecomesAuthenticationException()
    {
        var (client, _) = TestClient.Create(new[]
        {
            new StubResponse
            {
                Status = HttpStatusCode.Unauthorized,
                Body = """{"status":401,"detail":"Invalid API key."}""",
            },
        });

        var ex = await Assert.ThrowsAsync<AnnouncerAuthenticationException>(() => client.GetUsageAsync());
        Assert.Equal("Invalid API key.", ex.Message);
    }

    [Fact]
    public async Task Forbidden_KeepsTheDetail()
    {
        var (client, _) = TestClient.Create(new[]
        {
            new StubResponse
            {
                Status = HttpStatusCode.Forbidden,
                Body = """{"status":403,"detail":"Your account is not authorized to send from acme.test. Register the domain first."}""",
            },
        });

        var ex = await Assert.ThrowsAsync<AnnouncerPermissionException>(() => client.SendAsync(Basic));
        Assert.Contains("Register the domain first", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadRequest_ExposesThePerFieldMessages()
    {
        var (client, _) = TestClient.Create(new[]
        {
            new StubResponse
            {
                Status = HttpStatusCode.BadRequest,
                Body = """{"title":"One or more validation errors occurred.","status":400,"errors":{"from":["Not a valid address."]}}""",
            },
        });

        var ex = await Assert.ThrowsAsync<AnnouncerValidationException>(() =>
            client.SendAsync(Basic with { From = "nonsense" }));

        Assert.Equal(new[] { "Not a valid address." }, ex.Errors["from"]);
        // The field name has to survive verbatim.
        Assert.Equal("from: Not a valid address.", ex.Message);
    }

    [Fact]
    public async Task BareErrorKeyShape_IsUnderstood()
    {
        // Two 409 paths answer {"error": "..."} rather than problem+json.
        var (client, _) = TestClient.Create(new[]
        {
            new StubResponse
            {
                Status = HttpStatusCode.Conflict,
                Body = """{"error":"Domain acme.test is already registered."}""",
            },
        });

        var ex = await Assert.ThrowsAsync<AnnouncerConflictException>(() =>
            client.Domains.CreateAsync("acme.test"));

        Assert.Equal("Domain acme.test is already registered.", ex.Message);
    }

    [Fact]
    public async Task NotFound_WithNoBodyAtAll()
    {
        // Several handlers answer NotFound with nothing in the body.
        var (client, _) = TestClient.Create(new[]
        {
            new StubResponse { Status = HttpStatusCode.NotFound, Body = "" },
        });

        var ex = await Assert.ThrowsAsync<AnnouncerNotFoundException>(() =>
            client.Domains.DeleteAsync("missing"));

        Assert.Equal("Not found.", ex.Message);
    }

    [Fact]
    public async Task TooManyRequests_CarriesRetryAfterAndRequestId()
    {
        var (client, _) = TestClient.Create(new[]
        {
            new StubResponse
            {
                Status = HttpStatusCode.TooManyRequests,
                Headers = new Dictionary<string, string>
                {
                    ["Retry-After"] = "3600",
                    ["x-request-id"] = "req_abc123",
                },
                Body = """{"status":429,"detail":"Daily send limit of 100 messages reached."}""",
            },
        });

        var ex = await Assert.ThrowsAsync<AnnouncerRateLimitException>(() => client.SendAsync(Basic));

        Assert.Equal(TimeSpan.FromHours(1), ex.RetryAfter);
        Assert.Equal("req_abc123", ex.RequestId);
    }

    [Fact]
    public async Task UnprocessableOutsideTheSendPath_IsNotASuppression()
    {
        var (client, _) = TestClient.Create(new[]
        {
            new StubResponse
            {
                Status = HttpStatusCode.UnprocessableEntity,
                Body = """{"status":422,"detail":"No TXT record found at mail._domainkey.acme.test."}""",
            },
        });

        var ex = await Assert.ThrowsAsync<AnnouncerUnprocessableException>(() =>
            client.Domains.VerifyAsync("d1"));

        Assert.IsNotType<AnnouncerSuppressedRecipientException>(ex);
    }

    [Fact]
    public async Task ServerError_BecomesServerException()
    {
        var (client, _) = TestClient.Create(new[]
        {
            new StubResponse
            {
                Status = HttpStatusCode.ServiceUnavailable,
                Body = """{"status":503,"detail":"Refusing to send unsigned."}""",
            },
        });

        var ex = await Assert.ThrowsAsync<AnnouncerServerException>(() => client.SendAsync(Basic));
        Assert.Contains("Refusing to send unsigned", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportFailure_BecomesConnectionException()
    {
        var (client, _) = TestClient.Create(new[]
        {
            new StubResponse { Throws = new HttpRequestException("connection refused") },
        });

        var ex = await Assert.ThrowsAsync<AnnouncerConnectionException>(() => client.GetUsageAsync());
        Assert.Contains("Could not reach the Announcer API", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryExceptionIsCatchableAsTheBase()
    {
        var (client, _) = TestClient.Create(new[]
        {
            new StubResponse { Status = HttpStatusCode.Unauthorized, Body = """{"status":401}""" },
        });

        await Assert.ThrowsAnyAsync<AnnouncerException>(() => client.GetUsageAsync());
    }
}

public class RetryTests
{
    private static readonly SendEmailRequest Basic = new()
    {
        From = "a@acme.test",
        To = "b@example.com",
        Text = "hi",
    };

    [Fact]
    public async Task RetriesAServerErrorAndReturnsTheEventualSuccess()
    {
        var (client, handler) = TestClient.Create(
            new[]
            {
                new StubResponse { Status = HttpStatusCode.InternalServerError, Body = """{"status":500}""" },
                new StubResponse { Body = """{"id":"m","status":"sent"}""" },
            },
            maxRetries: 2);

        var sent = await client.SendAsync(Basic);

        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal("m", sent.Id);
    }

    [Fact]
    public async Task RetriesReuseTheSameIdempotencyKey()
    {
        var (client, handler) = TestClient.Create(
            new[]
            {
                new StubResponse { Status = HttpStatusCode.InternalServerError, Body = """{"status":500}""" },
                new StubResponse { Body = """{"id":"m","status":"sent"}""" },
            },
            maxRetries: 2);

        await client.SendAsync(Basic);

        // The whole point: the retry must be recognisable to the API as the
        // same operation, or a timeout on the first attempt would send twice.
        Assert.Equal(
            handler.Calls[0].Headers["Idempotency-Key"],
            handler.Calls[1].Headers["Idempotency-Key"]);
    }

    [Fact]
    public async Task ConflictOnTheSendPath_IsRetried()
    {
        var (client, handler) = TestClient.Create(
            new[]
            {
                new StubResponse
                {
                    Status = HttpStatusCode.Conflict,
                    Body = """{"error":"A request with this Idempotency-Key is already in flight."}""",
                },
                new StubResponse { Body = """{"id":"m","status":"sent","idempotentReplay":true}""" },
            },
            maxRetries: 2);

        var sent = await client.SendAsync(Basic);

        Assert.Equal(2, handler.Calls.Count);
        Assert.True(sent.IdempotentReplay);
    }

    [Fact]
    public async Task ConflictOutsideTheSendPath_IsNotRetried()
    {
        var (client, handler) = TestClient.Create(
            new[]
            {
                new StubResponse
                {
                    Status = HttpStatusCode.Conflict,
                    Body = """{"error":"Domain acme.test is already registered."}""",
                },
            },
            maxRetries: 2);

        await Assert.ThrowsAsync<AnnouncerConflictException>(() => client.Domains.CreateAsync("acme.test"));

        // A duplicate domain is a real conflict, not a transient one.
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task ValidationIsNotRetried()
    {
        var (client, handler) = TestClient.Create(
            new[]
            {
                new StubResponse
                {
                    Status = HttpStatusCode.BadRequest,
                    Body = """{"status":400,"errors":{"from":["Not a valid address."]}}""",
                },
            },
            maxRetries: 2);

        await Assert.ThrowsAsync<AnnouncerValidationException>(() =>
            client.SendAsync(Basic with { From = "junk" }));

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task RetryAfterBeatsTheComputedBackoff()
    {
        var (client, handler) = TestClient.Create(
            new[]
            {
                new StubResponse
                {
                    Status = HttpStatusCode.TooManyRequests,
                    Headers = new Dictionary<string, string> { ["Retry-After"] = "1" },
                    Body = """{"status":429}""",
                },
                new StubResponse { Body = """{"sentToday":1}""" },
            },
            maxRetries: 1);

        var stopwatch = Stopwatch.StartNew();
        await client.GetUsageAsync();
        stopwatch.Stop();

        Assert.Equal(2, handler.Calls.Count);
        // The point is that it waited roughly the second the server asked for
        // rather than its own sub-second guess.
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900),
            $"expected to wait ~1s, waited {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task TransportFailuresAreRetried()
    {
        var (client, handler) = TestClient.Create(
            new[]
            {
                new StubResponse { Throws = new HttpRequestException("boom") },
                new StubResponse { Throws = new HttpRequestException("boom") },
                new StubResponse { Body = """{"sentToday":5}""" },
            },
            maxRetries: 2);

        var usage = await client.GetUsageAsync();

        Assert.Equal(3, handler.Calls.Count);
        Assert.Equal(5, usage.SentToday);
    }

    [Fact]
    public async Task GivesUpOnceTheBudgetIsSpent()
    {
        var (client, handler) = TestClient.Create(
            new[]
            {
                new StubResponse { Status = HttpStatusCode.InternalServerError, Body = """{"status":500}""" },
                new StubResponse { Status = HttpStatusCode.InternalServerError, Body = """{"status":500}""" },
            },
            maxRetries: 1);

        await Assert.ThrowsAsync<AnnouncerServerException>(() => client.GetUsageAsync());

        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task CancellationStopsTheRetryLoop()
    {
        var (client, _) = TestClient.Create(
            new[]
            {
                new StubResponse { Status = HttpStatusCode.InternalServerError, Body = """{"status":500}""" },
                new StubResponse { Status = HttpStatusCode.InternalServerError, Body = """{"status":500}""" },
                new StubResponse { Status = HttpStatusCode.InternalServerError, Body = """{"status":500}""" },
            },
            maxRetries: 5);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetUsageAsync(cts.Token));
    }
}
