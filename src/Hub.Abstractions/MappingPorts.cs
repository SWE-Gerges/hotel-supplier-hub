namespace Hub.Abstractions;

/// <summary>Translates between supplier hotel codes and canonical hotel ids.</summary>
public interface ICanonicalIdResolver
{
    /// <summary>
    /// Resolves many supplier codes at once. Codes with no known identity are absent from the result.
    /// Retired canonical ids are followed to their survivor.
    /// </summary>
    ValueTask<IReadOnlyDictionary<string, string>> ResolveHotelsAsync(
        SupplierId supplier, IReadOnlyCollection<string> supplierCodes, CancellationToken cancellationToken);

    ValueTask<string?> ResolveHotelAsync(SupplierId supplier, string supplierCode, CancellationToken cancellationToken);

    /// <summary>The code to send to <paramref name="supplier"/> for a canonical hotel, or null if it has none.</summary>
    ValueTask<string?> GetSupplierCodeAsync(string canonicalId, SupplierId supplier, CancellationToken cancellationToken);
}

/// <summary>A country as one supplier lists it, under that supplier's own code.</summary>
public sealed record SupplierCountry(string Code, string Name);

/// <summary>A city as one supplier lists it: its own code, its spelling of the name, its country code and, sometimes, a region.</summary>
public sealed record SupplierCity(string Code, string Name, string CountryCode, string? Region);

public sealed record SupplierDirectory(IReadOnlyList<SupplierCountry> Countries, IReadOnlyList<SupplierCity> Cities);

/// <summary>The cities and countries a supplier can search, which the crosswalk matches to canonical cities.</summary>
public interface ICityDirectory
{
    ValueTask<Result<SupplierDirectory>> GetDirectoryAsync(CancellationToken cancellationToken);
}

/// <summary>Registers a supplier's city directory with the crosswalk.</summary>
public sealed record CityDirectoryRegistration(SupplierId Supplier, ICityDirectory Directory);

/// <summary>Translates a canonical city id into the codes a given supplier expects.</summary>
public interface ICityCodeResolver
{
    ValueTask<SupplierCityCodes?> ResolveAsync(string canonicalCityId, SupplierId supplier, CancellationToken cancellationToken);
}
