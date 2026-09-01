using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Announcer.Tests;

public class WebhookVerifierTests
{
    private const string Secret = "whsec_0123456789abcdef";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private const string Body =
        """{"event":"bounced","occurredAt":"2026-09-01T10:00:00Z","message":{"id":"8f3a0000-0000-0000-0000-000000000001","from":"billing@acme.test","to":"customer@example.com","subject":"Your receipt","status":"bounced"},"detail":{"dsnStatus":"5.1.1 user unknown"}}""";

    /// <summary>Builds the header exactly the way the API's signature_header does.</summary>
    private static string Sign(string body, long timestamp, string secret = Secret)
        => $"t={timestamp},v1={WebhookVerifier.ComputeSignature(secret, timestamp, body)}";

    [Fact]
    public void AcceptsAGoodSignature()
    {
        var evt = WebhookVerifier.Verify(Body, Sign(Body, Now.ToUnixTimeSeconds()), Secret, now: Now);

        Assert.Equal(EventType.Bounced, evt.Event);
        Assert.Equal("customer@example.com", evt.Message.To);
        Assert.Equal("billing@acme.test", evt.Message.From);
        Assert.Equal("5.1.1 user unknown", evt.Detail["dsnStatus"].GetString());
        Assert.Equal(2026, evt.OccurredAt.Year);
    }

    [Fact]
    public void MatchesTheApiDocumentedTestVector()
    {
        // Pinned against announcer's own signature_header unit test, so a change
        // on either side shows up here rather than in production.
        var expected = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes("key"), Encoding.UTF8.GetBytes("1700000000.{}")))
            .ToLowerInvariant();

        Assert.Equal($"t=1700000000,v1={expected}", Sign("{}", 1_700_000_000, "key"));
    }

    [Fact]
    public void RejectsATamperedBody()
    {
        var header = Sign(Body, Now.ToUnixTimeSeconds());
        var tampered = Body.Replace("customer@example.com", "attacker@evil.test", StringComparison.Ordinal);

        var ex = Assert.Throws<AnnouncerSignatureVerificationException>(() =>
            WebhookVerifier.Verify(tampered, header, Secret, now: Now));

        Assert.Contains("does not match", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsTheWrongSecret()
    {
        var header = Sign(Body, Now.ToUnixTimeSeconds());

        Assert.Throws<AnnouncerSignatureVerificationException>(() =>
            WebhookVerifier.Verify(Body, header, "whsec_wrong", now: Now));
    }

    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void RejectsReplaysOutsideTheTolerance(int offsetSeconds)
    {
        var stamp = Now.ToUnixTimeSeconds() + offsetSeconds;

        var ex = Assert.Throws<AnnouncerSignatureVerificationException>(() =>
            WebhookVerifier.Verify(Body, Sign(Body, stamp), Secret, now: Now));

        Assert.Contains("tolerance", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToleranceCanBeDisabled()
    {
        var old = Now.ToUnixTimeSeconds() - 86_400;

        var evt = WebhookVerifier.Verify(Body, Sign(Body, old), Secret, TimeSpan.Zero, Now);

        Assert.Equal(EventType.Bounced, evt.Event);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=123")]
    [InlineData("v1=deadbeef")]
    [InlineData("t=notanumber,v1=x")]
    public void RejectsMalformedHeaders(string header)
    {
        Assert.Throws<AnnouncerSignatureVerificationException>(() =>
            WebhookVerifier.Verify(Body, header, Secret, now: Now));
    }

    [Fact]
    public void IgnoresUnknownSchemes()
    {
        // A future v2 must not break v1 consumers.
        var header = Sign(Body, Now.ToUnixTimeSeconds()) + ",v2=somethingelse";

        var evt = WebhookVerifier.Verify(Body, header, Secret, now: Now);

        Assert.Equal(EventType.Bounced, evt.Event);
    }

    [Fact]
    public void RequiresASecret()
    {
        var header = Sign(Body, Now.ToUnixTimeSeconds());

        Assert.Throws<AnnouncerSignatureVerificationException>(() =>
            WebhookVerifier.Verify(Body, header, "", now: Now));
    }

    [Fact]
    public void IsReachableFromTheClient()
    {
        var (client, _) = TestClient.Create(Array.Empty<StubResponse>());
        var header = Sign(Body, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var evt = client.Webhooks.Verify(Body, header, Secret);

        Assert.Equal(EventType.Bounced, evt.Event);
    }
}

public class ClientAndResourceTests
{
    [Fact]
    public void ConstructorTakesTheKeyPositionally()
    {
        using var client = new AnnouncerClient("ann_positional");

        Assert.Equal(AnnouncerOptions.DefaultBaseUrl, client.BaseUrl);
    }

    [Fact]
    public void MissingKeyExplainsItself()
    {
        var previous = Environment.GetEnvironmentVariable("ANNOUNCER_API_KEY");
        Environment.SetEnvironmentVariable("ANNOUNCER_API_KEY", null);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new AnnouncerClient(new AnnouncerOptions()));
            Assert.Contains("ANNOUNCER_API_KEY", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANNOUNCER_API_KEY", previous);
        }
    }

    [Fact]
    public void TrailingSlashIsTrimmed()
    {
        using var client = new AnnouncerClient(new AnnouncerOptions
        {
            ApiKey = "ann_k",
            BaseUrl = "https://api.example.test/",
        });

        Assert.Equal("https://api.example.test", client.BaseUrl);
    }

    [Fact]
    public async Task UserAgentNamesTheCaller()
    {
        var (client, handler) = TestClient.Create(
            new[] { new StubResponse { Body = """{"sentToday":0}""" } },
            configure: options => options.UserAgent = "acme-billing/2.1");

        await client.GetUsageAsync();

        var ua = handler.Calls[0].Headers["User-Agent"];
        Assert.StartsWith("announcer-dotnet/", ua, StringComparison.Ordinal);
        Assert.EndsWith("acme-billing/2.1", ua, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAnnouncerRegistersAResolvableClient()
    {
        var services = new ServiceCollection();
        services.AddAnnouncer(options =>
        {
            options.ApiKey = "ann_from_di";
            options.BaseUrl = "https://di.example.test";
        });

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<AnnouncerClient>();

        Assert.Equal("https://di.example.test", client.BaseUrl);
        Assert.NotNull(client.Emails);
    }

    [Fact]
    public async Task DomainsCreateReturnsTheRecordToPublish()
    {
        var (client, handler) = TestClient.Create(new[]
        {
            new StubResponse
            {
                Status = HttpStatusCode.Created,
                Body = """
                {"id":"d1","domain":"acme.test","selector":"mail","verified":false,
                 "dns":[{"type":"TXT","name":"mail._domainkey.acme.test",
                         "value":"v=DKIM1; k=rsa; p=MIIBIjANBg","purpose":"DKIM public key."}]}
                """,
            },
        });

        var domain = await client.Domains.CreateAsync("acme.test");

        Assert.Contains("\"domain\":\"acme.test\"", handler.Calls[0].Body!, StringComparison.Ordinal);
        Assert.Equal("mail._domainkey.acme.test", domain.Dns[0].Name);
        Assert.False(domain.Verified);
    }

    [Fact]
    public async Task DomainsListDerivesVerified()
    {
        var (client, _) = TestClient.Create(
            """
            [{"id":"d1","domain":"acme.test","selector":"mail","verified_at":"2026-08-01T09:00:00Z","created_at":"2026-07-01T09:00:00Z"},
             {"id":"d2","domain":"beta.test","selector":"mail","verified_at":null,"created_at":"2026-07-02T09:00:00Z"}]
            """);

        var domains = await client.Domains.ListAsync();

        Assert.True(domains[0].Verified);
        Assert.False(domains[1].Verified);
        Assert.Equal(8, domains[0].VerifiedAt!.Value.Month);
    }

    [Fact]
    public async Task DeleteSendsDeleteAndToleratesTheEmpty204()
    {
        var (client, handler) = TestClient.Create(new[]
        {
            new StubResponse { Status = HttpStatusCode.NoContent, Body = "" },
        });

        await client.Domains.DeleteAsync("d1");

        Assert.Equal(HttpMethod.Delete, handler.Calls[0].Method);
        Assert.Equal("https://api.example.test/v1/domains/d1", handler.Calls[0].Uri.ToString());
    }

    [Fact]
    public async Task ApiKeysCreateDefaultsToFullScope()
    {
        var (client, handler) = TestClient.Create(new[]
        {
            new StubResponse
            {
                Status = HttpStatusCode.Created,
                Body = """{"id":"k1","name":"ci","prefix":"ann_abc123","scope":"full","key":"ann_secret"}""",
            },
        });

        var key = await client.ApiKeys.CreateAsync("ci");

        Assert.Contains("\"scope\":\"full\"", handler.Calls[0].Body!, StringComparison.Ordinal);
        Assert.Equal("ann_secret", key.Key);
    }

    [Fact]
    public async Task ApiKeysAndWebhooksDeriveTheirFlags()
    {
        var (client, _) = TestClient.Create(new[]
        {
            new StubResponse
            {
                Body = """
                [{"id":"k1","name":"old","prefix":"ann_a","scope":"full","created_at":"2026-01-01T00:00:00Z",
                  "last_used_at":"2026-02-01T00:00:00Z","revoked_at":"2026-03-01T00:00:00Z"}]
                """,
            },
            new StubResponse
            {
                Body = """
                [{"id":"w1","url":"https://a.test","created_at":"2026-01-01T00:00:00Z","disabled_at":null},
                 {"id":"w2","url":"https://b.test","created_at":"2026-01-01T00:00:00Z","disabled_at":"2026-02-01T00:00:00Z"}]
                """,
            },
        });

        var keys = await client.ApiKeys.ListAsync();
        Assert.True(keys[0].Revoked);
        Assert.Equal(2, keys[0].LastUsedAt!.Value.Month);

        var endpoints = await client.Webhooks.ListAsync();
        Assert.False(endpoints[0].Disabled);
        Assert.True(endpoints[1].Disabled);
    }

    [Fact]
    public async Task UsageParsesTheCamelCaseResponse()
    {
        var (client, _) = TestClient.Create(
            """
            {"sentToday":12,"dailySendLimit":100,"domains":1,"maxDomains":3,"plan":"free",
             "sentThisPeriod":40,"periodStart":"2026-09-01T00:00:00Z",
             "monthlyIncludedMessages":null,"monthlyHardCap":null,"overageMinorUnits":null,
             "series":[{"date":"2026-09-01","sent":12,"delivered":10,"failed":1}]}
            """);

        var usage = await client.GetUsageAsync();

        Assert.Equal(12, usage.SentToday);
        Assert.Null(usage.MonthlyHardCap);
        Assert.Equal("2026-09-01", usage.Series[0].Date);
    }

    [Fact]
    public async Task SuppressionsList()
    {
        var (client, handler) = TestClient.Create(
            """
            [{"id":"s1","email":"bounced@example.com","reason":"hard bounce (5.1.1 user unknown)",
              "created_at":"2026-08-30T12:00:00Z"}]
            """);

        var suppressions = await client.Suppressions.ListAsync(limit: 25);

        Assert.Equal("https://api.example.test/v1/suppressions?limit=25", handler.Calls[0].Uri.ToString());
        Assert.Equal("bounced@example.com", suppressions[0].Email);
        Assert.Equal(30, suppressions[0].CreatedAt.Day);
    }
}
