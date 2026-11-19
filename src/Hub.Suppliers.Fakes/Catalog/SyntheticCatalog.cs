using Hub.Abstractions;

namespace Hub.Suppliers.Fakes;

/// <summary>Stable pseudo-random values derived from a seed, so every run and every supplier agrees on the data.</summary>
public static class DeterministicHash
{
    public static ulong Mix(ulong seed, params ReadOnlySpan<ulong> parts)
    {
        var h = Step(seed);
        foreach (var part in parts)
        {
            h = Step(h ^ Step(part));
        }

        return h;
    }

    public static ulong OfString(string value)
    {
        var h = 14695981039346656037UL;
        foreach (var c in value)
        {
            h = (h ^ c) * 1099511628211UL;
        }

        return h;
    }

    public static int Below(ulong hash, int exclusiveMax) => (int)(hash % (ulong)exclusiveMax);

    public static double Unit(ulong hash) => (hash >> 11) / (double)(1UL << 53);

    private static ulong Step(ulong x)
    {
        unchecked
        {
            var z = x + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}

public sealed record FakeCity(string CanonicalId, string Name, double Latitude, double Longitude);

/// <summary>A hotel as it really is; each supplier lists it under its own code, name and prices.</summary>
public sealed record PhysicalHotel(
    int Index,
    FakeCity City,
    string Brand,
    string Qualifier,
    string Suffix,
    int Stars,
    double Latitude,
    double Longitude,
    string Street,
    IReadOnlyList<PhysicalRoom> Rooms)
{
    public string Name => $"{Brand} {Qualifier} {Suffix}";
}

public sealed record PhysicalRoom(string Code, string Grade, string Bed, string? View, decimal BaseRate, IReadOnlyList<BoardType> Boards);

public sealed record Stay(DateOnly CheckIn, DateOnly CheckOut, IReadOnlyList<Occupancy> Rooms, string Currency)
{
    public int Nights => CheckOut.DayNumber - CheckIn.DayNumber;
}

public sealed record PhysicalOffer(PhysicalRoom Room, BoardType Board, decimal Total, bool Refundable, DateOnly FreeCancellationUntil);

/// <summary>
/// A deterministic synthetic market: a few fictional cities with physical hotels that several suppliers list
/// under different codes, slightly different names and different prices, so merging across suppliers is meaningful.
/// </summary>
public sealed class SyntheticCatalog
{
    public const decimal UsdPerEur = 1.08m;

    private static readonly string[] Brands =
    [
        "Maple", "Harbor", "Cedar", "Willow", "Summit", "Lagoon", "Aurora", "Meridian", "Coral", "Falcon", "Birch", "Solstice",
        "Granite", "Tidewater", "Heron", "Linden", "Alder", "Sandpiper", "Ember", "Hollow", "Marlow", "Quarry", "Orchard", "Bayleaf",
    ];

    private static readonly string[] Qualifiers = ["Central", "Riverside", "Garden", "Plaza", "Park", "Square", "Heights", "Terrace"];

    private static readonly string[] Suffixes = ["Hotel", "Suites", "Resort", "Inn", "Lodge", "Residence"];

    private static readonly string[] Streets = ["Harbor Road", "Station Street", "Market Lane", "Orchard Avenue", "Mill Row", "Bridge Street"];

    private static readonly PhysicalRoom[] RoomTemplates =
    [
        new("std-dbl", "Standard", "Double", null, 90m, [BoardType.RoomOnly, BoardType.Breakfast]),
        new("sup-dbl-sea", "Superior", "Double", "Sea View", 125m, [BoardType.RoomOnly, BoardType.Breakfast, BoardType.HalfBoard]),
        new("dlx-twn", "Deluxe", "Twin", null, 140m, [BoardType.Breakfast, BoardType.HalfBoard]),
        new("jr-ste-city", "Junior", "Suite", "City View", 185m, [BoardType.Breakfast, BoardType.HalfBoard, BoardType.AllInclusive]),
        new("fam-ste-gdn", "Family", "Suite", "Garden View", 215m, [BoardType.Breakfast, BoardType.FullBoard]),
    ];

    private readonly int _seed;
    private readonly IReadOnlyDictionary<SupplierId, int> _coverage;

    public SyntheticCatalog(int seed, int hotelsPerCity, IReadOnlyDictionary<SupplierId, int> coveragePercent)
    {
        _seed = seed;
        _coverage = coveragePercent;

        Cities =
        [
            new FakeCity("city-aldermoor", "Aldermoor", 12.10, 31.20),
            new FakeCity("city-brightwater", "Brightwater", -8.40, 44.75),
            new FakeCity("city-cedarfall", "Cedarfall", 25.30, -17.60),
        ];

        var hotels = new List<PhysicalHotel>();
        for (var c = 0; c < Cities.Count; c++)
        {
            for (var i = 0; i < hotelsPerCity; i++)
            {
                hotels.Add(CreateHotel(Cities[c], c * 1000 + i, i));
            }
        }

        Hotels = hotels;
    }

    public IReadOnlyList<FakeCity> Cities { get; }

    public IReadOnlyList<PhysicalHotel> Hotels { get; }

    /// <summary>Physical hotels a supplier lists in a city; each supplier covers a different share of the market.</summary>
    public IReadOnlyList<PhysicalHotel> HotelsListedBy(SupplierId supplier, string canonicalCityId)
    {
        var coverage = _coverage.GetValueOrDefault(supplier, 100);
        var supplierKey = DeterministicHash.OfString(supplier.Value);
        return Hotels
            .Where(h => h.City.CanonicalId == canonicalCityId
                        && DeterministicHash.Below(DeterministicHash.Mix((ulong)_seed, 1, supplierKey, (ulong)h.Index), 100) < coverage)
            .ToArray();
    }

    public bool Lists(SupplierId supplier, PhysicalHotel hotel) =>
        HotelsListedBy(supplier, hotel.City.CanonicalId).Any(h => h.Index == hotel.Index);

    public string CodeFor(SupplierId supplier, PhysicalHotel hotel) => supplier.Value switch
    {
        FakeSupplierIds.SupplierAValue => (300000 + (hotel.Index * 13)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        FakeSupplierIds.SupplierBValue => "BX-" + ToBase36((hotel.Index * 37) + 1000),
        _ => "HTL" + hotel.Index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture),
    };

    public string NameFor(SupplierId supplier, PhysicalHotel hotel) => supplier.Value switch
    {
        FakeSupplierIds.SupplierAValue => hotel.Name,
        FakeSupplierIds.SupplierBValue => $"{hotel.Suffix} {hotel.Brand} {hotel.Qualifier}".ToUpperInvariant(),
        _ => $"The {hotel.Brand} {hotel.Qualifier}",
    };

    public PhysicalHotel? FindByCode(SupplierId supplier, string code) =>
        Hotels.FirstOrDefault(h => string.Equals(CodeFor(supplier, h), code, StringComparison.Ordinal) && Lists(supplier, h));

    /// <summary>Rooms and boards the supplier can sell for the stay, priced in the stay's currency.</summary>
    public IReadOnlyList<PhysicalOffer> OffersFor(SupplierId supplier, PhysicalHotel hotel, Stay stay)
    {
        var supplierKey = DeterministicHash.OfString(supplier.Value);
        var priceFactor = supplier.Value switch
        {
            FakeSupplierIds.SupplierAValue => 1.00m,
            FakeSupplierIds.SupplierBValue => 0.96m,
            _ => 1.04m,
        };
        var currencyFactor = stay.Currency == "USD" ? UsdPerEur : 1m;
        var starFactor = 1m + (0.15m * (hotel.Stars - 3));
        var guestFactor = stay.Rooms.Sum(static r => 1m + (0.6m * (r.Adults - 1)) + (0.3m * r.ChildAges.Count));

        var offers = new List<PhysicalOffer>();
        foreach (var room in hotel.Rooms)
        {
            var roomKey = DeterministicHash.OfString(room.Code);
            foreach (var board in room.Boards)
            {
                var key = DeterministicHash.Mix((ulong)_seed, 2, supplierKey, (ulong)hotel.Index, roomKey, (ulong)board, (ulong)stay.CheckIn.DayNumber);
                if (DeterministicHash.Below(key, 10) == 0)
                {
                    continue; // sold out for these dates
                }

                var boardFactor = board switch
                {
                    BoardType.RoomOnly => 1.00m,
                    BoardType.Breakfast => 1.08m,
                    BoardType.HalfBoard => 1.25m,
                    BoardType.FullBoard => 1.40m,
                    _ => 1.65m,
                };
                var jitter = 1m + ((decimal)((DeterministicHash.Unit(key) * 0.08) - 0.04));
                var total = Math.Round(
                    room.BaseRate * starFactor * boardFactor * priceFactor * jitter * currencyFactor * stay.Nights * guestFactor,
                    2,
                    MidpointRounding.AwayFromZero);

                var refundable = DeterministicHash.Below(DeterministicHash.Mix(key, 3), 3) != 0;
                offers.Add(new PhysicalOffer(room, board, total, refundable, stay.CheckIn.AddDays(-2)));
            }
        }

        return offers;
    }

    private PhysicalHotel CreateHotel(FakeCity city, int globalIndex, int cityIndex)
    {
        var h = DeterministicHash.Mix((ulong)_seed, 0, (ulong)globalIndex);
        var stars = 2 + DeterministicHash.Below(h, 4);
        var rooms = RoomTemplates
            .Where((_, i) => i < 2 || DeterministicHash.Below(DeterministicHash.Mix(h, (ulong)i), 100) < 60)
            .ToArray();

        return new PhysicalHotel(
            Index: globalIndex,
            City: city,
            Brand: Brands[cityIndex % Brands.Length],
            Qualifier: Qualifiers[cityIndex / Brands.Length % Qualifiers.Length],
            Suffix: Suffixes[DeterministicHash.Below(DeterministicHash.Mix(h, 100), Suffixes.Length)],
            Stars: stars,
            Latitude: Math.Round(city.Latitude + ((DeterministicHash.Unit(DeterministicHash.Mix(h, 101)) - 0.5) * 0.1), 5),
            Longitude: Math.Round(city.Longitude + ((DeterministicHash.Unit(DeterministicHash.Mix(h, 102)) - 0.5) * 0.1), 5),
            Street: $"{1 + DeterministicHash.Below(DeterministicHash.Mix(h, 103), 120)} {Streets[DeterministicHash.Below(DeterministicHash.Mix(h, 104), Streets.Length)]}",
            Rooms: rooms);
    }

    private static string ToBase36(int value)
    {
        const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var chars = new Stack<char>();
        do
        {
            chars.Push(digits[value % 36]);
            value /= 36;
        }
        while (value > 0);

        return new string(chars.ToArray());
    }
}
