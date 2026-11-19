using System.Globalization;
using System.Text.Json.Serialization;

namespace Hub.Abstractions;

/// <summary>An amount in a three-letter upper-case currency code.</summary>
public readonly record struct Money
{
    [JsonConstructor]
    public Money(decimal amount, string currency)
    {
        if (currency is not { Length: 3 } || !currency.All(static c => c is >= 'A' and <= 'Z'))
        {
            throw new ArgumentException($"'{currency}' is not a three-letter upper-case currency code.", nameof(currency));
        }

        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public string Currency { get; }

    public Money Multiply(decimal factor, int decimals = 2) =>
        new(Math.Round(Amount * factor, decimals, MidpointRounding.AwayFromZero), Currency);

    public override string ToString() => $"{Amount.ToString("0.00", CultureInfo.InvariantCulture)} {Currency}";
}
