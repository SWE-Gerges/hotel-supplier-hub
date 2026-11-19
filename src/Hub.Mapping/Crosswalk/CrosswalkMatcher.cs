using Hub.Abstractions;
using Microsoft.Extensions.Options;

namespace Hub.Mapping;

public sealed class CanonicalCityConfig
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The canonical (ISO-style) country code, a key of <see cref="CrosswalkOptions.Countries"/>.</summary>
    public string Country { get; set; } = string.Empty;

    public string? Region { get; set; }
}

public sealed class CrosswalkOptions
{
    public const string SectionName = "Hub:Crosswalk";

    /// <summary>Match every supplier's cities when the host starts (idempotent), so searches can resolve city codes at once.</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>A match is accepted automatically only at or above this score...</summary>
    public double AcceptThreshold { get; set; } = 0.9;

    /// <summary>...and only when it beats the runner-up by at least this much.</summary>
    public double MinMargin { get; set; } = 0.1;

    /// <summary>Added to the name score when the supplier's region agrees with the canonical city's.</summary>
    public double RegionBonus { get; set; } = 0.05;

    /// <summary>Canonical country code to the codes and names suppliers use for it. The canonical code itself always counts.</summary>
    public Dictionary<string, List<string>> Countries { get; set; } = new(StringComparer.Ordinal);

    public List<CanonicalCityConfig> CanonicalCities { get; set; } = [];
}

internal sealed class CrosswalkOptionsValidator : IValidateOptions<CrosswalkOptions>
{
    public ValidateOptionsResult Validate(string? name, CrosswalkOptions options)
    {
        var failures = new List<string>();
        if (options.AcceptThreshold is <= 0 or > 1)
        {
            failures.Add("AcceptThreshold must be above 0 and at most 1.");
        }

        if (options.MinMargin is < 0 or > 1 || options.RegionBonus is < 0 or > 0.5)
        {
            failures.Add("MinMargin must be between 0 and 1 and RegionBonus between 0 and 0.5.");
        }

        foreach (var city in options.CanonicalCities)
        {
            if (string.IsNullOrWhiteSpace(city.Id) || string.IsNullOrWhiteSpace(city.Name))
            {
                failures.Add("Every canonical city needs an id and a name.");
            }
            else if (!options.Countries.ContainsKey(city.Country))
            {
                failures.Add($"Canonical city '{city.Id}' names country '{city.Country}', which is not in Countries.");
            }
        }

        if (options.CanonicalCities.Select(static c => c.Id).Distinct(StringComparer.Ordinal).Count() != options.CanonicalCities.Count)
        {
            failures.Add("Canonical city ids must be unique.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public sealed record CanonicalCity(string Id, string Name, string Country, string? Region);

public sealed record CityCandidate(string CanonicalCityId, double Score);

public enum MatchDecision
{
    /// <summary>Good and clearly better than anything else: the code can be used without a person looking at it.</summary>
    AutoAccept,

    /// <summary>A person decides.</summary>
    Review,
}

public sealed record CityMatch(
    MatchDecision Decision, string? BestCityId, double BestScore, double RunnerUpScore, string Reason, IReadOnlyList<CityCandidate> Candidates);

/// <summary>City names are read with the generic rules plus the filler words that say nothing about which city it is.</summary>
public static class CityNames
{
    private static readonly IReadOnlySet<string> Filler = new HashSet<string>(NameSimilarity.DefaultFiller, StringComparer.Ordinal) { "city", "town" };

    public static string[] Tokens(string name) => NameSimilarity.Tokens(name, Filler);

    public static string Normalize(string name) => NameSimilarity.Normalize(name, Filler);

    public static double EditSimilarity(string a, string b) => NameSimilarity.EditSimilarity(a, b);

    public static double TokenSetSimilarity(string[] a, string[] b) => NameSimilarity.TokenSetSimilarity(a, b);

    public static double Similarity(string left, string right) => NameSimilarity.Similarity(left, right, Filler);
}

/// <summary>
/// Maps a supplier's city to a canonical city. The country gate comes first: only canonical cities of the supplier's
/// country are candidates. Candidates are scored on the name (words and edits) with a small bonus for a matching region.
/// A match is accepted automatically only when it is good enough and clearly better than the runner-up; everything else
/// is left to a person.
/// </summary>
public sealed class CrosswalkMatcher
{
    private readonly CrosswalkOptions _options;
    private readonly Dictionary<string, string> _countryByAlias = new(StringComparer.Ordinal);

    public CrosswalkMatcher(IOptions<CrosswalkOptions> options)
    {
        _options = options.Value;
        foreach (var (iso, aliases) in _options.Countries)
        {
            foreach (var alias in aliases.Append(iso))
            {
                _countryByAlias[CityNames.Normalize(alias)] = iso;
            }
        }
    }

    public double AcceptThreshold => _options.AcceptThreshold;

    public IReadOnlyList<CanonicalCity> CanonicalCities => _options.CanonicalCities.Select(static c => new CanonicalCity(c.Id, c.Name, c.Country, c.Region)).ToArray();

    /// <summary>The canonical country code for a supplier's country code or name; null when neither is known or they disagree.</summary>
    public string? ResolveCountry(string supplierCode, string? supplierName)
    {
        var byCode = _countryByAlias.GetValueOrDefault(CityNames.Normalize(supplierCode));
        var byName = supplierName is null ? null : _countryByAlias.GetValueOrDefault(CityNames.Normalize(supplierName));
        return byCode is not null && byName is not null && byCode != byName ? null : byCode ?? byName;
    }

    public CityMatch Match(SupplierCity city, string? supplierCountryName)
    {
        if (ResolveCountry(city.CountryCode, supplierCountryName) is not { } country)
        {
            return Review($"The country '{city.CountryCode}' is not in the alias table, so no city can be proposed.", []);
        }

        var candidates = CanonicalCities
            .Where(c => c.Country == country)
            .Select(c => new CityCandidate(c.Id, Math.Min(1.0, Score(city, c))))
            .OrderByDescending(static c => c.Score)
            .ThenBy(static c => c.CanonicalCityId, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
        {
            return Review($"No canonical city exists in country {country}.", []);
        }

        var best = candidates[0];
        var runnerUp = candidates.Count > 1 ? candidates[1].Score : 0.0;
        if (best.Score < _options.AcceptThreshold)
        {
            return new CityMatch(MatchDecision.Review, best.CanonicalCityId, best.Score, runnerUp, $"The best score {best.Score:0.00} is below {_options.AcceptThreshold:0.00}.", candidates);
        }

        if (best.Score - runnerUp < _options.MinMargin)
        {
            return new CityMatch(
                MatchDecision.Review, best.CanonicalCityId, best.Score, runnerUp,
                $"The best score {best.Score:0.00} is too close to the runner-up {runnerUp:0.00} (needs a margin of {_options.MinMargin:0.00}).", candidates);
        }

        return new CityMatch(MatchDecision.AutoAccept, best.CanonicalCityId, best.Score, runnerUp, "Accepted automatically.", candidates);
    }

    private double Score(SupplierCity city, CanonicalCity canonical)
    {
        var score = CityNames.Similarity(city.Name, canonical.Name);
        if (city.Region is { Length: > 0 } && canonical.Region is { Length: > 0 } && RegionsAgree(city.Region, canonical.Region))
        {
            score += _options.RegionBonus;
        }

        return score;
    }

    private static bool RegionsAgree(string left, string right)
    {
        var a = CityNames.Tokens(left);
        var b = CityNames.Tokens(right);
        return a.All(b.Contains) || b.All(a.Contains);
    }

    private static CityMatch Review(string reason, IReadOnlyList<CityCandidate> candidates) =>
        new(MatchDecision.Review, null, 0, 0, reason, candidates);
}
