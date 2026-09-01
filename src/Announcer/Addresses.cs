using System.Text.Json;
using System.Text.Json.Serialization;

namespace Announcer;

/// <summary>
/// One or more email addresses. Every address may carry a display name:
/// <c>"Acme Billing &lt;billing@acme.com&gt;"</c>.
///
/// Converts implicitly from a string and from a string collection, so both of
/// these compile and mean what they look like:
///
/// <code>
/// To = "customer@example.com",
/// To = new[] { "a@example.com", "b@example.com" },
/// </code>
/// </summary>
[JsonConverter(typeof(AddressesJsonConverter))]
public sealed class Addresses
{
    /// <summary>The addresses, in the order given.</summary>
    public IReadOnlyList<string> Values { get; }

    /// <summary>Creates a list from individual addresses.</summary>
    /// <param name="values">The addresses.</param>
    public Addresses(params string[] values)
        => Values = values is null ? Array.Empty<string>() : (string[])values.Clone();

    /// <summary>Creates a list from a collection.</summary>
    /// <param name="values">The addresses.</param>
    public Addresses(IEnumerable<string> values)
        => Values = values?.ToArray() ?? Array.Empty<string>();

    /// <summary>How many addresses this holds.</summary>
    public int Count => Values.Count;

    /// <summary>The first address, or an empty string when there are none.</summary>
    public string First => Values.Count > 0 ? Values[0] : "";

    /// <summary>Wraps a single address.</summary>
    /// <param name="address">The address.</param>
    public static implicit operator Addresses(string address) => new(address);

    /// <summary>Wraps several addresses.</summary>
    /// <param name="addresses">The addresses.</param>
    public static implicit operator Addresses(string[] addresses) => new(addresses);

    /// <summary>Wraps several addresses.</summary>
    /// <param name="addresses">The addresses.</param>
    public static implicit operator Addresses(List<string> addresses) => new(addresses);

    /// <summary>The addresses joined with commas, as they would appear in a header.</summary>
    public override string ToString() => string.Join(", ", Values);
}

/// <summary>
/// Writes an <see cref="Addresses"/> as a bare JSON string when it holds one
/// address and as an array otherwise — the two shapes the API accepts.
/// </summary>
internal sealed class AddressesJsonConverter : JsonConverter<Addresses>
{
    public override Addresses Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new Addresses(reader.GetString() ?? "");
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var values = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    values.Add(reader.GetString() ?? "");
                }
            }
            return new Addresses(values);
        }

        throw new JsonException("Expected a string or an array of strings for an address list.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        Addresses value,
        JsonSerializerOptions options)
    {
        if (value.Count == 1)
        {
            writer.WriteStringValue(value.Values[0]);
            return;
        }

        writer.WriteStartArray();
        foreach (var address in value.Values)
        {
            writer.WriteStringValue(address);
        }
        writer.WriteEndArray();
    }
}
