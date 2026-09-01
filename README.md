# Announcer

.NET SDK for [Announcer](https://misralo.com) — send transactional email from
your own domain, DKIM-signed.

Targets `net8.0`. Nullable-annotated, XML docs on everything, DI-ready.

```bash
dotnet add package Announcer
```

## Send an email

```csharp
using Announcer;

var announcer = new AnnouncerClient("ann_...");   // or new AnnouncerClient(new AnnouncerOptions())
                                                  // to read ANNOUNCER_API_KEY

await announcer.SendAsync(new SendEmailRequest
{
    From = "Acme <billing@acme.com>",
    To = "customer@example.com",
    Subject = "Your receipt",
    Text = "Thanks for your order.",
    Html = "<p>Thanks for your order.</p>",
});
```

### With dependency injection

```csharp
builder.Services.AddAnnouncer(options =>
{
    options.ApiKey = builder.Configuration["Announcer:ApiKey"];
});
```

Then inject it:

```csharp
public sealed class ReceiptSender(AnnouncerClient announcer)
{
    public Task SendAsync(Order order) => announcer.SendAsync(new SendEmailRequest
    {
        From = "billing@acme.com",
        To = order.CustomerEmail,
        Subject = $"Receipt for order {order.Id}",
        Text = order.AsPlainText(),
        IdempotencyKey = $"receipt-{order.Id}",
    });
}
```

`AddAnnouncer` registers a typed `IHttpClientFactory` client, so you get pooled
handlers and DNS refresh for free. It returns the `IHttpClientBuilder`, so you
can chain a Polly policy or a custom primary handler if you want to.

## Before your first send

You need a registered, verified sending domain — Announcer will not let you
send `From:` a domain you have not proved you control.

```csharp
var domain = await announcer.Domains.CreateAsync("acme.com");

foreach (var record in domain.Dns)
{
    Console.WriteLine($"{record.Type}  {record.Name}  {record.Value}");
}
// TXT  mail._domainkey.acme.com  v=DKIM1; k=rsa; p=MIIBIjANBg...

// Publish that record, wait for DNS, then:
await announcer.Domains.VerifyAsync(domain.Id);
```

One TXT record is the entire ask. SPF and MX stay on Announcer's own bounce
domain, so your root domain's DNS is untouched.

## What the SDK does for you

**Retries are safe.** Every send carries an `Idempotency-Key`, generated per
call when you leave `IdempotencyKey` null. A timeout, a 500, or a 429 gets
retried with exponential backoff and jitter — and because the key travels with
the retry, the API recognises it as the same operation instead of sending
twice. Retries honour your `CancellationToken`.

Set the key yourself to extend that guarantee across process restarts:

```csharp
var sent = await announcer.SendAsync(new SendEmailRequest
{
    From = "billing@acme.com",
    To = "customer@example.com",
    Subject = "Your receipt",
    Text = "Thanks!",
    IdempotencyKey = $"receipt-{order.Id}",   // this order mails exactly once, ever
});

if (sent.IdempotentReplay)
{
    // Already sent earlier. Nothing went out a second time.
}
```

**Exceptions are typed.** Catch the case you can actually handle:

```csharp
try
{
    await announcer.SendAsync(message);
}
catch (AnnouncerSuppressedRecipientException ex)
{
    // They hard-bounced or complained before. Don't retry; mark them inactive.
    await Deactivate(ex.Recipient!);
}
catch (AnnouncerRateLimitException ex)
{
    logger.LogWarning("Rate limited, retry after {Delay}", ex.RetryAfter);
}
catch (AnnouncerPermissionException)
{
    // Domain not registered, not verified, or this key is send-scoped.
}
```

The full set: `AnnouncerValidationException` (with a per-field `.Errors`
dictionary), `AnnouncerAuthenticationException`, `AnnouncerPermissionException`,
`AnnouncerNotFoundException`, `AnnouncerConflictException`,
`AnnouncerUnprocessableException`, `AnnouncerSuppressedRecipientException`,
`AnnouncerRateLimitException`, `AnnouncerServerException`,
`AnnouncerConnectionException`, `AnnouncerSignatureVerificationException`. All
derive from `AnnouncerException`, and every one carries `StatusCode` and
`RequestId`.

**Field names make sense.** The API calls them `header_from` and `recipient`;
the SDK calls them `From` and `To`, matching what you used to send. Timestamps
are `DateTimeOffset`, nullable ones are `DateTimeOffset?`, and the booleans you
want are computed: `domain.Verified`, `key.Revoked`, `endpoint.Disabled`.

`SendEmailRequest` is a record, so a template varies cleanly:

```csharp
var template = new SendEmailRequest { From = "news@acme.com", To = "", Html = body };
var forCustomer = template with { To = customer.Email, Subject = "September update" };
```

`To`, `Cc` and `Bcc` are `Addresses`, which converts implicitly from `string`
and `string[]` — see [Several recipients](#several-recipients).

## Several recipients

`To`, `Cc` and `Bcc` each take one address or many — `Addresses` converts
implicitly from `string` and from `string[]`, so both spellings just work.
Everything in `To` and `Cc` is **one email** whose recipients see each other;
`Bcc` recipients see nobody, not even each other:

```csharp
await announcer.SendAsync(new SendEmailRequest
{
    From = "billing@acme.com",
    To = new[] { "customer@example.com", "partner@example.com" },
    Cc = "accounting@acme.com",
    Bcc = "archive@acme.com",
    ReplyTo = "support@acme.com",
    Subject = "Your receipt",
    Text = "Thanks!",
});
```

At most 50 addresses across the three. `ReplyTo` is a header only — it costs
nothing and cannot bounce.

**Recipients are the billable unit.** That call counts four against your quota,
not one. It is also what keeps `MonthlyHardCap` meaningful: otherwise a leaked
key could send fifty times your ceiling by padding the array.

### One email, or many?

For anything list-shaped — a newsletter, a digest, a fan-out — you want
`SendManyAsync`, not an array:

```csharp
var results = await announcer.Emails.SendManyAsync(
    new[] { "a@example.com", "b@example.com", "c@example.com" },
    new SendEmailRequest
    {
        From = "news@acme.com",
        To = "",                 // replaced per recipient
        Subject = "September update",
        Html = body,
    });

foreach (var result in results.Where(r => !r.Ok))
{
    logger.LogWarning("{Recipient} failed: {Error}", result.To, result.Error!.Message);
}
```

|  | `To = new[] { a, b }` | `SendManyAsync([a, b], …)` |
|---|---|---|
| Emails sent | one | two |
| Do they see each other? | yes, in `To:` | no |
| API requests | one | two |
| Idempotency key | one | one each, derived |
| One address fails | the send reports it | the others are unaffected |

Each `SendManyAsync` recipient gets its own derived idempotency key, and your
request object is left untouched.

### Suppressed recipients

A recipient on your suppression list is dropped and the rest still goes out:

```csharp
var sent = await announcer.SendAsync(new SendEmailRequest
{
    From = "billing@acme.com",
    To = new[] { "good@example.com", "bounced-before@example.com" },
    Subject = "Your receipt",
    Text = "Thanks!",
});

sent.Recipients;  // 1 — what actually went out and what you were billed
sent.Suppressed;  // ["bounced-before@example.com"]
```

`AnnouncerSuppressedRecipientException` is thrown only when *every* recipient is
suppressed (or every `To` recipient — a message with no visible primary
recipient is refused rather than sent). Its `.Suppressed` list names them all.

## Webhooks

Register an endpoint, store the secret, verify every delivery:

```csharp
var endpoint = await announcer.Webhooks.CreateAsync("https://acme.com/hooks/announcer");
logger.LogInformation("{Secret}", endpoint.Secret); // whsec_... — shown once, store it now
```

```csharp
app.MapPost("/hooks/announcer", async (HttpRequest request, IConfiguration config) =>
{
    // The raw bytes are what was signed. Binding a model and re-serialising
    // reorders properties and the signature will not match.
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();

    WebhookEvent evt;
    try
    {
        evt = WebhookVerifier.Verify(
            body,
            request.Headers["X-Announcer-Signature"],
            config["Announcer:WebhookSecret"]!);
    }
    catch (AnnouncerSignatureVerificationException)
    {
        return Results.BadRequest();
    }

    switch (evt.Event)
    {
        case EventType.Delivered:
            await MarkDelivered(evt.Message.Id);
            break;
        case EventType.Bounced:
        case EventType.Complained:
            await Deactivate(evt.Message.To);
            break;
    }

    return Results.Ok();
});
```

Verification checks the HMAC **and** the timestamp, rejecting anything more
than five minutes old so a captured delivery cannot be replayed at you. Pass a
`tolerance` to tune it.

`WebhookVerifier.ComputeSignature` is public so you can build signed requests
in your own integration tests.

Events: `EventType.Sent`, `Delivered`, `Bounced`, `Complained`, `Suppressed`.

## API reference

### Client

```csharp
new AnnouncerClient(string apiKey, AnnouncerOptions? options = null)
new AnnouncerClient(AnnouncerOptions options, HttpClient? httpClient = null)
```

| Option | Default |
|--------|---------|
| `ApiKey` | `ANNOUNCER_API_KEY` |
| `BaseUrl` | `ANNOUNCER_BASE_URL`, then `https://mail.misralo.com` |
| `Timeout` | 30 seconds, per attempt |
| `MaxRetries` | 2 extra attempts |
| `UserAgent` | appended to the SDK's own — name your app |
| `DefaultHeaders` | added to every request |

### Methods

Every async method takes an optional `CancellationToken`.

| Call | Does |
|------|------|
| `SendAsync(msg)` | Shorthand for `Emails.SendAsync`. |
| `GetUsageAsync()` | Quota consumption plus a 14-day sending series. |
| `Emails.SendAsync(msg)` | Sends one email. `To`/`Cc`/`Bcc` take one address or many. |
| `Emails.SendManyAsync(to, msg, stopOnError)` | Separate emails, one per recipient. |
| `Emails.ListAsync(options)` | Send history. |
| `Emails.GetEventsAsync(id)` | A message's audit trail. |
| `Domains.CreateAsync(domain)` | Registers a domain, returns the DNS record. |
| `Domains.ListAsync()` | Every domain on the account. |
| `Domains.GetDnsAsync(id)` | The records again, for a domain you already registered. |
| `Domains.VerifyAsync(id)` | Resolves DNS and checks the published key. |
| `Domains.DeleteAsync(id)` | Removes the domain and its signing key. |
| `ApiKeys.CreateAsync(name, scope)` | Issues a key. Secret shown once. |
| `ApiKeys.ListAsync()` | Every key, without secrets. |
| `ApiKeys.RevokeAsync(id)` | Revokes a key; history survives. |
| `Webhooks.CreateAsync(url)` | Registers an endpoint. Max 2 active. |
| `Webhooks.ListAsync()` | Every endpoint. |
| `Webhooks.DeleteAsync(id)` | Disables an endpoint. |
| `WebhookVerifier.Verify(body, header, secret)` | Verifies a delivery. |
| `Suppressions.ListAsync(limit)` | Addresses that bounced or complained. |

`Domains`, `ApiKeys` and `Webhooks` need a `full`-scoped key. Everything else
works with a `send` key too — give integrations `send`.

## Scopes

Mint a `send`-scoped key for anything that only sends mail:

```csharp
var key = await announcer.ApiKeys.CreateAsync("production-worker", KeyScope.Send);
```

A leaked send key cannot register domains, mint successor keys, or touch
billing. It is the difference between an incident and a catastrophe.

## Local development

Point the SDK at a local Announcer stack:

```csharp
var announcer = new AnnouncerClient(new AnnouncerOptions
{
    ApiKey = "ann_dev_0000000000000000000000000000",
    BaseUrl = "http://localhost:8080",
});
```

## Contributing

```bash
dotnet build     # warnings are errors
dotnet test      # 59 tests, no network
```

## License

MIT
