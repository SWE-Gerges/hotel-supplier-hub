using System.Globalization;
using System.Text.Json;
using System.Xml;
using Hub.Abstractions;

namespace Hub.Suppliers.Fakes;

/// <summary>Parsing and boundary helpers shared by the fake suppliers (each still owns its own normaliser).</summary>
internal static class FakeSupplierSupport
{
    public static readonly string[] SupportedCurrencies = ["EUR", "USD"];

    public static SupplierError? ValidateQuery(SupplierSearchQuery query)
    {
        var errors = query.Request.Validate();
        if (errors.Count > 0)
        {
            return SupplierError.InvalidRequest(string.Join(' ', errors.SelectMany(static e => e.Value)));
        }

        return SupportedCurrencies.Contains(query.Request.Currency, StringComparer.Ordinal)
            ? null
            : SupplierError.InvalidRequest($"Currency '{query.Request.Currency}' is not supported; use EUR or USD.");
    }

    public static Stay ToStay(HotelSearchRequest request) =>
        new(request.CheckIn, request.CheckOut, request.Rooms, request.Currency);

    /// <summary>Waits out the injected latency, or forever for a hang, and returns what else the call should do.</summary>
    public static async ValueTask<FaultDecision> ApplyFaultsAsync(FaultController faults, TimeProvider time, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decision = faults.Next();
        if (decision.Hang)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, time, cancellationToken).ConfigureAwait(false);
        }

        if (decision.Latency > TimeSpan.Zero)
        {
            await Task.Delay(decision.Latency, time, cancellationToken).ConfigureAwait(false);
        }

        return decision;
    }

    /// <summary>
    /// The supplier boundary: expected failures are already Results; anything thrown while talking to or parsing the
    /// supplier becomes a failure here. Cancellation is the one thing that is allowed to escape.
    /// </summary>
    public static async ValueTask<Result<T>> GuardAsync<T>(Func<ValueTask<Result<T>>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or XmlException)
        {
            return Result.Fail<T>(SupplierError.Unknown("The supplier returned a payload that could not be parsed.", Excerpt(ex.Message)));
        }
        catch (Exception ex)
        {
            return Result.Fail<T>(SupplierError.Unknown($"Unexpected failure: {ex.GetType().Name}."));
        }
    }

    public static string Corrupt(string payload) =>
        payload.Length <= 8 ? "<" : payload[..(payload.Length / 2)];

    public static string Excerpt(string? text) => text is { Length: > 200 } ? text[..200] : text ?? string.Empty;

    public static Money ToMoney(decimal amount, string currency) => new(amount, currency);

    public static string FormatInvariant(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>Parses prices that suppliers format inconsistently: "1234.50", "12,30", "1.234,56", "1,234.56".</summary>
public static class TolerantDecimal
{
    public static bool TryParse(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');

        char? decimalSeparator = null;
        if (lastComma >= 0 && lastDot >= 0)
        {
            decimalSeparator = lastComma > lastDot ? ',' : '.';
        }
        else if (lastComma >= 0 || lastDot >= 0)
        {
            var separator = lastComma >= 0 ? ',' : '.';
            var occurrences = s.Count(c => c == separator);
            var digitsAfter = s.Length - s.LastIndexOf(separator) - 1;

            // A single separator followed by exactly three digits is a thousands separator ("12,345"); anything else is decimal.
            if (occurrences == 1 && digitsAfter != 3)
            {
                decimalSeparator = separator;
            }
        }

        var thousandsSeparator = decimalSeparator == ',' ? '.' : ',';
        var normalised = s.Replace(thousandsSeparator.ToString(), string.Empty, StringComparison.Ordinal);
        if (decimalSeparator == ',')
        {
            normalised = normalised.Replace(',', '.');
        }
        else if (decimalSeparator is null)
        {
            normalised = normalised.Replace(".", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal);
        }

        return decimal.TryParse(normalised, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }
}
