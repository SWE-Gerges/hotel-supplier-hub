using Hub.Abstractions;
using Hub.Booking;

namespace Hub.Api;

/// <param name="OfferId">The offer id of a search result.</param>
public sealed record QuoteRequestDto(string? OfferId);

public sealed record PriceChangeDto(Money From, Money To);

/// <summary>What the client is shown for an offer, and until when it can book that.</summary>
public sealed record QuoteResponse(
    string QuoteId,
    string OfferId,
    DateTimeOffset ExpiresAt,
    Money SellingPrice,
    PriceChangeDto? PriceChanged,
    bool Refundable,
    DateTimeOffset? FreeCancellationUntil);

/// <param name="QuoteId">The quote the client was shown.</param>
/// <param name="Amount">The selling amount of that quote, exactly as shown.</param>
/// <param name="Currency">The currency of that amount.</param>
/// <param name="PaymentMode"><c>payNow</c> (default) or <c>payLater</c>.</param>
public sealed record CreateBookingDto(string? QuoteId, decimal? Amount, string? Currency, string? LeadGuestName, string? PaymentMode);

public sealed record CancellationDto(string Status, Money? Penalty, Money? Refund, DateTimeOffset? CancelledAt);

public sealed record BookingResponse(
    string Id,
    string Status,
    string Supplier,
    string? SupplierBookingRef,
    string QuoteId,
    string OfferId,
    Money SellingPrice,
    string LeadGuestName,
    string PaymentMode,
    DateTimeOffset? PaidAt,
    DateTimeOffset? FreeCancellationUntil,
    DateTimeOffset? PayBy,
    string? FailureReason,
    string? Note,
    CancellationDto? Cancellation,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The same whatever shape the supplier answered in.</summary>
/// <param name="Status"><c>cancelled</c>, <c>penaltyApplied</c>, <c>alreadyCancelled</c> or <c>failed</c>.</param>
public sealed record CancelResponse(string Status, Money? Penalty, Money? Refund, string? Detail);

public sealed record StoredOfferResponse(
    string OfferId,
    string Supplier,
    string HotelName,
    string? CanonicalId,
    string RoomName,
    BoardType Board,
    bool Refundable,
    DateTimeOffset? RefundableUntil,
    Money SellingPrice,
    IReadOnlyList<CancellationTier> CancellationTiers);

internal static class BookingMapper
{
    public static QuoteResponse ToResponse(QuoteView quote) => new(
        quote.QuoteId,
        quote.OfferId,
        quote.ExpiresAt,
        quote.SellingPrice,
        quote.PriceChanged is { } change ? new PriceChangeDto(change.From, change.To) : null,
        quote.Refundable,
        quote.FreeCancellationUntil);

    public static BookingResponse ToResponse(BookingRecord booking) => new(
        booking.Id,
        Camel(booking.Status.ToString()),
        booking.Supplier,
        booking.SupplierBookingRef,
        booking.QuoteId,
        booking.OfferId,
        booking.Selling,
        booking.LeadGuestName,
        Camel(booking.PaymentMode.ToString()),
        booking.PaidAt,
        booking.FreeCancellationUntil,
        booking.PayBy,
        booking.FailureReason,
        booking.Note,
        booking.CancelStatus is null
            ? null
            : new CancellationDto(
                Camel(booking.CancelStatus),
                booking.PenaltyAmount is { } penalty ? new Money(penalty, booking.SellingCurrency) : null,
                booking.RefundAmount is { } refund ? new Money(refund, booking.SellingCurrency) : null,
                booking.CancelledAt),
        booking.CreatedAt,
        booking.UpdatedAt);

    public static CancelResponse ToResponse(BookingCancelOutcome outcome) =>
        new(Camel(outcome.Status.ToString()), outcome.Penalty, outcome.Refund, outcome.Detail);

    public static string Camel(string value) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(value);
}
