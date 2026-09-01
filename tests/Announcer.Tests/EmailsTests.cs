using System.Net;
using System.Text.Json;
using Xunit;

namespace Announcer.Tests;

public class EmailsTests
{
    private static readonly SendEmailRequest Basic = new()
    {
        From = "a@acme.test",
        To = "b@example.com",
        Text = "hi",
    };

    [Fact]
    public async Task SendAsync_PostsTheMessage()
    {
        var (client, handler) = TestClient.Create(
            """{"id":"msg-1","messageId":"<abc@acme.test>","status":"sent"}""");

        var sent = await client.SendAsync(new SendEmailRequest
        {
            From = "Acme <billing@acme.test>",
            To = "customer@example.com",
            Subject = "Your receipt",
            Text = "Thanks!",
        });

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://api.example.test/v1/emails", call.Uri.ToString());
        Assert.Equal($"Bearer {TestClient.ApiKey}", call.Headers["Authorization"]);

        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("Acme <billing@acme.test>", body.RootElement.GetProperty("from").GetString());
        Assert.Equal("customer@example.com", body.RootElement.GetProperty("to").GetString());
        // IdempotencyKey is [JsonIgnore] and must travel as a header only.
        Assert.False(body.RootElement.TryGetProperty("idempotencyKey", out _));

        Assert.Equal("msg-1", sent.Id);
        Assert.Equal("<abc@acme.test>", sent.MessageId);
        Assert.False(sent.IdempotentReplay);
    }

    [Fact]
    public async Task SendAsync_OmitsUnsetOptionalFields()
    {
        var (client, handler) = TestClient.Create("""{"id":"m","status":"sent"}""");

        await client.SendAsync(Basic);

        using var body = JsonDocument.Parse(handler.Calls[0].Body!);
        Assert.False(body.RootElement.TryGetProperty("subject", out _));
        Assert.False(body.RootElement.TryGetProperty("html", out _));
    }

    [Fact]
    public async Task SendAsync_GeneratesAnIdempotencyKey()
    {
        var (client, handler) = TestClient.Create("""{"id":"m","status":"sent"}""");

        await client.SendAsync(Basic);

        var key = handler.Calls[0].Headers["Idempotency-Key"];
        Assert.Equal(32, key.Length);
    }

    [Fact]
    public async Task SendAsync_PassesASuppliedKeyThrough()
    {
        var (client, handler) = TestClient.Create("""{"id":"m","status":"sent"}""");

        await client.SendAsync(Basic with { IdempotencyKey = "order-4711" });

        Assert.Equal("order-4711", handler.Calls[0].Headers["Idempotency-Key"]);
    }

    [Fact]
    public async Task SendAsync_ReportsAnIdempotentReplay()
    {
        var (client, _) = TestClient.Create(
            """{"id":"m","messageId":"<x@a.test>","status":"sent","idempotentReplay":true}""");

        var sent = await client.SendAsync(Basic with { IdempotencyKey = "k" });

        Assert.True(sent.IdempotentReplay);
    }

    [Fact]
    public async Task SendAsync_RefusesAMessageWithNoBodyBeforeSpendingACall()
    {
        var (client, handler) = TestClient.Create(Array.Empty<StubResponse>());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.SendAsync(new SendEmailRequest { From = "a@acme.test", To = "b@example.com", Subject = "x" }));

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task SendAsync_RaisesSuppressedRecipientWithTheAddress()
    {
        var (client, _) = TestClient.Create(new[]
        {
            new StubResponse
            {
                Status = HttpStatusCode.UnprocessableEntity,
                Body = """{"title":"Unprocessable Entity","status":422,"detail":"bounced@example.com is on your suppression list."}""",
            },
        });

        var ex = await Assert.ThrowsAsync<AnnouncerSuppressedRecipientException>(() =>
            client.SendAsync(Basic with { To = "bounced@example.com" }));

        Assert.Equal("bounced@example.com", ex.Recipient);
        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("suppression list", ex.Message, StringComparison.Ordinal);
        // It must still be catchable as the broader kind.
        Assert.IsAssignableFrom<AnnouncerUnprocessableException>(ex);
    }

    [Fact]
    public async Task SendManyAsync_ReportsEachOutcome()
    {
        var (client, handler) = TestClient.Create(new[]
        {
            new StubResponse { Body = """{"id":"m1","status":"sent"}""" },
            new StubResponse
            {
                Status = HttpStatusCode.UnprocessableEntity,
                Body = """{"status":422,"detail":"b@example.com is on your suppression list."}""",
            },
            new StubResponse { Body = """{"id":"m3","status":"sent"}""" },
        });

        var results = await client.Emails.SendManyAsync(
            new[] { "a@example.com", "b@example.com", "c@example.com" },
            new SendEmailRequest { From = "billing@acme.test", To = "placeholder@example.com", Text = "hi" });

        Assert.Equal(3, handler.Calls.Count);
        Assert.Collection(results,
            r => Assert.True(r.Ok),
            r => Assert.False(r.Ok),
            r => Assert.True(r.Ok));
        Assert.Equal("m1", results[0].Result!.Id);
        Assert.IsType<AnnouncerSuppressedRecipientException>(results[1].Error);
        Assert.Equal("b@example.com", results[1].To);
    }

    [Fact]
    public async Task SendManyAsync_DerivesAKeyPerRecipient()
    {
        var (client, handler) = TestClient.Create(new[]
        {
            new StubResponse { Body = """{"id":"m1","status":"sent"}""" },
            new StubResponse { Body = """{"id":"m2","status":"sent"}""" },
        });

        var message = new SendEmailRequest
        {
            From = "billing@acme.test",
            To = "placeholder@example.com",
            Text = "hi",
            IdempotencyKey = "digest-2026-09-01",
        };

        await client.Emails.SendManyAsync(new[] { "a@example.com", "b@example.com" }, message);

        // One key across the batch would make every recipient after the first an
        // idempotent replay of the first, and only one person gets the mail.
        Assert.Equal("digest-2026-09-01-0", handler.Calls[0].Headers["Idempotency-Key"]);
        Assert.Equal("digest-2026-09-01-1", handler.Calls[1].Headers["Idempotency-Key"]);
        // The caller's request must come back untouched.
        Assert.Equal("placeholder@example.com", message.To.First);
        Assert.Equal("digest-2026-09-01", message.IdempotencyKey);
    }

    [Fact]
    public async Task SendManyAsync_CanStopEarly()
    {
        var (client, handler) = TestClient.Create(new[]
        {
            new StubResponse { Status = HttpStatusCode.InternalServerError, Body = """{"status":500,"detail":"boom"}""" },
            new StubResponse { Body = """{"id":"m2","status":"sent"}""" },
        });

        var results = await client.Emails.SendManyAsync(
            new[] { "a@example.com", "b@example.com" },
            new SendEmailRequest { From = "billing@acme.test", To = "x@example.com", Text = "hi" },
            stopOnError: true);

        Assert.Single(results);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task ListAsync_MapsTheSnakeCaseRowOntoFromAndTo()
    {
        var (client, handler) = TestClient.Create(new[]
        {
            new StubResponse
            {
                Body = """
                [{"id":"m1","message_id":"<x@acme.test>","header_from":"billing@acme.test",
                  "recipient":"customer@example.com","recipient_count":3,"reply_to":"support@acme.test",
                  "subject":"Receipt","status":"delivered","created_at":"2026-09-01T10:00:00Z"}]
                """,
            },
        });

        var messages = await client.Emails.ListAsync(new ListMessagesOptions
        {
            Limit = 10,
            Status = MessageStatus.Delivered,
        });

        Assert.Equal(
            "https://api.example.test/v1/messages?limit=10&status=delivered",
            handler.Calls[0].Uri.ToString());

        var message = Assert.Single(messages);
        Assert.Equal("billing@acme.test", message.From);
        Assert.Equal("customer@example.com", message.To);
        Assert.Equal("<x@acme.test>", message.MessageId);
        Assert.Equal(2026, message.CreatedAt.Year);
        // `To` is the primary; the total lives alongside it.
        Assert.Equal(3, message.RecipientCount);
        Assert.Equal("support@acme.test", message.ReplyTo);
    }

    [Fact]
    public async Task ListAsync_OmitsUnsetFilters()
    {
        var (client, handler) = TestClient.Create("[]");

        await client.Emails.ListAsync();

        Assert.Equal("https://api.example.test/v1/messages", handler.Calls[0].Uri.ToString());
    }

    [Fact]
    public async Task GetEventsAsync_LeavesTheFreeFormPayloadAlone()
    {
        var (client, _) = TestClient.Create(
            """
            [{"id":2,"event":"bounced","payload":{"dsn_status":"5.1.1","Retry_Count":3},
              "created_at":"2026-09-01T10:00:00Z"}]
            """);

        var events = await client.Emails.GetEventsAsync("m1");

        var evt = Assert.Single(events);
        Assert.Equal(EventType.Bounced, evt.Event);
        // Tenant data. Rewriting these keys would corrupt real values.
        Assert.Equal("5.1.1", evt.Payload["dsn_status"].GetString());
        Assert.Equal(3, evt.Payload["Retry_Count"].GetInt32());
    }
}

public class RecipientTests
{
    [Fact]
    public async Task SendAsync_CarriesCcBccAndReplyTo()
    {
        var (client, handler) = TestClient.Create(
            """{"id":"m","status":"sent","recipients":4}""");

        var sent = await client.SendAsync(new SendEmailRequest
        {
            From = "billing@acme.test",
            To = new[] { "a@example.com", "b@example.com" },
            Cc = "accounting@acme.test",
            Bcc = new[] { "archive@acme.test", "audit@acme.test" },
            ReplyTo = "support@acme.test",
            Subject = "Your receipt",
            Text = "Thanks!",
        });

        using var body = JsonDocument.Parse(handler.Calls[0].Body!);
        var root = body.RootElement;

        // A single address serialises as a bare string, several as an array —
        // the two shapes the API documents.
        Assert.Equal("accounting@acme.test", root.GetProperty("cc").GetString());
        Assert.Equal("support@acme.test", root.GetProperty("reply_to").GetString());
        Assert.Equal(
            new[] { "a@example.com", "b@example.com" },
            root.GetProperty("to").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(
            new[] { "archive@acme.test", "audit@acme.test" },
            root.GetProperty("bcc").EnumerateArray().Select(e => e.GetString()).ToArray());

        Assert.Equal(4, sent.Recipients);
    }

    [Fact]
    public async Task SendAsync_OmitsHeadersLeftUnset()
    {
        var (client, handler) = TestClient.Create("""{"id":"m","status":"sent"}""");

        await client.SendAsync(new SendEmailRequest
        {
            From = "a@acme.test",
            To = "b@example.com",
            Text = "hi",
        });

        using var body = JsonDocument.Parse(handler.Calls[0].Body!);
        foreach (var absent in new[] { "cc", "bcc", "reply_to" })
        {
            Assert.False(body.RootElement.TryGetProperty(absent, out _), absent);
        }
        // One address still goes out as a bare string, unchanged from before.
        Assert.Equal("b@example.com", body.RootElement.GetProperty("to").GetString());
    }

    [Fact]
    public async Task SendAsync_RefusesAnEmptyRecipientList()
    {
        var (client, handler) = TestClient.Create(Array.Empty<StubResponse>());

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(new SendEmailRequest
        {
            From = "a@acme.test",
            To = Array.Empty<string>(),
            Text = "hi",
        }));

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task SendAsync_ReportsPartiallySuppressedRecipients()
    {
        var (client, _) = TestClient.Create(
            """{"id":"m","status":"sent","recipients":2,"suppressed":["dead@example.com"]}""");

        var sent = await client.SendAsync(new SendEmailRequest
        {
            From = "a@acme.test",
            To = new[] { "good@example.com", "dead@example.com" },
            Cc = "copied@example.com",
            Text = "hi",
        });

        // The message still went out; only the bad address was dropped.
        Assert.Equal(2, sent.Recipients);
        Assert.Equal(new[] { "dead@example.com" }, sent.Suppressed);
    }

    [Fact]
    public async Task FullySuppressedSend_ListsEveryRefusedAddress()
    {
        var (client, _) = TestClient.Create(new[]
        {
            new StubResponse
            {
                Status = System.Net.HttpStatusCode.UnprocessableEntity,
                Body = """{"status":422,"detail":"All 2 recipients are on your suppression list.","suppressed":["one@example.com","two@example.com"]}""",
            },
        });

        var ex = await Assert.ThrowsAsync<AnnouncerSuppressedRecipientException>(() =>
            client.SendAsync(new SendEmailRequest
            {
                From = "a@acme.test",
                To = new[] { "one@example.com", "two@example.com" },
                Text = "hi",
            }));

        // Read from the API's extension member, not parsed out of the prose.
        Assert.Equal(new[] { "one@example.com", "two@example.com" }, ex.Suppressed);
        Assert.Equal("one@example.com", ex.Recipient);
    }

    [Fact]
    public void Addresses_ConvertsImplicitlyFromStringAndArray()
    {
        Addresses one = "a@example.com";
        Addresses many = new[] { "a@example.com", "b@example.com" };

        Assert.Equal(1, one.Count);
        Assert.Equal("a@example.com", one.First);
        Assert.Equal(2, many.Count);
        Assert.Equal("a@example.com, b@example.com", many.ToString());
        Assert.Equal("", new Addresses().First);
    }
}
