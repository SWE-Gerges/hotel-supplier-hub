using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hub.Abstractions;

/// <summary>
/// Identifies a supplier. A validated lower-kebab string rather than an enum so that adding a supplier
/// never requires touching the abstractions.
/// </summary>
[JsonConverter(typeof(SupplierIdJsonConverter))]
public readonly record struct SupplierId : IComparable<SupplierId>
{
    public const int MaxLength = 64;

    private readonly string? _value;

    private SupplierId(string value) => _value = value;

    public string Value => _value ?? string.Empty;

    public bool IsDefault => _value is null;

    public static SupplierId Parse(string value)
    {
        if (!TryParse(value, out var id))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid supplier id (lower-kebab, 1-{MaxLength} characters, e.g. 'supplier-a').",
                nameof(value));
        }

        return id;
    }

    public static bool TryParse([NotNullWhen(true)] string? value, out SupplierId id)
    {
        if (IsValid(value))
        {
            id = new SupplierId(value);
            return true;
        }

        id = default;
        return false;
    }

    public int CompareTo(SupplierId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    private static bool IsValid([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        var previousWasHyphen = true; // a leading hyphen is rejected the same way as a doubled one
        foreach (var c in value)
        {
            if (c == '-')
            {
                if (previousWasHyphen)
                {
                    return false;
                }

                previousWasHyphen = true;
            }
            else if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                previousWasHyphen = false;
            }
            else
            {
                return false;
            }
        }

        return !previousWasHyphen;
    }
}

internal sealed class SupplierIdJsonConverter : JsonConverter<SupplierId>
{
    public override SupplierId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Convert(reader.GetString());

    public override void Write(Utf8JsonWriter writer, SupplierId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);

    public override SupplierId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Convert(reader.GetString());

    public override void WriteAsPropertyName(Utf8JsonWriter writer, SupplierId value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Value);

    private static SupplierId Convert(string? raw) =>
        SupplierId.TryParse(raw, out var id)
            ? id
            : throw new JsonException($"'{raw}' is not a valid supplier id.");
}
