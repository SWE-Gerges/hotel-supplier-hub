using System.Globalization;
using System.Text.Json;
using Hub.Abstractions;
using Microsoft.Extensions.Options;

namespace Hub.Suppliers.Fakes;

// supplier-b's booking side: there is no prebook. Availability for one hotel answers in a single call and is how a price is
// verified. Booking is asynchronous: the call answers "accepted" and the booking becomes confirmed (or failed) a little
// later, which the client finds out by retrieving it. Prices are integer cents.

internal sealed record BAvailabilityResponse(BHotel? Hotel, int? Code, string? Message);

internal sealed record BBookRequest(string ClientRef, string HotelId, string RoomId, int Meal, Stay Stay, long ExpectedPriceCents, string Guest);

internal sealed record BBookResponse(string? BookingId, string? Status, int? Code, string? Message);

internal sealed record BBookingRecord(string BookingId, string ClientRef, string Status, long TotalCents, string Currency);

internal sealed record BRetrieveResponse(BBookingRecord? Booking, int? Code, string? Message);

internal sealed record BCancelResponse(string? Result, long? PenaltyCents, long? RefundCents, string? Currency, int? Code, string? Message);

/// <summary>supplier-b's mapping of wire room fields into <see cref="RoomOffer.Extra"/>.</summary>
internal sealed class SupplierBRoomExtraMapper : IRoomExtraMapper<BRoom>
{
    public SupplierId Supplier => FakeSupplierIds.SupplierB;

    public Type WireRoomType => typeof(BRoom);

    public void Map(BRoom room, RoomExtraContext context, IDictionary<string, string> extra)
    {
        extra["roomId"] = room.Id;
        extra["meal"] = room.Meal.ToString(CultureInfo.InvariantCulture);
        if (room.CancelUntil is not null)
        {
            extra["cancelUntil"] = room.CancelUntil;
        }

        if (room.PenaltyCode is not null)
        {
            extra["penaltyCode"] = room.PenaltyCode;
        }
    }
}

internal sealed partial class SupplierBServer
{
    public async Task<string> AvailabilityAsync(string hotelId, Stay stay, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Serialize(new BAvailabilityResponse(null, StatusFor(kind), "The supplier reported an error."), decision);
        }

        var hotel = catalog.FindByCode(FakeSupplierIds.SupplierB, hotelId);
        var offers = hotel is null ? [] : catalog.OffersFor(FakeSupplierIds.SupplierB, hotel, stay);
        if (hotel is null || offers.Count == 0)
        {
            return Serialize(new BAvailabilityResponse(null, 422, "No rooms are available."), decision);
        }

        var drifted = offers.Select(o => o with { Total = FakeBookingSupport.Drifted(o.Total, faults.Current) });
        return Serialize(
            new BAvailabilityResponse(
                new BHotel(
                    hotelId,
                    catalog.NameFor(FakeSupplierIds.SupplierB, hotel),
                    hotel.Stars,
                    [hotel.Longitude, hotel.Latitude],
                    hotel.Street,
                    drifted.Select(o => ToRoom(o, stay)).ToList()),
                null,
                null),
            decision);
    }

    public async Task<string> BookAsync(BBookRequest request, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Serialize(new BBookResponse(null, null, StatusFor(kind), "The supplier reported an error."), decision);
        }

        if (ledger.Find(null, request.ClientRef) is { } existing)
        {
            return Serialize(new BBookResponse(existing.BookingRef, "accepted", null, null), decision);
        }

        var board = SupplierBNormalizer.ParseBoard(request.Meal);
        if (FakeBookingSupport.FindOffer(catalog, FakeSupplierIds.SupplierB, request.HotelId, request.RoomId, board, request.Stay) is not { } offer)
        {
            return Serialize(new BBookResponse(null, null, 422, "The room is no longer available."), decision);
        }

        var current = FakeBookingSupport.Drifted(offer.Total, faults.Current);
        if ((long)Math.Round(current * 100m, 0, MidpointRounding.AwayFromZero) != request.ExpectedPriceCents)
        {
            return Serialize(new BBookResponse(null, null, 409, "The price differs from the one expected."), decision);
        }

        var now = time.GetUtcNow();
        var delay = options.Value.SupplierB.BookingConfirmDelay;
        var fails = faults.RollBookingFails();
        var (booking, duplicate) = ledger.GetOrCreate(request.ClientRef, reference => FakeBookingSupport.NewBooking(
            reference, request.ClientRef, $"{request.HotelId}.{request.RoomId}.{request.Meal}", request.HotelId, offer, current, request.Stay, now, delay, fails));
        if (!duplicate && faults.RollAcceptedButLost())
        {
            return Serialize(new BBookResponse(null, null, 504, "Upstream timeout."), decision);
        }

        return Serialize(new BBookResponse(booking.BookingRef, "accepted", null, null), decision);
    }

    public async Task<string> RetrieveAsync(string? bookingId, string? clientRef, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Serialize(new BRetrieveResponse(null, StatusFor(kind), "The supplier reported an error."), decision);
        }

        return ledger.Find(bookingId, clientRef) is { } booking
            ? Serialize(new BRetrieveResponse(ToRecord(booking), null, null), decision)
            : Serialize(new BRetrieveResponse(null, 404, "No such booking."), decision);
    }

    public async Task<string> CancelAsync(string bookingId, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Serialize(new BCancelResponse(null, null, null, null, StatusFor(kind), "The supplier reported an error."), decision);
        }

        if (ledger.Find(bookingId, null) is not { } booking)
        {
            return Serialize(new BCancelResponse(null, null, null, null, 404, "No such booking."), decision);
        }

        lock (booking)
        {
            if (booking.Cancelled)
            {
                return Serialize(new BCancelResponse("already_cancelled", null, null, booking.Currency, null, null), decision);
            }

            var fee = booking.FeeAt(DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
            booking.Cancelled = true;
            return Serialize(
                new BCancelResponse("ok", Cents(fee), Cents(booking.Total - fee), booking.Currency, null, null),
                decision);
        }
    }

    private BBookingRecord ToRecord(LedgerBooking booking) => new(
        booking.BookingRef,
        booking.ClientRef,
        booking.StateAt(time.GetUtcNow()) switch
        {
            LedgerState.Cancelled => "cancelled",
            LedgerState.Failed => "failed",
            LedgerState.Pending => "pending",
            _ => "confirmed",
        },
        Cents(booking.Total),
        booking.Currency);

    private static long Cents(decimal amount) => (long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);
}

internal sealed partial class SupplierBClient
{
    public async ValueTask<Result<IReadOnlyList<RoomOffer>>> GetRoomsAsync(RoomAvailabilityQuery query, CancellationToken cancellationToken)
    {
        if (FakeSupplierSupport.ValidateQuery(query.Search) is { } invalid)
        {
            return Result.Fail<IReadOnlyList<RoomOffer>>(invalid);
        }

        return await FakeSupplierSupport.GuardAsync<IReadOnlyList<RoomOffer>>(async () =>
        {
            var json = await server.AvailabilityAsync(query.SupplierHotelCode, FakeSupplierSupport.ToStay(query.Search.Request), cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<BAvailabilityResponse>(json, SupplierBJson.Options) ?? throw new JsonException("Empty response.");
            if (response.Hotel is not { } hotel)
            {
                return Result.Fail<IReadOnlyList<RoomOffer>>(SupplierBNormalizer.MapError(response.Code, response.Message));
            }

            var hotels = normalizer.Normalize(new BBatch(1, [hotel]), query.Search);
            return hotels.IsFailure
                ? Result.Fail<IReadOnlyList<RoomOffer>>(hotels.Error)
                : Result.Ok<IReadOnlyList<RoomOffer>>(hotels.Value.SelectMany(static h => h.Offers).ToArray());
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<BookingConfirmation>> BookAsync(BookRequest request, CancellationToken cancellationToken)
    {
        var offer = request.Offer.Offer;
        if (FakeBookingSupport.Extra(offer, "roomId") is not { } room
            || !int.TryParse(FakeBookingSupport.Extra(offer, "meal"), NumberStyles.None, CultureInfo.InvariantCulture, out var meal))
        {
            return Result.Fail<BookingConfirmation>(SupplierError.InvalidRequest("The offer carries no room reference for supplier-b."));
        }

        return await FakeSupplierSupport.GuardAsync(async () =>
        {
            var wire = new BBookRequest(
                request.SupplierReference, offer.SupplierHotelCode, room, meal, FakeSupplierSupport.ToStay(request.Offer.Search.Request),
                (long)Math.Round(request.ExpectedPrice.Amount * 100m, 0, MidpointRounding.AwayFromZero), request.LeadGuestName);
            var json = await server.BookAsync(wire, cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<BBookResponse>(json, SupplierBJson.Options) ?? throw new JsonException("Empty response.");
            return response.BookingId is { Length: > 0 } id && response.Status == "accepted"
                ? Result.Ok(new BookingConfirmation(id, BookingStatus.Pending, request.ExpectedPrice, request.SupplierReference))
                : Result.Fail<BookingConfirmation>(SupplierBNormalizer.MapError(response.Code, response.Message));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<CancellationOutcome>> CancelAsync(CancelRequest request, CancellationToken cancellationToken) =>
        await FakeSupplierSupport.GuardAsync(async () =>
        {
            var json = await server.CancelAsync(request.SupplierBookingRef, cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<BCancelResponse>(json, SupplierBJson.Options) ?? throw new JsonException("Empty response.");
            var currency = (response.Currency ?? "EUR").ToUpperInvariant();
            return response.Result switch
            {
                "ok" when response.PenaltyCents is > 0 => Result.Ok(new CancellationOutcome(
                    request.SupplierBookingRef, SupplierCancelStatus.PenaltyApplied, new Money(response.PenaltyCents.Value / 100m, currency),
                    response.RefundCents is { } refundCents ? new Money(refundCents / 100m, currency) : null)),
                "ok" => Result.Ok(new CancellationOutcome(
                    request.SupplierBookingRef, SupplierCancelStatus.Cancelled, null, response.RefundCents is { } refund ? new Money(refund / 100m, currency) : null)),
                "already_cancelled" => Result.Ok(new CancellationOutcome(request.SupplierBookingRef, SupplierCancelStatus.AlreadyCancelled, null, null)),
                _ => Result.Fail<CancellationOutcome>(SupplierBNormalizer.MapError(response.Code, response.Message)),
            };
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<Result<BookingConfirmation>> RetrieveAsync(RetrieveRequest request, CancellationToken cancellationToken) =>
        await FakeSupplierSupport.GuardAsync(async () =>
        {
            var json = await server.RetrieveAsync(request.SupplierBookingRef, request.SupplierReference, cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<BRetrieveResponse>(json, SupplierBJson.Options) ?? throw new JsonException("Empty response.");
            if (response.Booking is not { } booking)
            {
                return Result.Fail<BookingConfirmation>(SupplierBNormalizer.MapError(response.Code, response.Message));
            }

            var status = booking.Status switch
            {
                "confirmed" => BookingStatus.Confirmed,
                "pending" => BookingStatus.Pending,
                "cancelled" => BookingStatus.Cancelled,
                _ => BookingStatus.Failed,
            };
            return Result.Ok(new BookingConfirmation(booking.BookingId, status, new Money(booking.TotalCents / 100m, booking.Currency.ToUpperInvariant()), booking.ClientRef));
        }, cancellationToken).ConfigureAwait(false);
}
