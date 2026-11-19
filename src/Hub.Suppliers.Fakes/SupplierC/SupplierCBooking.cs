using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Hub.Abstractions;

namespace Hub.Suppliers.Fakes;

// supplier-c's booking side: the session opened by the availability call is required for prebook and booking, and a
// session that has expired answers SESSION_EXPIRED. A successful prebook extends the session. Booking confirms at once.
// There is no way to look a booking up, so supplier-c does not implement IRetrieve.

/// <summary>supplier-c's mapping of wire room elements into <see cref="RoomOffer.Extra"/>, including its structured policy.</summary>
internal sealed class SupplierCRoomExtraMapper : IRoomExtraMapper<XElement>
{
    private static readonly XNamespace Ns = SupplierCXml.Ns;

    public SupplierId Supplier => FakeSupplierIds.SupplierC;

    public Type WireRoomType => typeof(XElement);

    public void Map(XElement room, RoomExtraContext context, IDictionary<string, string> extra)
    {
        extra["roomId"] = (string?)room.Attribute("Id") ?? string.Empty;
        extra["mealPlan"] = (string?)room.Attribute("MealPlan") ?? string.Empty;
        if (context.SessionId is not null)
        {
            extra["sessionId"] = context.SessionId;
        }

        extra["cancellationText"] = room.Element(Ns + "CancelPolicy")?.Value.Trim() ?? string.Empty;
        var tiers = room.Element(Ns + "Policy")?.Elements(Ns + "Tier")
            .Select(static t => $"{(string?)t.Attribute("From")}|{(string?)t.Attribute("Kind")}|{(string?)t.Attribute("Value")}");
        if (tiers is not null)
        {
            extra["policyTiers"] = string.Join(';', tiers);
        }
    }
}

internal sealed partial class SupplierCServer
{
    public async Task<string> PreBookAsync(string sessionId, string hotelCode, string roomId, string mealPlan, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Finish(ErrorDocument(kind), decision);
        }

        if (!TryUseSession(sessionId, out var session))
        {
            return Finish(ErrorDocument("SESSION_EXPIRED", "The session has expired."), decision);
        }

        if (FindOffer(hotelCode, roomId, mealPlan, session.Stay) is not { } offer)
        {
            return Finish(ErrorDocument("E422", "The room is no longer available."), decision);
        }

        session.ExpiresAt = time.GetUtcNow() + options.Value.SupplierC.SessionTtl;
        var total = FakeBookingSupport.Drifted(offer.Total, faults.Current);
        var root = new XElement(
            SupplierCXml.Ns + "PreBookResponse",
            new XAttribute("Session", sessionId),
            new XAttribute("ValidUntil", session.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)),
            new XElement(SupplierCXml.Ns + "Price", new XAttribute("Currency", session.Stay.Currency), FakeBookingSupport.Money2(total)));
        return Finish(new XDocument(root), decision);
    }

    public async Task<string> BookAsync(
        string sessionId, string hotelCode, string roomId, string mealPlan, string expectedPrice, string clientRef, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Finish(ErrorDocument(kind), decision);
        }

        if (ledger.Find(null, clientRef) is { } existing)
        {
            return Finish(BookingDocument(existing), decision);
        }

        if (!TryUseSession(sessionId, out var session))
        {
            return Finish(ErrorDocument("SESSION_EXPIRED", "The session has expired."), decision);
        }

        if (FindOffer(hotelCode, roomId, mealPlan, session.Stay) is not { } offer)
        {
            return Finish(ErrorDocument("E422", "The room is no longer available."), decision);
        }

        var current = FakeBookingSupport.Drifted(offer.Total, faults.Current);
        if (!decimal.TryParse(expectedPrice, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var expected) || expected != current)
        {
            return Finish(ErrorDocument("E409", "The price differs from the one expected."), decision);
        }

        var (booking, duplicate) = ledger.GetOrCreate(clientRef, reference => FakeBookingSupport.NewBooking(
            reference, clientRef, $"{hotelCode}.{roomId}.{mealPlan}", hotelCode, offer, current, session.Stay, time.GetUtcNow(), TimeSpan.Zero, endsFailed: false));
        if (!duplicate && faults.RollAcceptedButLost())
        {
            return Finish(ErrorDocument("E504", "Upstream timeout."), decision);
        }

        return Finish(BookingDocument(booking), decision);
    }

    public async Task<string> CancelAsync(string bookingRef, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Finish(ErrorDocument(kind), decision);
        }

        if (ledger.Find(bookingRef, null) is not { } booking)
        {
            return Finish(ErrorDocument("E404", "No such booking."), decision);
        }

        lock (booking)
        {
            var ns = SupplierCXml.Ns;
            if (booking.Cancelled)
            {
                return Finish(new XDocument(new XElement(ns + "CancelResponse", new XAttribute("Ref", bookingRef), new XAttribute("Result", "ALREADY"))), decision);
            }

            var fee = booking.FeeAt(DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
            booking.Cancelled = true;
            var root = new XElement(
                ns + "CancelResponse",
                new XAttribute("Ref", bookingRef),
                new XAttribute("Result", fee > 0 ? "CANCELLED_FEE" : "CANCELLED"),
                new XElement(ns + "Fee", new XAttribute("Currency", booking.Currency), FakeBookingSupport.Money2(fee)),
                new XElement(ns + "Refund", new XAttribute("Currency", booking.Currency), FakeBookingSupport.Money2(booking.Total - fee)));
            return Finish(new XDocument(root), decision);
        }
    }

    private bool TryUseSession(string sessionId, out Session session)
    {
        if (_sessions.TryGetValue(sessionId, out var found) && found.ExpiresAt > time.GetUtcNow())
        {
            session = found;
            return true;
        }

        session = null!;
        return false;
    }

    private PhysicalOffer? FindOffer(string hotelCode, string roomId, string mealPlan, Stay stay) =>
        FakeBookingSupport.FindOffer(catalog, FakeSupplierIds.SupplierC, hotelCode, roomId, SupplierCNormalizer.ParseBoard(mealPlan), stay);

    private XDocument BookingDocument(LedgerBooking booking) =>
        new(new XElement(
            SupplierCXml.Ns + "BookResponse",
            new XAttribute("Ref", booking.BookingRef),
            new XAttribute("ClientRef", booking.ClientRef),
            new XAttribute("Status", booking.StateAt(time.GetUtcNow()) == LedgerState.Cancelled ? "CANCELLED" : "CONFIRMED"),
            new XElement(SupplierCXml.Ns + "Total", new XAttribute("Currency", booking.Currency), FakeBookingSupport.Money2(booking.Total))));
}

internal sealed partial class SupplierCClient
{
    public async ValueTask<Result<PreBookQuote>> PreBookAsync(PreBookRequest request, CancellationToken cancellationToken)
    {
        if (RoomRefOf(request.Offer.Offer) is not { } room)
        {
            return Result.Fail<PreBookQuote>(SupplierError.InvalidRequest("The offer carries no session or room reference for supplier-c."));
        }

        return await FakeSupplierSupport.GuardAsync(async () =>
        {
            var xml = await server.PreBookAsync(room.Session, request.Offer.Offer.SupplierHotelCode, room.Room, room.Meal, cancellationToken).ConfigureAwait(false);
            var root = SupplierCXml.Parse(xml).Root ?? throw new XmlException("Empty document.");
            if (SupplierCNormalizer.ReadError(root) is { } error)
            {
                return Result.Fail<PreBookQuote>(error);
            }

            var price = root.Element(SupplierCXml.Ns + "Price");
            var currency = (string?)price?.Attribute("Currency");
            if (price is null || currency is not { Length: 3 }
                || !decimal.TryParse(price.Value.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
            {
                return Result.Fail<PreBookQuote>(SupplierError.Unknown("The prebook answer carried no readable price."));
            }

            DateTimeOffset? validUntil = DateTimeOffset.TryParse((string?)root.Attribute("ValidUntil"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
            return Result.Ok(new PreBookQuote(request.Offer.Offer.OfferId, new Money(amount, currency.ToUpperInvariant()), validUntil));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<BookingConfirmation>> BookAsync(BookRequest request, CancellationToken cancellationToken)
    {
        if (RoomRefOf(request.Offer.Offer) is not { } room)
        {
            return Result.Fail<BookingConfirmation>(SupplierError.InvalidRequest("The offer carries no session or room reference for supplier-c."));
        }

        return await FakeSupplierSupport.GuardAsync(async () =>
        {
            var xml = await server.BookAsync(
                room.Session, request.Offer.Offer.SupplierHotelCode, room.Room, room.Meal,
                FakeBookingSupport.Money2(request.ExpectedPrice.Amount), request.SupplierReference, cancellationToken).ConfigureAwait(false);
            var root = SupplierCXml.Parse(xml).Root ?? throw new XmlException("Empty document.");
            if (SupplierCNormalizer.ReadError(root) is { } error)
            {
                return Result.Fail<BookingConfirmation>(error);
            }

            var total = root.Element(SupplierCXml.Ns + "Total");
            if ((string?)root.Attribute("Ref") is not { Length: > 0 } reference || total is null
                || !decimal.TryParse(total.Value.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
            {
                return Result.Fail<BookingConfirmation>(SupplierError.Unknown("The booking answer could not be read."));
            }

            var status = (string?)root.Attribute("Status") == "CONFIRMED" ? BookingStatus.Confirmed : BookingStatus.Cancelled;
            return Result.Ok(new BookingConfirmation(reference, status, new Money(amount, ((string?)total.Attribute("Currency") ?? request.ExpectedPrice.Currency).ToUpperInvariant()), (string?)root.Attribute("ClientRef")));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<CancellationOutcome>> CancelAsync(CancelRequest request, CancellationToken cancellationToken) =>
        await FakeSupplierSupport.GuardAsync(async () =>
        {
            var xml = await server.CancelAsync(request.SupplierBookingRef, cancellationToken).ConfigureAwait(false);
            var root = SupplierCXml.Parse(xml).Root ?? throw new XmlException("Empty document.");
            if (SupplierCNormalizer.ReadError(root) is { } error)
            {
                return Result.Fail<CancellationOutcome>(error);
            }

            Money? Read(string element)
            {
                var node = root.Element(SupplierCXml.Ns + element);
                return node is not null && (string?)node.Attribute("Currency") is { Length: 3 } currency
                       && decimal.TryParse(node.Value.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
                    ? new Money(amount, currency.ToUpperInvariant())
                    : null;
            }

            return (string?)root.Attribute("Result") switch
            {
                "CANCELLED" => Result.Ok(new CancellationOutcome(request.SupplierBookingRef, SupplierCancelStatus.Cancelled, null, Read("Refund"))),
                "CANCELLED_FEE" => Result.Ok(new CancellationOutcome(request.SupplierBookingRef, SupplierCancelStatus.PenaltyApplied, Read("Fee"), Read("Refund"))),
                "ALREADY" => Result.Ok(new CancellationOutcome(request.SupplierBookingRef, SupplierCancelStatus.AlreadyCancelled, null, null)),
                var other => Result.Fail<CancellationOutcome>(SupplierError.Unknown("Unrecognised cancellation result.", other)),
            };
        }, cancellationToken).ConfigureAwait(false);

    private static (string Session, string Room, string Meal)? RoomRefOf(RoomOffer offer) =>
        FakeBookingSupport.Extra(offer, "sessionId") is { } session && FakeBookingSupport.Extra(offer, "roomId") is { } room
        && FakeBookingSupport.Extra(offer, "mealPlan") is { } meal
            ? (session, room, meal)
            : null;
}
