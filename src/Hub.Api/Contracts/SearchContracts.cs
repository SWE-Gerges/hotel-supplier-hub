using System.Text.Json;
using Hub.Abstractions;
using Hub.Core;

namespace Hub.Api;

public sealed record RoomDto(int? Adults, List<int>? ChildAges);

public sealed record MergeOptionsDto(bool? UseMapping, int? MaxOffersPerHotel, decimal? PriceChangeThreshold);

/// <summary>Request body shared by the JSON search and the streaming search.</summary>
/// <param name="Destination">Canonical city id, for instance <c>city-aldermoor</c>.</param>
/// <param name="Suppliers">Optional supplier ids to restrict the search to; the tenant's enabled suppliers still apply.</param>
/// <param name="WaitMs">Deadline for the whole search in milliseconds (100 to 60000). Suppliers still running at the deadline are reported as timed out.</param>
/// <param name="SearchId">Optional client-chosen id so the search can be cancelled while it runs.</param>
/// <param name="Replaces">Id of an earlier search by the same caller to cancel before this one starts.</param>
public sealed record SearchRequestDto(
    string? Destination,
    DateOnly? CheckIn,
    DateOnly? CheckOut,
    List<RoomDto>? Rooms,
    string? Nationality,
    string? Currency,
    int? MaxResults,
    List<string>? Suppliers,
    int? WaitMs,
    string? SearchId,
    string? Replaces,
    MergeOptionsDto? Options);

public sealed record MappedSearch(HotelSearchRequest Request, SearchRunOptions Options);

internal static class SearchRequestMapper
{
    public const int MinWaitMs = 100;
    public const int MaxWaitMs = 60_000;

    public static bool TryMap(SearchRequestDto? dto, SearchOptions defaults, out MappedSearch mapped, out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        mapped = null!;

        if (dto is null)
        {
            errors["body"] = ["A request body is required."];
            return false;
        }

        if (dto.CheckIn is null)
        {
            errors["checkIn"] = ["checkIn is required (yyyy-MM-dd)."];
        }

        if (dto.CheckOut is null)
        {
            errors["checkOut"] = ["checkOut is required (yyyy-MM-dd)."];
        }

        if (dto.Rooms is null || dto.Rooms.Any(static r => r is null || r.Adults is null))
        {
            errors["rooms"] = ["rooms is required and every room needs an adults count."];
        }

        var suppliers = ParseSuppliers(dto.Suppliers, errors);
        ValidateOptional(dto, errors);

        if (errors.Count > 0)
        {
            return false;
        }

        var request = new HotelSearchRequest
        {
            Destination = dto.Destination ?? string.Empty,
            CheckIn = dto.CheckIn!.Value,
            CheckOut = dto.CheckOut!.Value,
            Rooms = dto.Rooms!.Select(static r => new Occupancy(r.Adults!.Value, r.ChildAges ?? [])).ToArray(),
            Nationality = dto.Nationality ?? string.Empty,
            Currency = dto.Currency ?? string.Empty,
            MaxResults = dto.MaxResults,
            Suppliers = suppliers,
        };

        foreach (var (field, messages) in request.Validate())
        {
            errors[JsonNamingPolicy.CamelCase.ConvertName(field)] = messages;
        }

        var merge = defaults.ToMergeOptions();
        if (dto.Options is { } o)
        {
            merge = merge with
            {
                UseMapping = o.UseMapping ?? merge.UseMapping,
                MaxOffersPerHotel = o.MaxOffersPerHotel ?? merge.MaxOffersPerHotel,
                PriceChangeThreshold = o.PriceChangeThreshold ?? merge.PriceChangeThreshold,
            };

            var mergeErrors = merge.Validate().ToArray();
            if (mergeErrors.Length > 0)
            {
                errors["options"] = mergeErrors;
            }
        }

        if (errors.Count > 0)
        {
            return false;
        }

        mapped = new MappedSearch(
            request,
            new SearchRunOptions(
                Merge: merge,
                Deadline: dto.WaitMs is { } wait ? TimeSpan.FromMilliseconds(wait) : null,
                SearchId: dto.SearchId,
                Replaces: dto.Replaces));
        return true;
    }

    private static List<SupplierId>? ParseSuppliers(List<string>? raw, Dictionary<string, string[]> errors)
    {
        if (raw is null)
        {
            return null;
        }

        var parsed = new List<SupplierId>(raw.Count);
        foreach (var value in raw)
        {
            if (SupplierId.TryParse(value, out var id))
            {
                parsed.Add(id);
            }
            else
            {
                errors["suppliers"] = [$"'{value}' is not a valid supplier id."];
            }
        }

        return parsed;
    }

    private static void ValidateOptional(SearchRequestDto dto, Dictionary<string, string[]> errors)
    {
        if (dto.WaitMs is < MinWaitMs or > MaxWaitMs)
        {
            errors["waitMs"] = [$"waitMs must be between {MinWaitMs} and {MaxWaitMs}."];
        }

        if (dto.SearchId is not null && !SearchRunRegistry.IsValidRunId(dto.SearchId))
        {
            errors["searchId"] = ["searchId may use letters, digits, '-' and '_' and be at most 64 characters."];
        }

        if (dto.Replaces is not null && !SearchRunRegistry.IsValidRunId(dto.Replaces))
        {
            errors["replaces"] = ["replaces must be a valid search id."];
        }
    }
}

public sealed record SupplierInfo(
    string Id,
    SearchStyle SearchStyle,
    string[] Capabilities,
    int DefaultTimeoutMs,
    int MaxConcurrency,
    bool EnabledForTenant,
    SupplierCircuitState Circuit,
    int InFlight);

public sealed record AddIdentityDto(string? Supplier, string? SupplierCode, string? CanonicalId);

public sealed record IdentityResponse(string CanonicalId, string Status);

public sealed record SetAliasDto(string? RetiredId, string? SurvivorId);

public sealed record SourceDto(string Supplier, string Code);

public sealed record CanonicalHotelResponse(string RequestedId, string CanonicalId, List<SourceDto> Sources, IReadOnlyList<string> RetiredIds);
