using Hub.Abstractions;
using Hub.Mapping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Hub.Tests;

internal static class CrosswalkKit
{
    /// <summary>The matcher with the canonical cities and country aliases the API ships with.</summary>
    public static CrosswalkMatcher Shipped() => new(Options.Create(ShippedOptions()));

    public static CrosswalkOptions ShippedOptions()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.api.json")).Build();
        return configuration.GetSection(CrosswalkOptions.SectionName).Get<CrosswalkOptions>()!;
    }

    public static CrosswalkMatcher Custom(Action<CrosswalkOptions> change, params CanonicalCityConfig[] cities)
    {
        var options = new CrosswalkOptions { Countries = new() { ["XA"] = ["Xaria"] }, CanonicalCities = [.. cities] };
        change(options);
        return new CrosswalkMatcher(Options.Create(options));
    }

    public static CanonicalCityConfig City(string id, string name, string country = "XA", string? region = null) =>
        new() { Id = id, Name = name, Country = country, Region = region };
}

public class CityNamesTests
{
    [Theory]
    [InlineData("Aldermoor", "aldermoor")]
    [InlineData("  ALDERMOOR  ", "aldermoor")]
    [InlineData("Äldermoör", "aldermoor")]
    [InlineData("St. Orrin", "saint orrin")]
    [InlineData("Saint-Orrin", "saint orrin")]
    [InlineData("Mt Brightwater", "mount brightwater")]
    [InlineData("Cedarfall City", "cedarfall")]
    [InlineData("City of Cedarfall", "cedarfall")]
    [InlineData("O'Hara's Landing", "oharas landing")]
    public void Names_are_read_without_case_diacritics_punctuation_abbreviations_or_filler_words(string name, string expected)
    {
        CityNames.Normalize(name).ShouldBe(expected);
    }

    [Fact]
    public void A_name_made_only_of_filler_words_keeps_them()
    {
        CityNames.Normalize("The City").ShouldBe("the city");
    }

    [Theory]
    [InlineData("kitten", "kitten", 1.0)]
    [InlineData("", "", 1.0)]
    [InlineData("abc", "xyz", 0.0)]
    [InlineData("brightwater", "brightwatr", 0.909)]
    public void Edit_similarity_falls_with_the_number_of_edits(string a, string b, double expected)
    {
        CityNames.EditSimilarity(a, b).ShouldBe(expected, 0.001);
    }

    [Fact]
    public void Names_that_differ_only_in_spacing_are_equal_and_a_missing_word_costs_a_lot()
    {
        CityNames.Similarity("Cedar Fall", "Cedarfall").ShouldBe(1.0);
        CityNames.Similarity("Brightwater", "Brightwater Bay").ShouldBeLessThan(0.7);
        CityNames.Similarity("Aldermoor", "Brightwater").ShouldBeLessThan(0.3);
    }

    [Fact]
    public void Similarity_is_symmetric_and_stays_between_zero_and_one()
    {
        var random = new Random(11);
        var syllables = new[] { "al", "der", "moor", "bright", "water", "ce", "dar", "fall", "saint", "orr", "in", "bay", "heights" };
        string Name() => string.Join(' ', Enumerable.Range(0, random.Next(1, 4)).Select(_ => string.Concat(Enumerable.Range(0, random.Next(1, 4)).Select(_ => syllables[random.Next(syllables.Length)]))));

        for (var i = 0; i < 300; i++)
        {
            var a = Name();
            var b = Name();

            var forward = CityNames.Similarity(a, b);
            var backward = CityNames.Similarity(b, a);
            forward.ShouldBeInRange(0.0, 1.0);
            backward.ShouldBe(forward, 1e-9);
            CityNames.Similarity(a, a).ShouldBe(1.0);
        }
    }
}

public class CrosswalkMatcherTests
{
    private static readonly CrosswalkMatcher Matcher = CrosswalkKit.Shipped();

    private static CityMatch Match(string name, string country, string? region = null, string? countryName = null, string code = "X") =>
        Matcher.Match(new SupplierCity(code, name, country, region), countryName);

    [Theory]
    [InlineData("Aldermoor", "XA", "Northmark", "city-aldermoor")]
    [InlineData("ALDERMOOR", "XA", null, "city-aldermoor")]
    [InlineData("Äldermoor", "900", "Northmark Region", "city-aldermoor")]
    [InlineData("Aldermoor", "XAL", null, "city-aldermoor")]
    [InlineData("Brightwater", "XA", "Eastshore", "city-brightwater")]
    [InlineData("BRIGHTWATER", "900", null, "city-brightwater")]
    [InlineData("Cedar Fall", "900", "Highland Region", "city-cedarfall")]
    [InlineData("Cedarfall City", "XA", null, "city-cedarfall")]
    [InlineData("St. Orrin", "900", null, "city-saint-orrin")]
    [InlineData("Saint-Orrin", "XAL", null, "city-saint-orrin")]
    [InlineData("SAINT ORRIN", "XA", null, "city-saint-orrin")]
    [InlineData("Brightwater Bay", "XA", null, "city-brightwater-bay")]
    [InlineData("Aldermoor Heights", "XA", null, "city-aldermoor-heights")]
    public void Spelling_variants_of_a_city_are_accepted_for_the_right_canonical_city(string name, string country, string? region, string expected)
    {
        var match = Match(name, country, region);

        match.Decision.ShouldBe(MatchDecision.AutoAccept, match.Reason);
        match.BestCityId.ShouldBe(expected);
        match.BestScore.ShouldBeGreaterThanOrEqualTo(0.9);
    }

    [Theory]
    [InlineData("XB")]
    [InlineData("901")]
    [InlineData("XBL")]
    public void The_same_name_in_another_country_maps_to_that_countrys_city_never_to_the_first_one(string country)
    {
        var match = Match("Aldermoor", country);

        match.Decision.ShouldBe(MatchDecision.AutoAccept);
        match.BestCityId.ShouldBe("city-aldermoor-xb");
        match.Candidates.ShouldHaveSingleItem("the country gate leaves only the cities of that country");
    }

    [Fact]
    public void A_country_the_alias_table_does_not_know_sends_the_city_to_review_with_no_proposal()
    {
        var match = Match("Aldermoor", "ZZZ");

        match.Decision.ShouldBe(MatchDecision.Review);
        match.BestCityId.ShouldBeNull();
        match.Reason.ShouldContain("alias table");
    }

    [Fact]
    public void A_country_code_and_name_that_point_at_different_countries_are_not_trusted()
    {
        Matcher.ResolveCountry("XA", "Xbaland").ShouldBeNull();
        Matcher.ResolveCountry("XA", "Xaria").ShouldBe("XA");
        Matcher.ResolveCountry("900", null).ShouldBe("XA");
        Matcher.ResolveCountry("???", "Xbaland").ShouldBe("XB");
    }

    [Theory]
    [InlineData("Nowhereville", "XA")]
    [InlineData("Brightwatr Cove", "XA")]
    [InlineData("Cedarfal", "XA")]
    [InlineData("Aldermoor Harbour", "XA")]
    public void Names_that_are_not_clearly_a_canonical_city_go_to_review_with_the_best_guess(string name, string country)
    {
        var match = Match(name, country);

        match.Decision.ShouldBe(MatchDecision.Review);
        match.Reason.ShouldContain("below");
    }

    [Fact]
    public void A_one_letter_typo_with_a_matching_region_is_good_enough_but_without_one_it_is_not()
    {
        Match("Cedarfal", "XA", "Highland").Decision.ShouldBe(MatchDecision.AutoAccept);
        Match("Cedarfal", "XA").Decision.ShouldBe(MatchDecision.Review);
    }

    [Fact]
    public void Near_identical_names_for_different_cities_are_never_accepted_on_their_own()
    {
        var matcher = CrosswalkKit.Custom(
            static _ => { },
            CrosswalkKit.City("city-marlow-cross", "Marlow Cross"),
            CrosswalkKit.City("city-marlow-crose", "Marlow Crose"));

        foreach (var name in new[] { "Marlow Cross", "Marlow Crosse" })
        {
            var match = matcher.Match(new SupplierCity("MC", name, "XA", null), null);

            match.Decision.ShouldBe(MatchDecision.Review, name);
            match.Reason.ShouldContain("too close");
            match.Candidates.Select(static c => c.CanonicalCityId).ShouldBe(["city-marlow-cross", "city-marlow-crose"], ignoreOrder: true);
        }
    }

    [Fact]
    public void A_clearly_different_neighbour_does_not_block_an_exact_match()
    {
        var matcher = CrosswalkKit.Custom(
            static _ => { },
            CrosswalkKit.City("city-marlow-cross", "Marlow Cross"),
            CrosswalkKit.City("city-marlow-crossing", "Marlow Crossing"));

        var match = matcher.Match(new SupplierCity("MC", "Marlow Cross", "XA", null), null);

        match.Decision.ShouldBe(MatchDecision.AutoAccept);
        match.BestCityId.ShouldBe("city-marlow-cross");
    }

    [Fact]
    public void A_decoy_in_the_same_country_does_not_shrink_the_margin_of_a_clear_match()
    {
        var match = Match("Brightwater", "XA", "Eastshore");

        match.Decision.ShouldBe(MatchDecision.AutoAccept);
        (match.BestScore - match.RunnerUpScore).ShouldBeGreaterThan(0.3, "Brightwater Bay is a different city and scores well below Brightwater");
    }

    [Fact]
    public void The_thresholds_come_from_options()
    {
        var strict = CrosswalkKit.Custom(static o => o.AcceptThreshold = 0.99, CrosswalkKit.City("c", "Cedarfall"));
        var loose = CrosswalkKit.Custom(static o => o.AcceptThreshold = 0.5, CrosswalkKit.City("c", "Cedarfall"));

        var city = new SupplierCity("X", "Cedarfal", "XA", null);

        strict.Match(city, null).Decision.ShouldBe(MatchDecision.Review);
        loose.Match(city, null).Decision.ShouldBe(MatchDecision.AutoAccept);
    }

    [Fact]
    public void A_country_with_no_canonical_city_has_nothing_to_propose()
    {
        var matcher = CrosswalkKit.Custom(static o => o.Countries["XB"] = ["Xbaland"], CrosswalkKit.City("c", "Cedarfall"));

        var match = matcher.Match(new SupplierCity("X", "Cedarfall", "XB", null), null);

        match.Decision.ShouldBe(MatchDecision.Review);
        match.Reason.ShouldContain("No canonical city");
    }

    [Fact]
    public void Invalid_crosswalk_settings_stop_startup()
    {
        var validator = new CrosswalkOptionsValidator();

        validator.Validate(null, new CrosswalkOptions { AcceptThreshold = 1.5 }).Failed.ShouldBeTrue();
        validator.Validate(null, new CrosswalkOptions { CanonicalCities = [CrosswalkKit.City("c", "Name", "XQ")] }).Failed.ShouldBeTrue("the city names a country that is not in Countries");
        validator.Validate(null, new CrosswalkOptions { Countries = new() { ["XA"] = [] }, CanonicalCities = [CrosswalkKit.City("c", "A"), CrosswalkKit.City("c", "B")] }).Failed.ShouldBeTrue("ids repeat");
        validator.Validate(null, CrosswalkKit.ShippedOptions()).Succeeded.ShouldBeTrue();
    }
}
