using System.Globalization;
using Hub.Abstractions;

namespace Hub.Suppliers.Fakes;

internal enum LedgerState
{
    Pending,
    Confirmed,
    Failed,
    Cancelled,
}

/// <summary>A booking as a fake supplier's own system records it.</summary>
internal sealed class LedgerBooking
{
    public required string BookingRef { get; init; }

    public required string ClientRef { get; init; }

    public required string OfferId { get; init; }

    public required string HotelCode { get; init; }

    public required decimal Total { get; init; }

    public required string Currency { get; init; }

    public required int Nights { get; init; }

    public required bool Refundable { get; init; }

    public required DateOnly FreeCancellationUntil { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When a pending booking resolves; equal to the creation time for suppliers that confirm at once.</summary>
    public required DateTimeOffset ResolvesAt { get; init; }

    /// <summary>Whether the booking ends up failed once it resolves.</summary>
    public required bool EndsFailed { get; init; }

    public bool Cancelled { get; set; }

    public LedgerState StateAt(DateTimeOffset now) =>
        Cancelled ? LedgerState.Cancelled
        : now < ResolvesAt ? LedgerState.Pending
        : EndsFailed ? LedgerState.Failed
        : LedgerState.Confirmed;

    /// <summary>What cancelling costs today: nothing inside the free window, the first night after it, everything if the rate is not refundable.</summary>
    public decimal FeeAt(DateOnly today) =>
        !Refundable ? Total
        : today <= FreeCancellationUntil ? 0m
        : Math.Round(Total / Nights, 2, MidpointRounding.AwayFromZero);
}

/// <summary>One fake supplier's bookings. Booking the same client reference twice returns the booking that already exists.</summary>
internal sealed class FakeSupplierLedger(string prefix, TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LedgerBooking> _byRef = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LedgerBooking> _byClientRef = new(StringComparer.Ordinal);
    private int _sequence;

    public TimeProvider Time { get; } = time;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byRef.Count;
            }
        }
    }

    public (LedgerBooking Booking, bool Duplicate) GetOrCreate(string clientRef, Func<string, LedgerBooking> create)
    {
        lock (_gate)
        {
            if (_byClientRef.TryGetValue(clientRef, out var existing))
            {
                return (existing, true);
            }

            var booking = create(prefix + (++_sequence).ToString("D5", CultureInfo.InvariantCulture));
            _byRef[booking.BookingRef] = booking;
            _byClientRef[clientRef] = booking;
            return (booking, false);
        }
    }

    public LedgerBooking? Find(string? bookingRef, string? clientRef)
    {
        lock (_gate)
        {
            if (bookingRef is not null && _byRef.TryGetValue(bookingRef, out var byRef))
            {
                return byRef;
            }

            return clientRef is not null && _byClientRef.TryGetValue(clientRef, out var byClient) ? byClient : null;
        }
    }

    public IReadOnlyList<FakeBookingView> Snapshot()
    {
        var now = Time.GetUtcNow();
        lock (_gate)
        {
            return _byRef.Values.Select(b => new FakeBookingView(b.BookingRef, b.ClientRef, b.StateAt(now).ToString(), b.Total, b.Currency)).ToArray();
        }
    }
}

public sealed record FakeBookingView(string BookingRef, string ClientRef, string State, decimal Total, string Currency);

/// <summary>Read access to what each fake supplier has booked, for tests and the demo.</summary>
public sealed class FakeSupplierLedgers
{
    private readonly Dictionary<SupplierId, FakeSupplierLedger> _ledgers;

    internal FakeSupplierLedgers(Dictionary<SupplierId, FakeSupplierLedger> ledgers) => _ledgers = ledgers;

    internal FakeSupplierLedger Require(SupplierId supplier) => _ledgers[supplier];

    public int Count(SupplierId supplier) => _ledgers[supplier].Count;

    public IReadOnlyList<FakeBookingView> Bookings(SupplierId supplier) => _ledgers[supplier].Snapshot();
}

internal static class FakeBookingSupport
{
    /// <summary>The offer the supplier would sell for these identifiers, or null when it is not sold for the stay.</summary>
    public static PhysicalOffer? FindOffer(SyntheticCatalog catalog, SupplierId supplier, string hotelCode, string roomCode, BoardType board, Stay stay)
    {
        var hotel = catalog.FindByCode(supplier, hotelCode);
        return hotel is null
            ? null
            : catalog.OffersFor(supplier, hotel, stay).FirstOrDefault(o => o.Room.Code == roomCode && o.Board == board);
    }

    /// <summary>The price the supplier asks when queried again, after any injected drift.</summary>
    public static decimal Drifted(decimal total, FaultProfile profile) =>
        profile.PriceDriftPercent == 0
            ? total
            : Math.Round(total * (1m + ((decimal)profile.PriceDriftPercent / 100m)), 2, MidpointRounding.AwayFromZero);

    public static LedgerBooking NewBooking(
        string bookingRef, string clientRef, string offerId, string hotelCode, PhysicalOffer offer, decimal total, Stay stay,
        DateTimeOffset now, TimeSpan resolveAfter, bool endsFailed) => new()
        {
            BookingRef = bookingRef,
            ClientRef = clientRef,
            OfferId = offerId,
            HotelCode = hotelCode,
            Total = total,
            Currency = stay.Currency,
            Nights = stay.Nights,
            Refundable = offer.Refundable,
            FreeCancellationUntil = offer.FreeCancellationUntil,
            CreatedAt = now,
            ResolvesAt = now + resolveAfter,
            EndsFailed = endsFailed,
        };

    public static string Money2(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    public static string? Extra(RoomOffer offer, string key) => offer.Extra.GetValueOrDefault(key);
}
