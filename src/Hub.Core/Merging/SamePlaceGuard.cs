using System.Diagnostics.Metrics;
using Hub.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hub.Core;

public sealed class SamePlaceOptions
{
    public const string SectionName = "Hub:SamePlace";

    /// <summary>An overlay whose coordinates are further than this from the hotel's own is a different place.</summary>
    public double MaxDistanceMeters { get; set; } = 300;

    /// <summary>The least name similarity (0 to 1) an overlay needs. Localised names differ in wording, so this is low; the coordinates carry the weight.</summary>
    public double MinNameSimilarity { get; set; } = 0.5;

    /// <summary>When either side has no coordinates the distance cannot be checked, so the names alone must be this close.</summary>
    public double NameOnlyMinSimilarity { get; set; } = 0.85;
}

internal sealed class SamePlaceOptionsValidator : IValidateOptions<SamePlaceOptions>
{
    public ValidateOptionsResult Validate(string? name, SamePlaceOptions options) =>
        options.MaxDistanceMeters > 0
        && options.MinNameSimilarity is >= 0 and <= 1
        && options.NameOnlyMinSimilarity is >= 0 and <= 1
        && options.NameOnlyMinSimilarity >= options.MinNameSimilarity
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("SamePlace: MaxDistanceMeters must be positive, similarities between 0 and 1, and NameOnlyMinSimilarity at least MinNameSimilarity.");
}

public sealed record SamePlaceVerdict(bool SamePlace, string Reason, double? DistanceMeters, double NameSimilarity);

public interface ISamePlaceGuard
{
    /// <summary>Decides whether a supplier's localised name and address describe the hotel the hub already knows.</summary>
    SamePlaceVerdict Evaluate(string hotelName, GeoPoint? hotelLocation, LocalizedOverlay overlay);

    /// <summary>Counts and logs an overlay that was kept out.</summary>
    void RecordRejection(SupplierId supplier, string hotelName, SamePlaceVerdict verdict);
}

/// <summary>
/// A localised overlay may replace a hotel's name and address only if it is the same place: close on the map and similar
/// in name. Otherwise the supplier's data belongs to some other hotel, and applying it would rename the wrong one.
/// </summary>
public sealed class SamePlaceGuard : ISamePlaceGuard
{
    private static readonly IReadOnlySet<string> HotelFiller = new HashSet<string>(NameSimilarity.DefaultFiller, StringComparer.Ordinal)
    {
        "hotel", "hotels", "resort", "inn", "suites", "and",
    };

    public const string MeterName = "Hub.Core.Overlays";

    private readonly SamePlaceOptions _options;
    private readonly ILogger<SamePlaceGuard> _logger;
    private readonly Counter<long> _rejected;

    public SamePlaceGuard(IOptions<SamePlaceOptions> options, IMeterFactory meters, ILogger<SamePlaceGuard> logger)
    {
        _options = options.Value;
        _logger = logger;
        _rejected = meters.Create(MeterName).CreateCounter<long>("hub.overlay.rejected", description: "Localised overlays that were not applied because they describe a different place.");
    }

    public SamePlaceVerdict Evaluate(string hotelName, GeoPoint? hotelLocation, LocalizedOverlay overlay)
    {
        var similarity = NameSimilarity.Similarity(hotelName, overlay.Name, HotelFiller);
        if (hotelLocation is { } own && overlay.Location is { } other)
        {
            var distance = DistanceMeters(own, other);
            if (distance > _options.MaxDistanceMeters)
            {
                return new SamePlaceVerdict(false, $"The overlay is {distance:0} m from the hotel (limit {_options.MaxDistanceMeters:0} m).", distance, similarity);
            }

            return similarity >= _options.MinNameSimilarity
                ? new SamePlaceVerdict(true, "Same place.", distance, similarity)
                : new SamePlaceVerdict(false, $"The names are too different ({similarity:0.00}, needs {_options.MinNameSimilarity:0.00}).", distance, similarity);
        }

        return similarity >= _options.NameOnlyMinSimilarity
            ? new SamePlaceVerdict(true, "Same place by name; coordinates were not available to check.", null, similarity)
            : new SamePlaceVerdict(false, $"Coordinates are missing, so the names alone must match ({similarity:0.00}, needs {_options.NameOnlyMinSimilarity:0.00}).", null, similarity);
    }

    public void RecordRejection(SupplierId supplier, string hotelName, SamePlaceVerdict verdict)
    {
        _rejected.Add(1, new KeyValuePair<string, object?>("supplier", supplier.Value));
        _logger.LogWarning("Localised overlay from {Supplier} for '{Hotel}' was not applied: {Reason}", supplier, hotelName, verdict.Reason);
    }

    /// <summary>Great-circle distance in metres.</summary>
    public static double DistanceMeters(GeoPoint a, GeoPoint b)
    {
        const double EarthRadius = 6_371_000;
        static double Rad(double degrees) => degrees * Math.PI / 180;
        var dLat = Rad(b.Latitude - a.Latitude);
        var dLon = Rad(b.Longitude - a.Longitude);
        var h = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
                + (Math.Cos(Rad(a.Latitude)) * Math.Cos(Rad(b.Latitude)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return 2 * EarthRadius * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}
