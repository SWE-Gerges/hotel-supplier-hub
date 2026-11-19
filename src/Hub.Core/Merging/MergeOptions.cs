namespace Hub.Core;

/// <summary>
/// Settings for one search run. They are passed into each run and never read from global state, so two
/// concurrent runs with different settings cannot affect each other.
/// </summary>
/// <param name="UseMapping">When false, hotels are not matched across suppliers and each supplier hotel stays separate.</param>
/// <param name="MaxOffersPerHotel">Cheapest offers kept per merged hotel.</param>
/// <param name="PriceChangeThreshold">
/// Smallest movement of a hotel's cheapest price that is worth a <see cref="HotelUpdated"/> event on its own.
/// Updates that add offers or sources are always reported.
/// </param>
public sealed record MergeOptions(bool UseMapping = true, int MaxOffersPerHotel = 25, decimal PriceChangeThreshold = 0m)
{
    public IEnumerable<string> Validate()
    {
        if (MaxOffersPerHotel < 1)
        {
            yield return "MaxOffersPerHotel must be at least 1.";
        }

        if (PriceChangeThreshold < 0)
        {
            yield return "PriceChangeThreshold cannot be negative.";
        }
    }
}
