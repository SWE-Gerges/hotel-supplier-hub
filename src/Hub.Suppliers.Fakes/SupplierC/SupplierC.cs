using Hub.Core;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using Hub.Abstractions;
using Microsoft.Extensions.Options;

namespace Hub.Suppliers.Fakes;

// supplier-c speaks a two-step XML protocol. Step 1 (availability) returns a session id and the available hotels;
// step 2 (GetRooms) returns the rooms of one hotel for that session. Sessions expire.

internal static class SupplierCXml
{
    public static readonly XNamespace Ns = "urn:example:supplier-c:v1";

    /// <summary>Parses supplier XML without ever resolving DTDs or external entities.</summary>
    public static XDocument Parse(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 5_000_000,
        };

        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader);
    }
}

internal sealed record CAvailabilityRequest(string CityCode, Stay Stay);

/// <summary>The hotel element from step 1 together with the rooms element from step 2 (null when not fetched).</summary>
internal sealed record CHotelPayload(XElement Hotel, XElement? Rooms, string? SessionId = null);

internal sealed record CBatch(IReadOnlyList<CHotelPayload> Hotels);

/// <summary>The in-process stand-in for supplier-c's remote service: it answers with XML text.</summary>
internal sealed partial class SupplierCServer(SyntheticCatalog catalog, FaultController faults, TimeProvider time, IOptions<FakeSuppliersOptions> options, FakeSupplierLedger ledger)
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public async Task<string> AvailabilityAsync(CAvailabilityRequest request, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Finish(ErrorDocument(kind), decision);
        }

        var city = catalog.Cities.FirstOrDefault(c => CityCodeOf(c) == request.CityCode);
        var hotels = city is null ? [] : catalog.HotelsListedBy(FakeSupplierIds.SupplierC, city.CanonicalId);

        var now = time.GetUtcNow();
        var sessionId = "S-" + Guid.NewGuid().ToString("N")[..12];
        _sessions[sessionId] = new Session(request.Stay, now + options.Value.SupplierC.SessionTtl);
        foreach (var expired in _sessions.Where(s => s.Value.ExpiresAt < now - TimeSpan.FromMinutes(10)).Select(static s => s.Key))
        {
            _sessions.TryRemove(expired, out _);
        }

        var ns = SupplierCXml.Ns;
        var hotelElements = hotels
            .Where(h => catalog.OffersFor(FakeSupplierIds.SupplierC, h, request.Stay).Count > 0)
            .Select(h => new XElement(
                ns + "Hotel",
                new XAttribute("Code", catalog.CodeFor(FakeSupplierIds.SupplierC, h)),
                new XAttribute("Stars", h.Stars),
                new XElement(ns + "Name", catalog.NameFor(FakeSupplierIds.SupplierC, h)),
                new XElement(ns + "Position", new XAttribute("Lat", Invariant(h.Latitude)), new XAttribute("Lon", Invariant(h.Longitude))),
                new XElement(ns + "Address", $"{h.Street}, {h.City.Name}")));

        var root = new XElement(
            ns + "AvailabilityResponse",
            new XAttribute("Session", sessionId),
            new XAttribute("ExpiresAt", (now + options.Value.SupplierC.SessionTtl).ToString("O", CultureInfo.InvariantCulture)),
            new XElement(ns + "Hotels", hotelElements));
        return Finish(new XDocument(root), decision);
    }

    public async Task<string> GetRoomsAsync(string sessionId, string hotelCode, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Finish(ErrorDocument(kind), decision);
        }

        if (!_sessions.TryGetValue(sessionId, out var session) || session.ExpiresAt <= time.GetUtcNow())
        {
            return Finish(ErrorDocument("SESSION_EXPIRED", "The session has expired."), decision);
        }

        var ns = SupplierCXml.Ns;
        var hotel = catalog.FindByCode(FakeSupplierIds.SupplierC, hotelCode);
        if (hotel is null)
        {
            return Finish(ErrorDocument("HOTEL_NOT_FOUND", "No such hotel."), decision);
        }

        var rooms = catalog.OffersFor(FakeSupplierIds.SupplierC, hotel, session.Stay).Select(o => ToRoomElement(o, session.Stay));
        var root = new XElement(
            ns + "RoomsResponse",
            new XAttribute("Session", sessionId),
            new XAttribute("Hotel", hotelCode),
            new XElement(ns + "Rooms", rooms));
        return Finish(new XDocument(root), decision);
    }

    public static string CityCodeOf(FakeCity city) => city.CanonicalId switch
    {
        "city-aldermoor" => "ALDERMOOR",
        "city-brightwater" => "BRIGHTWATER",
        _ => "CEDARFALL",
    };

    private static XElement ToRoomElement(PhysicalOffer offer, Stay stay)
    {
        var ns = SupplierCXml.Ns;
        var view = offer.Room.View is null ? string.Empty : $" ({offer.Room.View})";
        var policy = offer.Refundable
            ? $"Free cancellation until {offer.FreeCancellationUntil:yyyy-MM-dd}."
            : "This rate cannot be cancelled.";

        return new XElement(
            ns + "Room",
            new XAttribute("Id", offer.Room.Code),
            new XAttribute("Name", $"{offer.Room.Bed} {offer.Room.Grade}{view}"),
            new XAttribute("MealPlan", MealPlan(offer.Board)),
            new XAttribute("Refundable", offer.Refundable ? "true" : "false"),
            new XElement(ns + "Price", new XAttribute("Currency", stay.Currency), offer.Total.ToString("0.00", CultureInfo.InvariantCulture)),
            new XElement(ns + "CancelPolicy", new XCData(policy)),
            PolicyElement(offer, stay));
    }

    private static XElement PolicyElement(PhysicalOffer offer, Stay stay)
    {
        var ns = SupplierCXml.Ns;
        return offer.Refundable
            ? new XElement(ns + "Policy", new XElement(ns + "Tier", new XAttribute("From", offer.FreeCancellationUntil.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), new XAttribute("Kind", "nights"), new XAttribute("Value", "1")))
            : new XElement(ns + "Policy", new XElement(ns + "Tier", new XAttribute("From", "NOW"), new XAttribute("Kind", "percent"), new XAttribute("Value", "100")));
    }

    private static string MealPlan(BoardType board) => board switch
    {
        BoardType.RoomOnly => "ROOM_ONLY",
        BoardType.Breakfast => "BREAKFAST_INCLUDED",
        BoardType.HalfBoard => "HALF_BOARD",
        BoardType.FullBoard => "FULL_BOARD",
        _ => "ALL_INCLUSIVE",
    };

    private static XDocument ErrorDocument(SupplierErrorKind kind) => kind switch
    {
        SupplierErrorKind.Transient => ErrorDocument("E503", "Service temporarily unavailable."),
        SupplierErrorKind.RateLimited => ErrorDocument("E429", "Too many requests."),
        SupplierErrorKind.Unavailable => ErrorDocument("E410", "Service retired."),
        SupplierErrorKind.Auth => ErrorDocument("E401", "Credentials rejected."),
        SupplierErrorKind.InvalidRequest => ErrorDocument("E400", "Bad request."),
        SupplierErrorKind.Timeout => ErrorDocument("E504", "Upstream timeout."),
        _ => ErrorDocument("E500", "Internal error."),
    };

    private static XDocument ErrorDocument(string code, string message) =>
        new(new XElement(SupplierCXml.Ns + "Error", new XAttribute("Code", code), message));

    private static string Finish(XDocument document, FaultDecision decision)
    {
        var xml = document.ToString(SaveOptions.DisableFormatting);
        return decision.Malformed ? FakeSupplierSupport.Corrupt(xml) : xml;
    }

    private static string Invariant(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

    private sealed class Session(Stay stay, DateTimeOffset expiresAt)
    {
        public Stay Stay { get; } = stay;

        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
    }

}

internal sealed class SupplierCNormalizer(RoomExtraMapperRegistry extras) : ISupplierResponseNormalizer<CBatch>
{
    public SupplierCNormalizer()
        : this(RoomExtraMapperRegistry.Of(new SupplierCRoomExtraMapper()))
    {
    }

    private static readonly XNamespace Ns = SupplierCXml.Ns;

    public Result<IReadOnlyList<Hotel>> Normalize(CBatch response, SupplierSearchQuery query)
    {
        var hotels = new List<Hotel>();
        foreach (var payload in response.Hotels)
        {
            var element = payload.Hotel;
            var code = (string?)element.Attribute("Code");
            var name = Text(element.Element(Ns + "Name"));
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var offers = (payload.Rooms?.Elements(Ns + "Room") ?? [])
                .Select(room => ToOffer(code, room, query, payload.SessionId))
                .OfType<RoomOffer>()
                .ToList();
            if (offers.Count == 0)
            {
                continue;
            }

            hotels.Add(new Hotel
            {
                Name = name,
                Stars = int.TryParse((string?)element.Attribute("Stars"), NumberStyles.None, CultureInfo.InvariantCulture, out var stars)
                    ? Math.Clamp(stars, 0, 5)
                    : 0,
                Location = ParseLocation(element.Element(Ns + "Position")),
                Address = Text(element.Element(Ns + "Address")),
                Offers = offers,
                Sources = [new SupplierHotelRef(FakeSupplierIds.SupplierC, code)],
            });
        }

        return Result.Ok<IReadOnlyList<Hotel>>(hotels);
    }

    /// <summary>Reads an Error element if the document is one; the document root is checked, not its descendants.</summary>
    public static SupplierError? ReadError(XElement root)
    {
        if (root.Name != Ns + "Error")
        {
            return null;
        }

        var code = (string?)root.Attribute("Code") ?? "unknown";
        var message = root.Value;
        return code switch
        {
            "E503" => SupplierError.Transient(message),
            "E429" => new SupplierError(SupplierErrorKind.RateLimited, message),
            "E410" => SupplierError.Unavailable(message),
            "E401" => new SupplierError(SupplierErrorKind.Auth, message),
            "E400" => SupplierError.InvalidRequest(message),
            "E504" => SupplierError.Timeout(message),
            "E409" => SupplierError.PriceChanged(message),
            "E422" => SupplierError.SoldOut(message),
            "E404" => SupplierError.NotFound(message),
            "SESSION_EXPIRED" => new SupplierError(SupplierErrorKind.SessionExpired, message, Retryable: false, Raw: code),
            _ => SupplierError.Unknown(message, code),
        };
    }

    public static BoardType ParseBoard(string? plan) => plan?.Trim().ToUpperInvariant() switch
    {
        "ROOM_ONLY" => BoardType.RoomOnly,
        "BREAKFAST_INCLUDED" => BoardType.Breakfast,
        "HALF_BOARD" => BoardType.HalfBoard,
        "FULL_BOARD" => BoardType.FullBoard,
        "ALL_INCLUSIVE" => BoardType.AllInclusive,
        _ => BoardType.Unknown,
    };

    private RoomOffer? ToOffer(string hotelCode, XElement room, SupplierSearchQuery query, string? sessionId)
    {
        var id = (string?)room.Attribute("Id");
        var priceElement = room.Element(Ns + "Price");
        var currency = (string?)priceElement?.Attribute("Currency");
        if (string.IsNullOrWhiteSpace(id) || priceElement is null || currency is not { Length: 3 }
            || !decimal.TryParse(priceElement.Value.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            || amount < 0)
        {
            return null;
        }

        var rawPlan = (string?)room.Attribute("MealPlan") ?? string.Empty;
        var stay = query.Request.Stay;
        var price = new Money(amount, currency.ToUpperInvariant());

        return new RoomOffer
        {
            OfferId = $"{FakeSupplierIds.SupplierCValue}.{hotelCode}.{id}.{rawPlan}.{stay.CheckIn:yyyyMMdd}.{stay.Nights}",
            Supplier = FakeSupplierIds.SupplierC,
            SupplierHotelCode = hotelCode,
            RoomName = (string?)room.Attribute("Name") ?? id,
            Board = ParseBoard(rawPlan),
            RawBoard = rawPlan,
            Refundable = false, // the hub's policy rules decide; the structured tiers travel in Extra
            Price = price,
            SellingPrice = price,
            Extra = extras.Map(room, new RoomExtraContext(FakeSupplierIds.SupplierC, hotelCode, sessionId)),
        };
    }

    private static GeoPoint? ParseLocation(XElement? position) =>
        position is not null
        && double.TryParse((string?)position.Attribute("Lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
        && double.TryParse((string?)position.Attribute("Lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            ? new GeoPoint(lat, lon)
            : null;

    private static string Text(XElement? element) => element?.Value.Trim() ?? string.Empty;
}

/// <summary>supplier-c client: availability first, then the rooms of each hotel in small concurrent batches.</summary>
internal sealed partial class SupplierCClient(
    SupplierCServer server,
    IOptions<FakeSuppliersOptions> options,
    SupplierCNormalizer normalizer) : ISupplierDescriptor, IHotelSearch, IPreBook, IBook, ICancel
{
    private const int MaxSessionRefreshes = 1;

    public SupplierId SupplierId => FakeSupplierIds.SupplierC;

    public SupplierCapabilities Capabilities =>
        SupplierCapabilities.Search | SupplierCapabilities.PreBook | SupplierCapabilities.Book | SupplierCapabilities.Cancel
        | SupplierCapabilities.DuplicateDetection | SupplierCapabilities.CityDirectory;

    public SearchStyle SearchStyle => SearchStyle.SessionTwoStep;

    public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(6);

    public int MaxConcurrency => 6;

    public async IAsyncEnumerable<Result<IReadOnlyList<Hotel>>> SearchAsync(
        SupplierSearchQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (FakeSupplierSupport.ValidateQuery(query) is { } invalid)
        {
            yield return Result.Fail<IReadOnlyList<Hotel>>(invalid);
            yield break;
        }

        var stay = FakeSupplierSupport.ToStay(query.Request);
        var holder = new SessionHolder(server, query.Codes.CityCode, stay);

        var opened = await FakeSupplierSupport.GuardAsync(() => holder.OpenAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        if (opened.IsFailure)
        {
            yield return Result.Fail<IReadOnlyList<Hotel>>(opened.Error);
            yield break;
        }

        foreach (var chunk in opened.Value.Chunk(options.Value.SupplierC.RoomsBatchSize))
        {
            var batch = await FakeSupplierSupport.GuardAsync(() => FetchRoomsAsync(holder, chunk, query, cancellationToken), cancellationToken).ConfigureAwait(false);
            yield return batch;
            if (batch.IsFailure)
            {
                yield break;
            }
        }
    }

    private async ValueTask<Result<IReadOnlyList<Hotel>>> FetchRoomsAsync(
        SessionHolder holder, XElement[] hotels, SupplierSearchQuery query, CancellationToken cancellationToken)
    {
        var fetches = hotels.Select(hotel => holder.GetRoomsAsync(hotel, cancellationToken).AsTask()).ToArray();
        var results = await Task.WhenAll(fetches).ConfigureAwait(false);

        var payloads = new List<CHotelPayload>(results.Length);
        foreach (var result in results)
        {
            if (result.IsFailure)
            {
                return Result.Fail<IReadOnlyList<Hotel>>(result.Error);
            }

            payloads.Add(result.Value);
        }

        return normalizer.Normalize(new CBatch(payloads), query);
    }

    /// <summary>Owns the current session and replaces it, at most a bounded number of times, when it expires mid-search.</summary>
    private sealed class SessionHolder(SupplierCServer server, string cityCode, Stay stay)
    {
        private readonly SemaphoreSlim _refresh = new(1, 1);
        private string _sessionId = string.Empty;
        private int _refreshes;

        public async ValueTask<Result<XElement[]>> OpenAsync(CancellationToken cancellationToken)
        {
            var xml = await server.AvailabilityAsync(new CAvailabilityRequest(cityCode, stay), cancellationToken).ConfigureAwait(false);
            var root = SupplierCXml.Parse(xml).Root ?? throw new XmlException("Empty document.");
            if (SupplierCNormalizer.ReadError(root) is { } error)
            {
                return Result.Fail<XElement[]>(error);
            }

            _sessionId = (string?)root.Attribute("Session") ?? throw new XmlException("Missing session id.");
            return Result.Ok(root.Element(SupplierCXml.Ns + "Hotels")?.Elements(SupplierCXml.Ns + "Hotel").ToArray() ?? []);
        }

        public async ValueTask<Result<CHotelPayload>> GetRoomsAsync(XElement hotel, CancellationToken cancellationToken)
        {
            var code = (string?)hotel.Attribute("Code") ?? string.Empty;
            while (true)
            {
                var sessionId = Volatile.Read(ref _sessionId);
                var xml = await server.GetRoomsAsync(sessionId, code, cancellationToken).ConfigureAwait(false);
                var root = SupplierCXml.Parse(xml).Root ?? throw new XmlException("Empty document.");

                if (SupplierCNormalizer.ReadError(root) is not { } error)
                {
                    return Result.Ok(new CHotelPayload(hotel, root.Element(SupplierCXml.Ns + "Rooms"), sessionId));
                }

                if (error.Raw != "SESSION_EXPIRED")
                {
                    return Result.Fail<CHotelPayload>(error);
                }

                var refreshed = await RefreshAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (refreshed.IsFailure)
                {
                    return Result.Fail<CHotelPayload>(refreshed.Error);
                }
            }
        }

        private async ValueTask<Result<bool>> RefreshAsync(string expiredSessionId, CancellationToken cancellationToken)
        {
            await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!string.Equals(Volatile.Read(ref _sessionId), expiredSessionId, StringComparison.Ordinal))
                {
                    return Result.Ok(true); // another call already replaced the session
                }

                if (_refreshes >= MaxSessionRefreshes)
                {
                    return Result.Fail<bool>(SupplierError.Transient("The session kept expiring during the search."));
                }

                _refreshes++;
                var reopened = await OpenAsync(cancellationToken).ConfigureAwait(false);
                return reopened.IsFailure ? Result.Fail<bool>(reopened.Error) : Result.Ok(true);
            }
            finally
            {
                _refresh.Release();
            }
        }
    }
}
