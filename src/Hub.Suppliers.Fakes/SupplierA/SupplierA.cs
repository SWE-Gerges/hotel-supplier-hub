using Hub.Core;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hub.Abstractions;
using Microsoft.Extensions.Options;

namespace Hub.Suppliers.Fakes;

// supplier-a speaks paged synchronous JSON: list the hotel codes of a city, then request rates for a batch of
// codes per call. Responses use camelCase, nested rate objects, free-form cancellation text, and prices as
// strings (one of them with a decimal comma).

internal sealed record ACodesResponse(List<string>? HotelCodes, AError? Error);

internal sealed record AError(string Code, string Message);

internal sealed record AHotelsResponse(List<AHotel>? Hotels, AError? Error);

internal sealed record AHotel(string HotelCode, string HotelName, string StarRating, AGeo? Geo, AAddress? Address, List<ARoom>? Rooms);

internal sealed record AGeo(double Lat, double Lng);

internal sealed record AAddress(string? Line1, string? City, string? CountryCode);

internal sealed record ARoom(string RoomCode, string RoomDescription, string BoardCode, ARate Rate);

internal sealed record ARate(AMoney Net, AMoney? Taxes, string CancellationPolicy);

internal sealed record AMoney(string Amount, string Currency);

internal sealed record ARatesRequest(IReadOnlyList<string> HotelCodes, Stay Stay);

internal static class SupplierAJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>The in-process stand-in for supplier-a's remote service: it answers with JSON text.</summary>
internal sealed partial class SupplierAServer(SyntheticCatalog catalog, FaultController faults, TimeProvider time, FakeSupplierLedger ledger)
{
    public async Task<string> ListHotelCodesAsync(string cityCode, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Serialize(new ACodesResponse(null, ErrorFor(kind)), decision);
        }

        var city = catalog.Cities.FirstOrDefault(c => CityCodeOf(c) == cityCode);
        var codes = city is null
            ? []
            : catalog.HotelsListedBy(FakeSupplierIds.SupplierA, city.CanonicalId).Select(h => catalog.CodeFor(FakeSupplierIds.SupplierA, h)).ToList();
        return Serialize(new ACodesResponse(codes, null), decision);
    }

    public async Task<string> GetRatesAsync(ARatesRequest request, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Serialize(new AHotelsResponse(null, ErrorFor(kind)), decision);
        }

        var hotels = new List<AHotel>();
        foreach (var code in request.HotelCodes)
        {
            if (catalog.FindByCode(FakeSupplierIds.SupplierA, code) is not { } hotel)
            {
                continue;
            }

            var offers = catalog.OffersFor(FakeSupplierIds.SupplierA, hotel, request.Stay);
            if (offers.Count == 0)
            {
                continue;
            }

            hotels.Add(new AHotel(
                code,
                catalog.NameFor(FakeSupplierIds.SupplierA, hotel),
                hotel.Stars.ToString(CultureInfo.InvariantCulture),
                new AGeo(hotel.Latitude, hotel.Longitude),
                new AAddress(hotel.Street, hotel.City.Name, "XA"),
                offers.Select(o => ToRoom(o, request.Stay)).ToList()));
        }

        return Serialize(new AHotelsResponse(hotels, null), decision);
    }

    public static string CityCodeOf(FakeCity city) => city.CanonicalId switch
    {
        "city-aldermoor" => "ALD",
        "city-brightwater" => "BRW",
        _ => "CDF",
    };

    private static ARoom ToRoom(PhysicalOffer offer, Stay stay)
    {
        var view = offer.Room.View is null ? string.Empty : $" - {offer.Room.View}";
        var suite = offer.Room.Bed == "Suite" ? string.Empty : " Room";

        // Prices are split into a net amount (dot) and taxes (decimal comma), as this supplier does.
        var taxes = Math.Round(offer.Total * 0.1m, 2, MidpointRounding.AwayFromZero);
        var net = offer.Total - taxes;
        var cancellation = PolicyText(offer, stay);

        return new ARoom(
            offer.Room.Code,
            $"{offer.Room.Grade} {offer.Room.Bed}{suite}{view}",
            BoardCode(offer.Board),
            new ARate(
                new AMoney(net.ToString("0.00", CultureInfo.InvariantCulture), stay.Currency),
                new AMoney(taxes.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ','), stay.Currency),
                cancellation));
    }

    /// <summary>
    /// supplier-a words its policy in free text, and not always the same way: the phrasing and the date format depend on the
    /// room, so one search returns several of each. The meaning is the same as the supplier's ledger applies.
    /// </summary>
    internal static string PolicyText(PhysicalOffer offer, Stay stay)
    {
        var variant = DeterministicHash.Below(DeterministicHash.Mix(DeterministicHash.OfString(offer.Room.Code), (ulong)stay.CheckIn.DayNumber, (ulong)offer.Board), 12);
        if (!offer.Refundable)
        {
            return (variant % 3) switch
            {
                0 => "Non-refundable. The full amount is charged on cancellation.",
                1 => "This rate cannot be cancelled or changed.",
                _ => "No free cancellation - 100% penalty applies.",
            };
        }

        var date = offer.FreeCancellationUntil.ToString((variant / 4) switch { 0 => "yyyy-MM-dd", 1 => "dd/MM/yyyy", _ => "d MMM yyyy" }, CultureInfo.InvariantCulture);
        return (variant % 4) switch
        {
            0 => $"Free cancellation until {date}. Afterwards the first night is charged.",
            1 => $"Cancel free of charge before {date}; after that one night will be charged.",
            2 => $"Free cancellation up to {date} - thereafter 1 night penalty.",
            _ => $"Cancellations made after {date} incur a charge of 1 night.",
        };
    }

    private static string BoardCode(BoardType board) => board switch
    {
        BoardType.RoomOnly => "RO",
        BoardType.Breakfast => "BB",
        BoardType.HalfBoard => "HB",
        BoardType.FullBoard => "FB",
        _ => "AI",
    };

    private static AError ErrorFor(SupplierErrorKind kind) => kind switch
    {
        SupplierErrorKind.Transient => new AError("SERVICE_BUSY", "The service is busy, try again."),
        SupplierErrorKind.RateLimited => new AError("TOO_MANY_REQUESTS", "Request quota exceeded."),
        SupplierErrorKind.Unavailable => new AError("SERVICE_DOWN", "The service is down for maintenance."),
        SupplierErrorKind.Auth => new AError("INVALID_CREDENTIALS", "The credentials were rejected."),
        SupplierErrorKind.InvalidRequest => new AError("BAD_REQUEST", "The request was not understood."),
        SupplierErrorKind.Timeout => new AError("UPSTREAM_TIMEOUT", "An upstream call timed out."),
        _ => new AError("INTERNAL_ERROR", "Something went wrong."),
    };

    private static string Serialize<T>(T payload, FaultDecision decision)
    {
        var json = JsonSerializer.Serialize(payload, SupplierAJson.Options);
        return decision.Malformed ? FakeSupplierSupport.Corrupt(json) : json;
    }
}

internal sealed class SupplierANormalizer(RoomExtraMapperRegistry extras) : ISupplierResponseNormalizer<AHotelsResponse>
{
    public SupplierANormalizer()
        : this(RoomExtraMapperRegistry.Of(new SupplierARoomExtraMapper()))
    {
    }

    public Result<IReadOnlyList<Hotel>> Normalize(AHotelsResponse response, SupplierSearchQuery query)
    {
        if (response.Error is { } error)
        {
            return Result.Fail<IReadOnlyList<Hotel>>(MapError(error));
        }

        var hotels = new List<Hotel>();
        foreach (var raw in response.Hotels ?? [])
        {
            var offers = (raw.Rooms ?? []).Select(room => ToOffer(raw.HotelCode, room, query)).OfType<RoomOffer>().ToList();
            if (offers.Count == 0)
            {
                continue;
            }

            hotels.Add(new Hotel
            {
                Name = raw.HotelName.Trim(),
                Stars = ParseStars(raw.StarRating),
                Location = raw.Geo is null ? null : new GeoPoint(raw.Geo.Lat, raw.Geo.Lng),
                Address = string.Join(", ", new[] { raw.Address?.Line1, raw.Address?.City }.Where(static p => !string.IsNullOrWhiteSpace(p))),
                Offers = offers,
                Sources = [new SupplierHotelRef(FakeSupplierIds.SupplierA, raw.HotelCode)],
            });
        }

        return Result.Ok<IReadOnlyList<Hotel>>(hotels);
    }

    public static SupplierError MapError(AError error) => error.Code switch
    {
        "SERVICE_BUSY" => SupplierError.Transient(error.Message),
        "TOO_MANY_REQUESTS" => new SupplierError(SupplierErrorKind.RateLimited, error.Message),
        "SERVICE_DOWN" => SupplierError.Unavailable(error.Message),
        "INVALID_CREDENTIALS" => new SupplierError(SupplierErrorKind.Auth, error.Message),
        "BAD_REQUEST" => SupplierError.InvalidRequest(error.Message),
        "UPSTREAM_TIMEOUT" => SupplierError.Timeout(error.Message),
        "PRICE_CHANGED" => SupplierError.PriceChanged(error.Message),
        "BOOKING_NOT_FOUND" => SupplierError.NotFound(error.Message),
        "SOLD_OUT" => SupplierError.SoldOut(error.Message),
        _ => SupplierError.Unknown(error.Message, error.Code),
    };

    private RoomOffer? ToOffer(string hotelCode, ARoom room, SupplierSearchQuery query)
    {
        // An offer whose price cannot be read is dropped rather than shown with a wrong price.
        if (!TolerantDecimal.TryParse(room.Rate.Net.Amount, out var net) || net < 0)
        {
            return null;
        }

        var taxes = 0m;
        if (room.Rate.Taxes is { } t && !TolerantDecimal.TryParse(t.Amount, out taxes))
        {
            return null;
        }

        var currency = room.Rate.Net.Currency;
        if (currency is not { Length: 3 })
        {
            return null;
        }

        var price = new Money(net + taxes, currency.ToUpperInvariant());
        var stay = query.Request.Stay;

        return new RoomOffer
        {
            OfferId = $"{FakeSupplierIds.SupplierAValue}.{hotelCode}.{room.RoomCode}.{room.BoardCode}.{stay.CheckIn:yyyyMMdd}.{stay.Nights}",
            Supplier = FakeSupplierIds.SupplierA,
            SupplierHotelCode = hotelCode,
            RoomName = room.RoomDescription.Trim(),
            Board = ParseBoard(room.BoardCode),
            RawBoard = room.BoardCode,
            Refundable = false, // the hub's policy rules decide; the raw text travels in Extra
            Price = price,
            SellingPrice = price,
            Extra = extras.Map(room, new RoomExtraContext(FakeSupplierIds.SupplierA, hotelCode)),
        };
    }

    public static BoardType ParseBoard(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        "RO" => BoardType.RoomOnly,
        "BB" => BoardType.Breakfast,
        "HB" => BoardType.HalfBoard,
        "FB" => BoardType.FullBoard,
        "AI" => BoardType.AllInclusive,
        _ => BoardType.Unknown,
    };

    public static int ParseStars(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return 0;
        }

        var digits = new string(rating.Trim().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var stars) ? Math.Clamp(stars, 0, 5) : 0;
    }
}

/// <summary>supplier-a client: pages through the city's hotel codes and streams one batch per page.</summary>
internal sealed partial class SupplierAClient(
    SupplierAServer server,
    IOptions<FakeSuppliersOptions> options,
    SupplierANormalizer normalizer) : ISupplierDescriptor, IHotelSearch, IRoomAvailability, IPreBook, IBook, ICancel, IRetrieve
{
    public SupplierId SupplierId => FakeSupplierIds.SupplierA;

    public SupplierCapabilities Capabilities =>
        SupplierCapabilities.Search | SupplierCapabilities.RoomAvailability | SupplierCapabilities.PreBook
        | SupplierCapabilities.Book | SupplierCapabilities.Cancel | SupplierCapabilities.Retrieve | SupplierCapabilities.DuplicateDetection | SupplierCapabilities.CityDirectory;

    public SearchStyle SearchStyle => SearchStyle.PagedSync;

    public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(5);

    public int MaxConcurrency => 8;

    public async IAsyncEnumerable<Result<IReadOnlyList<Hotel>>> SearchAsync(
        SupplierSearchQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (FakeSupplierSupport.ValidateQuery(query) is { } invalid)
        {
            yield return Result.Fail<IReadOnlyList<Hotel>>(invalid);
            yield break;
        }

        var codes = await FakeSupplierSupport.GuardAsync(() => ListCodesAsync(query, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (codes.IsFailure)
        {
            yield return Result.Fail<IReadOnlyList<Hotel>>(codes.Error);
            yield break;
        }

        var stay = FakeSupplierSupport.ToStay(query.Request);
        foreach (var page in codes.Value.Chunk(options.Value.SupplierA.PageSize))
        {
            var batch = await FakeSupplierSupport.GuardAsync(() => FetchPageAsync(page, stay, query, cancellationToken), cancellationToken).ConfigureAwait(false);
            yield return batch;
            if (batch.IsFailure)
            {
                yield break;
            }
        }
    }

    private async ValueTask<Result<string[]>> ListCodesAsync(SupplierSearchQuery query, CancellationToken cancellationToken)
    {
        var json = await server.ListHotelCodesAsync(query.Codes.CityCode, cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<ACodesResponse>(json, SupplierAJson.Options)
                       ?? throw new JsonException("Empty response.");
        return response.Error is { } error
            ? Result.Fail<string[]>(SupplierANormalizer.MapError(error))
            : Result.Ok((response.HotelCodes ?? []).ToArray());
    }

    private async ValueTask<Result<IReadOnlyList<Hotel>>> FetchPageAsync(
        string[] page, Stay stay, SupplierSearchQuery query, CancellationToken cancellationToken)
    {
        var json = await server.GetRatesAsync(new ARatesRequest(page, stay), cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<AHotelsResponse>(json, SupplierAJson.Options)
                       ?? throw new JsonException("Empty response.");
        return normalizer.Normalize(response, query);
    }
}
