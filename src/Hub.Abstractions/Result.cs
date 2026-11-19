namespace Hub.Abstractions;

public enum SupplierErrorKind
{
    Unavailable,
    InvalidRequest,
    Auth,
    RateLimited,
    Timeout,
    Transient,
    Unsupported,
    Unknown,

    /// <summary>The supplier does not know the booking or offer asked about.</summary>
    NotFound,

    /// <summary>The supplier session behind an offer has expired; the offer has to be found again.</summary>
    SessionExpired,

    /// <summary>The supplier's price differs from the price the hub expected.</summary>
    PriceChanged,

    /// <summary>The supplier no longer sells the room; unlike <see cref="Unavailable"/>, the supplier itself is fine.</summary>
    SoldOut,
}

/// <summary>An expected supplier failure. <c>Retryable</c> is the only signal the resilience layer retries on.</summary>
public sealed record SupplierError(SupplierErrorKind Kind, string Message, bool Retryable = false, string? Raw = null)
{
    public static SupplierError Transient(string message, string? raw = null) =>
        new(SupplierErrorKind.Transient, message, Retryable: true, raw);

    public static SupplierError Unavailable(string message, string? raw = null) =>
        new(SupplierErrorKind.Unavailable, message, Retryable: false, raw);

    public static SupplierError InvalidRequest(string message) => new(SupplierErrorKind.InvalidRequest, message);

    public static SupplierError Unsupported(string message) => new(SupplierErrorKind.Unsupported, message);

    public static SupplierError Timeout(string message) => new(SupplierErrorKind.Timeout, message);

    public static SupplierError NotFound(string message) => new(SupplierErrorKind.NotFound, message);

    public static SupplierError SessionExpired(string message) => new(SupplierErrorKind.SessionExpired, message);

    public static SupplierError PriceChanged(string message) => new(SupplierErrorKind.PriceChanged, message);

    public static SupplierError SoldOut(string message) => new(SupplierErrorKind.SoldOut, message);

    public static SupplierError Unknown(string message, string? raw = null) =>
        new(SupplierErrorKind.Unknown, message, Retryable: false, raw);
}

/// <summary>
/// The single result convention at the supplier boundary: a value or a <see cref="SupplierError"/>.
/// Suppliers never return null or tuples and never throw for expected failures.
/// </summary>
public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly SupplierError? _error;

    private Result(T? value, SupplierError? error)
    {
        _value = value;
        _error = error;
    }

    public bool IsSuccess => _error is null;

    public bool IsFailure => _error is not null;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result is a failure: {_error!.Message}");

    public SupplierError Error => _error ?? throw new InvalidOperationException("Result is a success.");

    public static Result<T> Ok(T value) => new(value, null);

    public static Result<T> Fail(SupplierError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(default, error);
    }

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<SupplierError, TOut> onFailure) =>
        IsSuccess ? onSuccess(_value!) : onFailure(_error!);

    public override string ToString() => IsSuccess ? $"Ok({_value})" : $"Fail({_error})";
}

public static class Result
{
    public static Result<T> Ok<T>(T value) => Result<T>.Ok(value);

    public static Result<T> Fail<T>(SupplierError error) => Result<T>.Fail(error);
}
