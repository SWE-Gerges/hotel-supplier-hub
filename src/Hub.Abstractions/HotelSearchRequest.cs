using System.Text.Json.Serialization;

namespace Hub.Abstractions;

/// <summary>A tenant-independent hotel availability request expressed in canonical terms.</summary>
public sealed record HotelSearchRequest
{
    public const int MaxRooms = 4;
    public const int MaxNights = 30;

    /// <summary>Canonical city id; the mapping layer translates it to each supplier's own codes.</summary>
    public required string Destination { get; init; }

    public required DateOnly CheckIn { get; init; }

    public required DateOnly CheckOut { get; init; }

    public required IReadOnlyList<Occupancy> Rooms { get; init; }

    /// <summary>Two-letter nationality code of the lead guest.</summary>
    public required string Nationality { get; init; }

    public required string Currency { get; init; }

    public int? MaxResults { get; init; }

    /// <summary>Optional restriction to specific suppliers; intersected with the tenant's enabled suppliers.</summary>
    public IReadOnlyList<SupplierId>? Suppliers { get; init; }

    [JsonIgnore]
    public DateRange Stay => new(CheckIn, CheckOut);

    /// <summary>Returns field name to message pairs; empty when the request is valid.</summary>
    public IReadOnlyDictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(Destination))
        {
            errors[nameof(Destination)] = ["Destination is required."];
        }

        if (CheckOut <= CheckIn)
        {
            errors[nameof(CheckOut)] = ["CheckOut must be after CheckIn."];
        }
        else if (Stay.Nights > MaxNights)
        {
            errors[nameof(CheckOut)] = [$"A stay cannot exceed {MaxNights} nights."];
        }

        if (Rooms is null || Rooms.Count < 1 || Rooms.Count > MaxRooms)
        {
            errors[nameof(Rooms)] = [$"Between 1 and {MaxRooms} rooms are required."];
        }
        else if (Rooms.Any(static room => !IsValidOccupancy(room)))
        {
            errors[nameof(Rooms)] = ["Each room needs 1-6 adults and at most 4 children aged 0-17."];
        }

        if (!IsUpperCode(Nationality, 2))
        {
            errors[nameof(Nationality)] = ["Nationality must be a two-letter upper-case code."];
        }

        if (!IsUpperCode(Currency, 3))
        {
            errors[nameof(Currency)] = ["Currency must be a three-letter upper-case code."];
        }

        if (MaxResults is < 1 or > 500)
        {
            errors[nameof(MaxResults)] = ["MaxResults must be between 1 and 500."];
        }

        return errors;
    }

    private static bool IsValidOccupancy(Occupancy? room) =>
        room is { ChildAges: not null }
        && room.Adults is >= 1 and <= 6
        && room.ChildAges.Count <= 4
        && room.ChildAges.All(static age => age is >= 0 and <= 17);

    private static bool IsUpperCode(string? value, int length) =>
        value is not null && value.Length == length && value.All(static c => c is >= 'A' and <= 'Z');
}

/// <summary>The supplier's own city and country codes for a canonical destination.</summary>
public sealed record SupplierCityCodes(string CityCode, string CountryCode);

/// <summary>A request plus the codes the target supplier understands for the destination.</summary>
public sealed record SupplierSearchQuery(HotelSearchRequest Request, SupplierCityCodes Codes);
