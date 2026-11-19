namespace Hub.Abstractions;

// Booking-side ports. A supplier implements only the ones it can honour; the hub reads the capability flags and the
// registry to know which. Every call identifies the offer through an OfferRef, because the hub does not keep supplier
// sessions: what a supplier needs to address an offer again travels in RoomOffer.Extra.

/// <summary>An offer together with the search that produced it: everything a supplier needs to address the offer again.</summary>
public sealed record OfferRef(RoomOffer Offer, SupplierSearchQuery Search);

public sealed record RoomAvailabilityQuery(SupplierSearchQuery Search, string SupplierHotelCode);

/// <summary>Asks the supplier to confirm that the offer is still sold and at what price.</summary>
public sealed record PreBookRequest(OfferRef Offer);

/// <param name="Price">What the supplier charges now.</param>
/// <param name="ValidUntil">How long the supplier holds that price, when it says.</param>
public sealed record PreBookQuote(string OfferId, Money Price, DateTimeOffset? ValidUntil);

/// <param name="ExpectedPrice">The supplier price the hub quoted; the supplier refuses the booking if its price moved since.</param>
/// <param name="SupplierReference">A reference derived from the client's idempotency key. A supplier that supports it refuses to book the same reference twice.</param>
public sealed record BookRequest(OfferRef Offer, Money ExpectedPrice, string SupplierReference, string LeadGuestName);

public enum BookingStatus
{
    Confirmed,

    /// <summary>Accepted but not yet confirmed; asynchronous suppliers answer like this and are polled with <see cref="IRetrieve"/>.</summary>
    Pending,

    Failed,
    Cancelled,
}

public sealed record BookingConfirmation(string SupplierBookingRef, BookingStatus Status, Money Total, string? SupplierReference = null);

public sealed record CancelRequest(string SupplierBookingRef);

public enum SupplierCancelStatus
{
    Cancelled,
    PenaltyApplied,
    AlreadyCancelled,
}

/// <summary>What a supplier did with a cancellation, in the hub's terms whatever shape the supplier answered in.</summary>
public sealed record CancellationOutcome(string SupplierBookingRef, SupplierCancelStatus Status, Money? Penalty, Money? Refund);

/// <summary>Finds a booking by the supplier's own reference or by the reference the hub sent when booking.</summary>
public sealed record RetrieveRequest(string? SupplierBookingRef, string? SupplierReference);

public interface IRoomAvailability
{
    /// <summary>The current rooms of one hotel for the stay in the query, as fresh offers.</summary>
    ValueTask<Result<IReadOnlyList<RoomOffer>>> GetRoomsAsync(RoomAvailabilityQuery query, CancellationToken cancellationToken);
}

public interface IPreBook
{
    ValueTask<Result<PreBookQuote>> PreBookAsync(PreBookRequest request, CancellationToken cancellationToken);
}

public interface IBook
{
    ValueTask<Result<BookingConfirmation>> BookAsync(BookRequest request, CancellationToken cancellationToken);
}

public interface ICancel
{
    ValueTask<Result<CancellationOutcome>> CancelAsync(CancelRequest request, CancellationToken cancellationToken);
}

public interface IRetrieve
{
    ValueTask<Result<BookingConfirmation>> RetrieveAsync(RetrieveRequest request, CancellationToken cancellationToken);
}
