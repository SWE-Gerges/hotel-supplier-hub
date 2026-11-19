using Hub.Abstractions;
using Microsoft.Extensions.Options;

namespace Hub.Booking;

public enum HubBookingStatus
{
    /// <summary>Claimed by a request; the supplier has been asked, or an asynchronous supplier has accepted and not yet confirmed.</summary>
    Pending,

    Confirmed,

    Failed,

    /// <summary>The request left and nobody knows whether the supplier booked. Resolved by asking the supplier, never by asking again.</summary>
    Unknown,

    Cancelled,
}

public enum PaymentMode
{
    PayNow,
    PayLater,
}

public sealed class BookingRecord
{
    public string Id { get; set; } = string.Empty;

    public string Tenant { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string PayloadHash { get; set; } = string.Empty;

    public string QuoteId { get; set; } = string.Empty;

    public string OfferId { get; set; } = string.Empty;

    public string Supplier { get; set; } = string.Empty;

    public string SupplierHotelCode { get; set; } = string.Empty;

    /// <summary>Derived from the idempotency key and sent to suppliers that support de-duplication.</summary>
    public string SupplierReference { get; set; } = string.Empty;

    public string? SupplierBookingRef { get; set; }

    public HubBookingStatus Status { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>What reconciliation found or why it could not decide.</summary>
    public string? Note { get; set; }

    public decimal SellingAmount { get; set; }

    public string SellingCurrency { get; set; } = string.Empty;

    public decimal SupplierAmount { get; set; }

    public string SupplierCurrency { get; set; } = string.Empty;

    public string LeadGuestName { get; set; } = string.Empty;

    public PaymentMode PaymentMode { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    public DateTimeOffset? FreeCancellationUntil { get; set; }

    /// <summary>For pay-later bookings: the moment an unpaid booking is cancelled, ahead of the penalty window.</summary>
    public DateTimeOffset? PayBy { get; set; }

    public string? CancelStatus { get; set; }

    public decimal? PenaltyAmount { get; set; }

    public decimal? RefundAmount { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>How many times the supplier was asked to book (a retry after reconciliation proved absence counts).</summary>
    public int BookAttempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string OfferJson { get; set; } = string.Empty;

    public string SearchJson { get; set; } = string.Empty;

    /// <summary>Concurrency token: an update that lost a race fails instead of overwriting the winner.</summary>
    public long Version { get; set; }

    public Money Selling => new(SellingAmount, SellingCurrency);

    public Money SupplierPrice => new(SupplierAmount, SupplierCurrency);
}

public sealed record CreateBookingCommand(
    string Tenant, string IdempotencyKey, string QuoteId, decimal Amount, string Currency, string LeadGuestName, PaymentMode PaymentMode);

public enum BookingCreateStatus
{
    /// <summary>First request with this key: the booking was made (or attempted) and its state is in the result.</summary>
    Created,

    /// <summary>The key was used before with the same payload; the stored outcome is returned and the supplier is not asked again.</summary>
    Replayed,

    /// <summary>The key was used before with a different payload.</summary>
    KeyReused,

    QuoteNotFound,

    /// <summary>The amount does not match the quote, or a newer quote replaced it; the response carries the current quote.</summary>
    PriceChanged,

    /// <summary>The quote ran out or was already used by another booking; the response carries a fresh quote when one could be made.</summary>
    QuoteExpired,

    /// <summary>Pay later is not allowed for this offer.</summary>
    PayLaterNotAllowed,

    /// <summary>The supplier cannot book at all.</summary>
    Unsupported,
}

public sealed record BookingCreateOutcome(BookingCreateStatus Status, BookingRecord? Booking, QuoteView? CurrentQuote, string? Detail);

public enum BookingCancelStatus
{
    Cancelled,
    PenaltyApplied,
    AlreadyCancelled,
    Failed,
}

/// <summary>The result of cancelling a booking, the same whatever shape the supplier answered in.</summary>
public sealed record BookingCancelOutcome(BookingCancelStatus Status, Money? Penalty, Money? Refund, string? Detail);

public enum BookingCancelRefusal
{
    NotFound,

    /// <summary>The booking is in a state that cannot be cancelled yet (unknown, or pending without a supplier reference).</summary>
    NotCancellable,
}

public sealed record BookingCancelResult(BookingCancelOutcome? Outcome, BookingRecord? Booking, BookingCancelRefusal? Refusal);

public sealed class BookingOptions
{
    public const string SectionName = "Hub:Bookings";

    /// <summary>A pay-later booking needs a free-cancellation deadline at least this far away when it is made.</summary>
    public TimeSpan PayLaterMinimumLead { get; set; } = TimeSpan.FromDays(3);

    /// <summary>An unpaid pay-later booking is cancelled this long before its free-cancellation window closes.</summary>
    public TimeSpan PayLaterCancelBuffer { get; set; } = TimeSpan.FromHours(24);

    /// <summary>The supplier may be asked to book again after a reconciliation proved it has no booking, at most this many times in total.</summary>
    public int MaxBookAttempts { get; set; } = 2;

    /// <summary>How often the background sweep looks at pending and unknown bookings.</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A booking is left alone by the sweep for this long after its last change, so a request in flight is not raced.</summary>
    public TimeSpan SyncMinimumAge { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A pending booking that never recorded a supplier answer for this long is treated as unknown and settled with the supplier.</summary>
    public TimeSpan InFlightTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How often the pay-later expiry service looks for unpaid bookings that are due.</summary>
    public TimeSpan PayLaterSweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    public int SweepBatchSize { get; set; } = 50;
}

internal sealed class BookingOptionsValidator : IValidateOptions<BookingOptions>
{
    public ValidateOptionsResult Validate(string? name, BookingOptions options)
    {
        var failures = new List<string>();
        if (options.PayLaterMinimumLead <= options.PayLaterCancelBuffer)
        {
            failures.Add("PayLaterMinimumLead must be longer than PayLaterCancelBuffer, or a pay-later booking would be due the moment it is made.");
        }

        if (options.PayLaterCancelBuffer < TimeSpan.Zero || options.SyncMinimumAge < TimeSpan.Zero || options.InFlightTimeout <= TimeSpan.Zero)
        {
            failures.Add("PayLaterCancelBuffer and SyncMinimumAge cannot be negative.");
        }

        if (options.MaxBookAttempts is < 1 or > 5)
        {
            failures.Add("MaxBookAttempts must be between 1 and 5.");
        }

        if (options.SyncInterval <= TimeSpan.Zero || options.PayLaterSweepInterval <= TimeSpan.Zero || options.SweepBatchSize < 1)
        {
            failures.Add("SyncInterval and PayLaterSweepInterval must be positive and SweepBatchSize at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

internal sealed class QuoteOptionsValidator : IValidateOptions<QuoteOptions>
{
    public ValidateOptionsResult Validate(string? name, QuoteOptions options) =>
        options.Lifetime > TimeSpan.Zero ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail("Quotes: Lifetime must be positive.");
}
