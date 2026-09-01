using System.Text.Json;
using System.Text.Json.Serialization;

namespace Announcer;

/// <summary>The API paths this SDK calls.</summary>
internal static class ApiPaths
{
    internal const string Emails = "/v1/emails";
    internal const string Messages = "/v1/messages";
    internal const string Usage = "/v1/usage";
    internal const string Suppressions = "/v1/suppressions";
    internal const string Domains = "/v1/domains";
    internal const string Keys = "/v1/keys";
    internal const string Webhooks = "/v1/webhooks";
}

/// <summary>Where a message currently sits.</summary>
public static class MessageStatus
{
    /// <summary>Reserved, not yet handed to the MTA.</summary>
    public const string Queued = "queued";
    /// <summary>Accepted by the MTA.</summary>
    public const string Sent = "sent";
    /// <summary>The receiving server accepted it.</summary>
    public const string Delivered = "delivered";
    /// <summary>Permanently rejected. The recipient is now suppressed.</summary>
    public const string Bounced = "bounced";
    /// <summary>Reported as spam. The recipient is now suppressed.</summary>
    public const string Complained = "complained";
    /// <summary>Could not be sent.</summary>
    public const string Failed = "failed";
    /// <summary>Refused because the recipient was already suppressed.</summary>
    public const string Suppressed = "suppressed";
}

/// <summary>The transitions that produce a webhook delivery.</summary>
public static class EventType
{
    /// <summary>Handed to the MTA.</summary>
    public const string Sent = "sent";
    /// <summary>The receiving server accepted it.</summary>
    public const string Delivered = "delivered";
    /// <summary>Permanently rejected.</summary>
    public const string Bounced = "bounced";
    /// <summary>Reported as spam.</summary>
    public const string Complained = "complained";
    /// <summary>Refused because the recipient was suppressed.</summary>
    public const string Suppressed = "suppressed";
}

/// <summary>What an API key may do.</summary>
public static class KeyScope
{
    /// <summary>Everything, including domains, keys, webhooks and billing.</summary>
    public const string Full = "full";
    /// <summary>Sending mail and reading messages only. Prefer this for integrations.</summary>
    public const string Send = "send";
}

/// <summary>
/// An email to send. <see cref="From"/> and <see cref="To"/> accept either a
/// bare address (<c>billing@acme.com</c>) or a display name
/// (<c>Acme Billing &lt;billing@acme.com&gt;</c>). At least one of
/// <see cref="Text"/> or <see cref="Html"/> is required.
///
/// A record, so a template message can be varied with <c>with</c>:
/// <code>
/// var receipt = template with { To = customer.Email, Subject = $"Receipt {order.Id}" };
/// </code>
/// </summary>
public sealed record SendEmailRequest
{
    /// <summary>The sender. Its domain must be registered to this account.</summary>
    [JsonPropertyName("from")]
    public required string From { get; init; }

    /// <summary>
    /// The primary recipients. Several addresses go out as one email and see
    /// each other in the <c>To:</c> header — for separate emails that share
    /// nothing, use <c>Emails.SendManyAsync</c>.
    /// </summary>
    [JsonPropertyName("to")]
    public required Addresses To { get; init; }

    /// <summary>Carbon copies. Visible to every other recipient.</summary>
    [JsonPropertyName("cc")]
    public Addresses? Cc { get; init; }

    /// <summary>
    /// Blind copies. They receive the message; nobody — including the other
    /// blind copies — sees that they did.
    /// </summary>
    [JsonPropertyName("bcc")]
    public Addresses? Bcc { get; init; }

    /// <summary>
    /// Where replies should go. A header only: no delivery, nothing billable,
    /// nothing that can bounce.
    /// </summary>
    [JsonPropertyName("reply_to")]
    public Addresses? ReplyTo { get; init; }

    /// <summary>The subject line.</summary>
    [JsonPropertyName("subject")]
    public string? Subject { get; init; }

    /// <summary>Plain-text body. Supply it even alongside HTML — filters like seeing both.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>HTML body.</summary>
    [JsonPropertyName("html")]
    public string? Html { get; init; }

    /// <summary>
    /// Makes the send exactly-once. Left null, the SDK generates one per call
    /// so its own retries cannot double-send; set it yourself — an order id, a
    /// job id — to keep that guarantee across process restarts. Travels as a
    /// header, never in the body.
    /// </summary>
    [JsonIgnore]
    public string? IdempotencyKey { get; init; }
}

/// <summary>The result of a successful send.</summary>
public sealed record SentEmail
{
    /// <summary>Announcer's id for the message. Use it with <c>Emails.GetEventsAsync</c>.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>The RFC 5322 Message-ID the MTA assigned.</summary>
    [JsonPropertyName("messageId")] public string? MessageId { get; init; }

    /// <summary>One of the <see cref="MessageStatus"/> values.</summary>
    [JsonPropertyName("status")] public string Status { get; init; } = "";

    /// <summary>
    /// True when this idempotency key had already been used: nothing was sent a
    /// second time and these are the original send's details.
    /// </summary>
    [JsonPropertyName("idempotentReplay")] public bool IdempotentReplay { get; init; }

    /// <summary>
    /// How many addresses the message went to, across To, Cc and Bcc. This is
    /// the number billed and counted against quota.
    /// </summary>
    [JsonPropertyName("recipients")] public int Recipients { get; init; } = 1;

    /// <summary>
    /// Addresses dropped because they are on your suppression list. Empty on a
    /// clean send — the rest of the message still went out. Only when every
    /// recipient is suppressed does the send fail outright.
    /// </summary>
    [JsonPropertyName("suppressed")]
    public IReadOnlyList<string> Suppressed { get; init; } = Array.Empty<string>();
}

/// <summary>One row of send history.</summary>
public sealed record Message
{
    /// <summary>Announcer's id for the message.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>RFC 5322 Message-ID, null for messages that never reached the MTA.</summary>
    [JsonPropertyName("message_id")] public string? MessageId { get; init; }

    /// <summary>
    /// The <c>From:</c> header that went out. The API calls this
    /// <c>header_from</c>; the SDK uses the same word you used to send.
    /// </summary>
    [JsonPropertyName("header_from")] public string From { get; init; } = "";

    /// <summary>
    /// The primary recipient — the first <c>To</c> address. A message with Cc,
    /// Bcc or several To addresses reports its first here and the total in
    /// <see cref="RecipientCount"/>.
    /// </summary>
    [JsonPropertyName("recipient")] public string To { get; init; } = "";

    /// <summary>How many addresses the message went to, across To, Cc and Bcc.</summary>
    [JsonPropertyName("recipient_count")] public int RecipientCount { get; init; } = 1;

    /// <summary>The <c>Reply-To:</c> header that went out, if any.</summary>
    [JsonPropertyName("reply_to")] public string? ReplyTo { get; init; }

    /// <summary>The subject line, if one was set.</summary>
    [JsonPropertyName("subject")] public string? Subject { get; init; }

    /// <summary>
    /// The rolled-up status, one of the <see cref="MessageStatus"/> values. One
    /// bounced recipient makes the whole message <c>bounced</c> — it is the
    /// thing you have to act on.
    /// </summary>
    [JsonPropertyName("status")] public string Status { get; init; } = "";

    /// <summary>When the send was attempted.</summary>
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Filters for <c>Emails.ListAsync</c>. Nulls are omitted.</summary>
public sealed record ListMessagesOptions
{
    /// <summary>Clamped to 1-200 by the API. Null means the API default of 50.</summary>
    public int? Limit { get; init; }

    /// <summary>One of the <see cref="MessageStatus"/> values.</summary>
    public string? Status { get; init; }

    /// <summary>Matches a substring of the recipient address.</summary>
    public string? Search { get; init; }
}

/// <summary>One entry in a message's audit trail.</summary>
public sealed record MessageEvent
{
    /// <summary>Monotonic event id.</summary>
    [JsonPropertyName("id")] public long Id { get; init; }

    /// <summary>One of the <see cref="EventType"/> values.</summary>
    [JsonPropertyName("event")] public string Event { get; init; } = "";

    /// <summary>
    /// Event-specific data, left exactly as the API sent it — these keys are
    /// tenant data and must not be rewritten.
    /// </summary>
    [JsonPropertyName("payload")] public IReadOnlyDictionary<string, JsonElement> Payload { get; init; }
        = new Dictionary<string, JsonElement>();

    /// <summary>When the transition happened.</summary>
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A DNS record the domain owner has to publish.</summary>
public sealed record DnsRecord
{
    /// <summary>Always <c>TXT</c> today.</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = "TXT";

    /// <summary>The record name, e.g. <c>mail._domainkey.acme.com</c>.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>The record value, e.g. <c>v=DKIM1; k=rsa; p=MIIBIjANBg...</c>.</summary>
    [JsonPropertyName("value")] public string Value { get; init; } = "";

    /// <summary>Why the record exists, in a sentence you can show a user.</summary>
    [JsonPropertyName("purpose")] public string Purpose { get; init; } = "";
}

/// <summary>A sending domain.</summary>
public sealed record Domain
{
    /// <summary>Announcer's id for the domain.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>The domain name.</summary>
    [JsonPropertyName("domain")] public string Name { get; init; } = "";

    /// <summary>The DKIM selector. Always <c>mail</c>.</summary>
    [JsonPropertyName("selector")] public string Selector { get; init; } = "mail";

    /// <summary>When verification first succeeded, or null.</summary>
    [JsonPropertyName("verified_at")] public DateTimeOffset? VerifiedAt { get; init; }

    /// <summary>When the domain was registered.</summary>
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Whether the DKIM record has been seen in DNS and matched.</summary>
    [JsonIgnore] public bool Verified => VerifiedAt is not null;
}

/// <summary>A freshly registered domain, including the record to publish.</summary>
public sealed record CreatedDomain
{
    /// <summary>Announcer's id for the domain.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>The domain name.</summary>
    [JsonPropertyName("domain")] public string Name { get; init; } = "";

    /// <summary>The DKIM selector.</summary>
    [JsonPropertyName("selector")] public string Selector { get; init; } = "mail";

    /// <summary>False until the record is published and verified.</summary>
    [JsonPropertyName("verified")] public bool Verified { get; init; }

    /// <summary>Publish every record here, then call <c>Domains.VerifyAsync</c>.</summary>
    [JsonPropertyName("dns")] public IReadOnlyList<DnsRecord> Dns { get; init; } = Array.Empty<DnsRecord>();
}

/// <summary>The records for an existing domain, re-derived from the stored key.</summary>
public sealed record DomainDns
{
    /// <summary>Announcer's id for the domain.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>The domain name.</summary>
    [JsonPropertyName("domain")] public string Name { get; init; } = "";

    /// <summary>Whether the record has been verified.</summary>
    [JsonPropertyName("verified")] public bool Verified { get; init; }

    /// <summary>The records to publish.</summary>
    [JsonPropertyName("dns")] public IReadOnlyList<DnsRecord> Dns { get; init; } = Array.Empty<DnsRecord>();
}

/// <summary>The outcome of a verification attempt.</summary>
public sealed record DomainVerification
{
    /// <summary>The domain's id.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>True when the published record matched the key we issued.</summary>
    [JsonPropertyName("verified")] public bool Verified { get; init; }
}

/// <summary>An API key, minus the secret.</summary>
public sealed record ApiKey
{
    /// <summary>Announcer's id for the key.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>The name you gave it.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>The first few characters, for telling keys apart in a list.</summary>
    [JsonPropertyName("prefix")] public string Prefix { get; init; } = "";

    /// <summary>One of the <see cref="KeyScope"/> values.</summary>
    [JsonPropertyName("scope")] public string Scope { get; init; } = KeyScope.Full;

    /// <summary>When the key was issued.</summary>
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the key was last used to authenticate, or null.</summary>
    [JsonPropertyName("last_used_at")] public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>When the key was revoked, or null.</summary>
    [JsonPropertyName("revoked_at")] public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>Whether the key has been revoked.</summary>
    [JsonIgnore] public bool Revoked => RevokedAt is not null;
}

/// <summary>A newly minted key. <see cref="Key"/> is shown once and never again.</summary>
public sealed record CreatedApiKey
{
    /// <summary>Announcer's id for the key.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>The name you gave it.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>The first few characters of the key.</summary>
    [JsonPropertyName("prefix")] public string Prefix { get; init; } = "";

    /// <summary>One of the <see cref="KeyScope"/> values.</summary>
    [JsonPropertyName("scope")] public string Scope { get; init; } = KeyScope.Full;

    /// <summary>The full secret. Only ever present here — the API stores only its hash.</summary>
    [JsonPropertyName("key")] public string Key { get; init; } = "";
}

/// <summary>A registered webhook endpoint.</summary>
public sealed record WebhookEndpoint
{
    /// <summary>Announcer's id for the endpoint.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>The URL deliveries are POSTed to.</summary>
    [JsonPropertyName("url")] public string Url { get; init; } = "";

    /// <summary>When the endpoint was registered.</summary>
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the endpoint was disabled, or null.</summary>
    [JsonPropertyName("disabled_at")] public DateTimeOffset? DisabledAt { get; init; }

    /// <summary>Whether the endpoint has been disabled.</summary>
    [JsonIgnore] public bool Disabled => DisabledAt is not null;
}

/// <summary>A newly registered endpoint. <see cref="Secret"/> is shown once.</summary>
public sealed record CreatedWebhookEndpoint
{
    /// <summary>Announcer's id for the endpoint.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>The URL deliveries are POSTed to.</summary>
    [JsonPropertyName("url")] public string Url { get; init; } = "";

    /// <summary>The <c>whsec_...</c> secret to pass to <c>WebhookVerifier.Verify</c>.</summary>
    [JsonPropertyName("secret")] public string Secret { get; init; } = "";
}

/// <summary>An address Announcer refuses to send to.</summary>
public sealed record Suppression
{
    /// <summary>Announcer's id for the entry.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>The suppressed address.</summary>
    [JsonPropertyName("email")] public string Email { get; init; } = "";

    /// <summary>Why, e.g. <c>hard bounce (5.1.1 user unknown)</c>.</summary>
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";

    /// <summary>When it was added.</summary>
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>One day of the 14-day sending series.</summary>
public sealed record UsagePoint
{
    /// <summary>The day, as <c>YYYY-MM-DD</c>.</summary>
    [JsonPropertyName("date")] public string Date { get; init; } = "";

    /// <summary>Messages attempted that day.</summary>
    [JsonPropertyName("sent")] public long Sent { get; init; }

    /// <summary>Of those, how many were confirmed delivered.</summary>
    [JsonPropertyName("delivered")] public long Delivered { get; init; }

    /// <summary>Bounced + complained + failed. A subset of <see cref="Sent"/>.</summary>
    [JsonPropertyName("failed")] public long Failed { get; init; }
}

/// <summary>Consumption against this account's limits.</summary>
public sealed record Usage
{
    /// <summary>Messages attempted in the last 24 hours.</summary>
    [JsonPropertyName("sentToday")] public long SentToday { get; init; }

    /// <summary>The daily anti-burst ceiling.</summary>
    [JsonPropertyName("dailySendLimit")] public int DailySendLimit { get; init; }

    /// <summary>Registered domains.</summary>
    [JsonPropertyName("domains")] public long Domains { get; init; }

    /// <summary>The domain ceiling.</summary>
    [JsonPropertyName("maxDomains")] public int MaxDomains { get; init; }

    /// <summary>The account's plan id.</summary>
    [JsonPropertyName("plan")] public string Plan { get; init; } = "";

    /// <summary>Messages attempted in the current billing period.</summary>
    [JsonPropertyName("sentThisPeriod")] public long SentThisPeriod { get; init; }

    /// <summary>When the current billing period began.</summary>
    [JsonPropertyName("periodStart")] public DateTimeOffset PeriodStart { get; init; }

    /// <summary>Null on plans with no monthly accounting.</summary>
    [JsonPropertyName("monthlyIncludedMessages")] public int? MonthlyIncludedMessages { get; init; }

    /// <summary>The spend ceiling. Null when uncapped.</summary>
    [JsonPropertyName("monthlyHardCap")] public int? MonthlyHardCap { get; init; }

    /// <summary>What the current period's overage would cost, in minor units.</summary>
    [JsonPropertyName("overageMinorUnits")] public long? OverageMinorUnits { get; init; }

    /// <summary>The last 14 days, oldest first. Days with no sends are zeroes.</summary>
    [JsonPropertyName("series")] public IReadOnlyList<UsagePoint> Series { get; init; } = Array.Empty<UsagePoint>();
}

/// <summary>The message summary carried on a webhook delivery.</summary>
public sealed record WebhookEventMessage
{
    /// <summary>Announcer's id for the message.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>The <c>From:</c> header.</summary>
    [JsonPropertyName("from")] public string From { get; init; } = "";

    /// <summary>The recipient.</summary>
    [JsonPropertyName("to")] public string To { get; init; } = "";

    /// <summary>The subject line, if one was set.</summary>
    [JsonPropertyName("subject")] public string? Subject { get; init; }

    /// <summary>One of the <see cref="MessageStatus"/> values.</summary>
    [JsonPropertyName("status")] public string Status { get; init; } = "";
}

/// <summary>A verified webhook delivery.</summary>
public sealed record WebhookEvent
{
    /// <summary>One of the <see cref="EventType"/> values.</summary>
    [JsonPropertyName("event")] public string Event { get; init; } = "";

    /// <summary>When the transition happened.</summary>
    [JsonPropertyName("occurredAt")] public DateTimeOffset OccurredAt { get; init; }

    /// <summary>The message the event is about.</summary>
    [JsonPropertyName("message")] public WebhookEventMessage Message { get; init; } = new();

    /// <summary>Event-specific data, e.g. <c>dsnStatus</c> on a bounce.</summary>
    [JsonPropertyName("detail")] public IReadOnlyDictionary<string, JsonElement> Detail { get; init; }
        = new Dictionary<string, JsonElement>();
}

/// <summary>One recipient's outcome from <c>Emails.SendManyAsync</c>.</summary>
public sealed record BatchSendResult
{
    /// <summary>The recipient this result is for.</summary>
    public string To { get; init; } = "";

    /// <summary>Set when the send succeeded.</summary>
    public SentEmail? Result { get; init; }

    /// <summary>Set when the send failed.</summary>
    public AnnouncerException? Error { get; init; }

    /// <summary>Whether this recipient's send succeeded.</summary>
    public bool Ok => Error is null;
}
