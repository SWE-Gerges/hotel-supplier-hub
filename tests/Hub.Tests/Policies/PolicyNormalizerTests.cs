using System.Diagnostics.Metrics;
using Hub.Abstractions;
using Hub.Core;
using Hub.Suppliers.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Hub.Tests;

internal static class PolicyHarness
{
    public static readonly DateTimeOffset Now = new(2027, 1, 10, 9, 0, 0, TimeSpan.Zero);
    public static readonly DateRange Stay = new(new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 4));
    public static readonly DateTimeOffset CheckIn = new(2027, 3, 1, 0, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset Day(int month, int day) => new(2027, month, day, 0, 0, 0, TimeSpan.Zero);

    public static CancellationPolicyNormalizer Create(ILogger<CancellationPolicyNormalizer>? logger = null, IMeterFactory? meters = null, params IPolicyRuleSource[] sources) =>
        new(
            new PolicyRuleBook(sources.Length == 0 ? [new EmbeddedPolicyRuleSource()] : sources),
            meters ?? new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>(),
            logger ?? NullLogger<CancellationPolicyNormalizer>.Instance);

    public static RoomOffer Offer(SupplierId supplier, params (string Key, string Value)[] extra) => new()
    {
        OfferId = "offer-1",
        Supplier = supplier,
        SupplierHotelCode = "h1",
        RoomName = "Room",
        Board = BoardType.RoomOnly,
        RawBoard = "RO",
        Refundable = false,
        Price = new Money(300m, "EUR"),
        SellingPrice = new Money(330m, "EUR"),
        Extra = extra.ToDictionary(static e => e.Key, static e => e.Value),
    };

    public static (DateTimeOffset From, PenaltyKind Kind, decimal Value) Tier(DateTimeOffset from, PenaltyKind kind, decimal value) => (from, kind, value);

    public static IReadOnlyList<(DateTimeOffset, PenaltyKind, decimal)> Shape(CancellationPolicy policy) =>
        policy.Tiers.Select(static t => (t.From, t.Penalty.Kind, t.Penalty.Value)).ToArray();
}

public class PolicyGoldenTests
{
    private static readonly SupplierId A = TestData.A;
    private static readonly SupplierId B = TestData.B;
    private static readonly SupplierId C = TestData.C;

    private static readonly CancellationPolicyNormalizer Normalizer = PolicyHarness.Create();

    private static CancellationPolicy FromA(string text) =>
        Normalizer.Normalize(PolicyHarness.Offer(A, ("cancellationText", text)), PolicyHarness.Stay, PolicyHarness.Now);

    private static readonly (DateTimeOffset, PenaltyKind, decimal) Free = (PolicyHarness.Now, PenaltyKind.Percent, 0m);
    private static readonly (DateTimeOffset, PenaltyKind, decimal) NoShow = (PolicyHarness.CheckIn, PenaltyKind.Percent, 100m);

    private static readonly (string Name, string Text)[] FirstNightPhrasings =
    [
        ("plain", "Free cancellation until 2027-02-27. Afterwards the first night is charged."),
        ("slash dates", "Cancel free of charge before 27/02/2027; after that one night will be charged."),
        ("short month", "Free cancellation up to 27 Feb 2027 - thereafter 1 night penalty."),
        ("charge after date", "Cancellations made after 2027-02-27 incur a charge of 1 night."),
        ("messy whitespace", "  Free   cancellation until 2027-02-27.   Afterwards the first night is charged.  "),
        ("shouting", "FREE CANCELLATION UNTIL 27.02.2027. AFTERWARDS THE FIRST NIGHT IS CHARGED."),
        ("long month with ordinal", "Free cancellation until February 27th, 2027. After that, the first night is charged."),
        ("month first", "Free of charge cancellation by Feb 27, 2027, then one night is charged."),
        ("trailing text", "Free cancellation until 2027-02-27. Afterwards the first night is charged. Taxes are not refundable."),
    ];

    public static TheoryData<string, string> FirstNightAfterFeb27
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (name, text) in FirstNightPhrasings)
            {
                data.Add(name, text);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FirstNightAfterFeb27))]
    public void Supplier_a_phrasings_that_mean_one_night_after_the_free_window(string _, string text)
    {
        var policy = FromA(text);

        policy.Known.ShouldBeTrue();
        policy.Refundable.ShouldBeTrue();
        policy.RefundableUntil.ShouldBe(PolicyHarness.Day(2, 27));
        PolicyHarness.Shape(policy).ShouldBe([Free, (PolicyHarness.Day(2, 27), PenaltyKind.Nights, 1m), NoShow]);
    }

    [Fact]
    public void A_percentage_after_the_free_window_is_read_whatever_the_wording()
    {
        var shouting = FromA("FREE CANCELLATION UNTIL 27.02.2027, THEN 50% OF THE TOTAL");
        var words = FromA("Free cancellation before 2027-02-27. Afterwards 50 per cent is charged.");
        var after = FromA("Cancellations made after 2027-02-27 incur a charge of 50%.");

        foreach (var policy in new[] { shouting, words, after })
        {
            PolicyHarness.Shape(policy).ShouldBe([Free, (PolicyHarness.Day(2, 27), PenaltyKind.Percent, 50m), NoShow]);
            policy.RefundableUntil.ShouldBe(PolicyHarness.Day(2, 27));
        }
    }

    [Fact]
    public void Several_nights_and_staged_percentages_give_several_tiers()
    {
        PolicyHarness.Shape(FromA("Free cancellation until 2027-02-27. Afterwards 2 nights are charged."))
            .ShouldBe([Free, (PolicyHarness.Day(2, 27), PenaltyKind.Nights, 2m), NoShow]);

        var staged = FromA("Free cancellation until 2027-02-20. 50% of the total is charged until 2027-02-27, then 100%");

        PolicyHarness.Shape(staged).ShouldBe([Free, (PolicyHarness.Day(2, 20), PenaltyKind.Percent, 50m), (PolicyHarness.Day(2, 27), PenaltyKind.Percent, 100m)]);
        staged.RefundableUntil.ShouldBe(PolicyHarness.Day(2, 20));
    }

    [Theory]
    [InlineData("Non-refundable. The full amount is charged on cancellation.")]
    [InlineData("This rate cannot be cancelled or changed.")]
    [InlineData("No free cancellation - 100% penalty applies.")]
    [InlineData("NON REFUNDABLE")]
    public void Supplier_a_non_refundable_phrasings_are_known_and_charge_everything_from_now(string text)
    {
        var policy = FromA(text);

        policy.Known.ShouldBeTrue();
        policy.Refundable.ShouldBeFalse();
        policy.RefundableUntil.ShouldBeNull();
        PolicyHarness.Shape(policy).ShouldBe([(PolicyHarness.Now, PenaltyKind.Percent, 100m)]);
    }

    [Fact]
    public void A_free_window_that_has_already_closed_leaves_no_free_cancellation()
    {
        var policy = FromA("Free cancellation until 2027-01-05. Afterwards the first night is charged.");

        policy.Known.ShouldBeTrue();
        policy.Refundable.ShouldBeFalse("the date has passed, so nothing is free any more");
        policy.RefundableUntil.ShouldBeNull();
        PolicyHarness.Shape(policy).ShouldBe([(PolicyHarness.Now, PenaltyKind.Nights, 1m), NoShow]);
    }

    [Fact]
    public void A_free_window_that_runs_past_check_in_is_free_until_the_stay()
    {
        var policy = FromA("Free cancellation until 2027-03-15. Afterwards the first night is charged.");

        policy.Refundable.ShouldBeTrue();
        policy.RefundableUntil.ShouldBe(PolicyHarness.CheckIn);
        PolicyHarness.Shape(policy).ShouldBe([Free, NoShow]);
    }

    [Theory]
    [InlineData("Contact the hotel for cancellation terms.")]
    [InlineData("Free cancellation until the day before arrival. Afterwards the first night is charged.")]
    [InlineData("Free cancellation until 31/02/2027. Afterwards the first night is charged.")]
    [InlineData("Free cancellation until 2027-02-20. 100% of the total is charged until 2027-02-27, then 50%")]
    [InlineData("Cancellation: see supplier.")]
    [InlineData("")]
    [InlineData("   ")]
    public void Text_that_cannot_be_understood_is_an_unknown_policy_never_a_refundable_one(string text)
    {
        var policy = FromA(text);

        policy.Known.ShouldBeFalse();
        policy.Refundable.ShouldBeFalse();
        policy.RefundableUntil.ShouldBeNull();
        policy.Tiers.ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_policy_is_unknown()
    {
        Normalizer.Normalize(PolicyHarness.Offer(A), PolicyHarness.Stay, PolicyHarness.Now).Known.ShouldBeFalse();
    }

    // ---- supplier-b: penalty codes ----

    [Theory]
    [InlineData("NIGHT1", PenaltyKind.Nights, 1)]
    [InlineData("NIGHT2", PenaltyKind.Nights, 2)]
    [InlineData("PCT50", PenaltyKind.Percent, 50)]
    public void Supplier_b_reads_the_penalty_code_and_the_date(string code, PenaltyKind kind, int value)
    {
        var offer = PolicyHarness.Offer(B, ("cancelUntil", "2027-02-27T00:00:00Z"), ("penaltyCode", code));

        var policy = Normalizer.Normalize(offer, PolicyHarness.Stay, PolicyHarness.Now);

        policy.Known.ShouldBeTrue();
        policy.RefundableUntil.ShouldBe(PolicyHarness.Day(2, 27));
        PolicyHarness.Shape(policy).ShouldBe([Free, (PolicyHarness.Day(2, 27), kind, (decimal)value), NoShow]);
    }

    [Fact]
    public void Supplier_b_without_a_date_is_non_refundable_and_an_unknown_code_is_unknown()
    {
        var noDate = Normalizer.Normalize(PolicyHarness.Offer(B, ("penaltyCode", "FULL")), PolicyHarness.Stay, PolicyHarness.Now);
        var weird = Normalizer.Normalize(PolicyHarness.Offer(B, ("cancelUntil", "2027-02-27T00:00:00Z"), ("penaltyCode", "WEIRD")), PolicyHarness.Stay, PolicyHarness.Now);
        var badDate = Normalizer.Normalize(PolicyHarness.Offer(B, ("cancelUntil", "soon"), ("penaltyCode", "NIGHT1")), PolicyHarness.Stay, PolicyHarness.Now);

        noDate.Known.ShouldBeTrue();
        noDate.Refundable.ShouldBeFalse();
        PolicyHarness.Shape(noDate).ShouldBe([(PolicyHarness.Now, PenaltyKind.Percent, 100m)]);
        weird.Known.ShouldBeFalse();
        badDate.Known.ShouldBeFalse();
    }

    // ---- supplier-c: structured tiers ----

    [Fact]
    public void Supplier_c_structured_tiers_become_canonical_tiers()
    {
        CancellationPolicy Read(string tiers) =>
            Normalizer.Normalize(PolicyHarness.Offer(C, ("policyTiers", tiers)), PolicyHarness.Stay, PolicyHarness.Now);

        PolicyHarness.Shape(Read("2027-02-27|nights|1")).ShouldBe([Free, (PolicyHarness.Day(2, 27), PenaltyKind.Nights, 1m), NoShow]);
        PolicyHarness.Shape(Read("NOW|percent|100")).ShouldBe([(PolicyHarness.Now, PenaltyKind.Percent, 100m)]);
        PolicyHarness.Shape(Read("2027-02-20|percent|50;2027-02-27|percent|100"))
            .ShouldBe([Free, (PolicyHarness.Day(2, 20), PenaltyKind.Percent, 50m), (PolicyHarness.Day(2, 27), PenaltyKind.Percent, 100m)]);
        PolicyHarness.Shape(Read("2027-02-27|fixed|45.50")).ShouldBe([Free, (PolicyHarness.Day(2, 27), PenaltyKind.Fixed, 45.50m), NoShow]);
        Read("NOW|percent|100").Refundable.ShouldBeFalse();
    }

    [Theory]
    [InlineData("2027-02-27|nights")]
    [InlineData("2027-02-27|weird|1")]
    [InlineData("2027-02-27|percent|150")]
    [InlineData("tomorrow|percent|50")]
    [InlineData("2027-02-27|percent|abc")]
    [InlineData("2027-02-20|percent|50;2027-02-27|percent|20")]
    [InlineData("")]
    public void Supplier_c_policies_that_are_malformed_or_get_cheaper_over_time_are_unknown(string tiers)
    {
        var policy = Normalizer.Normalize(PolicyHarness.Offer(C, ("policyTiers", tiers)), PolicyHarness.Stay, PolicyHarness.Now);

        policy.Known.ShouldBeFalse();
        policy.Refundable.ShouldBeFalse();
    }

    // ---- what every known policy must satisfy ----

    private static void AssertSensible(CancellationPolicy policy, decimal price = 300m)
    {
        policy.Known.ShouldBeTrue();
        policy.Tiers.ShouldNotBeEmpty();
        policy.Tiers[0].From.ShouldBe(PolicyHarness.Now, "the schedule starts when the policy is read");
        policy.Tiers.Select(static t => t.From).ShouldBeInOrder();
        policy.Tiers.Select(static t => t.From).Distinct().Count().ShouldBe(policy.Tiers.Count, "no two tiers start together");
        policy.Tiers[^1].From.ShouldBeLessThanOrEqualTo(PolicyHarness.CheckIn, "the schedule covers the stay");
        var percents = policy.Tiers.Select(t => CancellationPolicyNormalizer.PercentOf(t.Penalty, PolicyHarness.Stay.Nights, price)).ToList();
        percents.ShouldBeInOrder(SortDirection.Ascending, "penalties never get cheaper");
        if (policy.Tiers[^1].From < PolicyHarness.CheckIn)
        {
            percents[^1].ShouldBe(100m, "unless the supplier states its own penalty from check-in, the whole stay is charged at check-in");
        }
        policy.Refundable.ShouldBe(policy.RefundableUntil is not null);
        if (policy.RefundableUntil is { } until)
        {
            until.ShouldBeGreaterThan(PolicyHarness.Now);
            percents[0].ShouldBe(0m);
        }
    }

    [Fact]
    public void Every_known_golden_policy_is_monotone_starts_now_and_covers_the_stay()
    {
        var texts = FirstNightPhrasings.Select(static p => p.Text).Concat(
        [
            "FREE CANCELLATION UNTIL 27.02.2027, THEN 50% OF THE TOTAL",
            "Free cancellation until 2027-02-20. 50% of the total is charged until 2027-02-27, then 100%",
            "Non-refundable. The full amount is charged on cancellation.",
            "Free cancellation until 2027-01-05. Afterwards the first night is charged.",
            "Free cancellation until 2027-03-15. Afterwards the first night is charged.",
        ]);

        foreach (var text in texts)
        {
            AssertSensible(FromA(text));
        }

        foreach (var code in new[] { "NIGHT1", "NIGHT2", "PCT50", "FULL" })
        {
            AssertSensible(Normalizer.Normalize(PolicyHarness.Offer(B, ("cancelUntil", "2027-02-27T00:00:00Z"), ("penaltyCode", code)), PolicyHarness.Stay, PolicyHarness.Now));
        }
    }

    [Fact]
    public void Random_structured_policies_are_either_unknown_or_sensible()
    {
        var random = new Random(2027);
        var known = 0;
        var unknown = 0;
        for (var i = 0; i < 300; i++)
        {
            var tiers = Enumerable.Range(0, random.Next(1, 4))
                .Select(_ => (Date: PolicyHarness.Now.AddDays(random.Next(-5, 60)), Kind: new[] { "nights", "percent", "fixed" }[random.Next(3)], Value: random.Next(1, 120)))
                .Select(static t => $"{t.Date:yyyy-MM-dd}|{t.Kind}|{t.Value}");
            var policy = Normalizer.Normalize(PolicyHarness.Offer(C, ("policyTiers", string.Join(';', tiers))), PolicyHarness.Stay, PolicyHarness.Now);

            if (policy.Known)
            {
                known++;
                AssertSensible(policy);
            }
            else
            {
                unknown++;
                policy.Refundable.ShouldBeFalse();
                policy.Tiers.ShouldBeEmpty();
            }
        }

        known.ShouldBeGreaterThan(20);
        unknown.ShouldBeGreaterThan(20);
    }

    // ---- fail safe ----

    [Fact]
    public void An_unknown_policy_is_non_refundable_counted_and_warned_about_once_per_supplier()
    {
        var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var logs = new ListLogger<CancellationPolicyNormalizer>();
        var normalizer = PolicyHarness.Create(logs, provider.GetRequiredService<IMeterFactory>());
        var counted = new List<(long Value, string? Supplier)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "hub.policy.unknown")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => counted.Add((value, tags.ToArray().Single(static t => t.Key == "supplier").Value?.ToString())));
        listener.Start();
        var offer = PolicyHarness.Offer(A, ("cancellationText", "Ask the front desk"));

        var applied = normalizer.Apply(offer with { Refundable = true, PolicyKnown = true }, PolicyHarness.Stay, PolicyHarness.Now);
        normalizer.Apply(offer, PolicyHarness.Stay, PolicyHarness.Now);

        applied.Refundable.ShouldBeFalse("whatever the supplier normaliser guessed, an unknown policy is never refundable");
        applied.PolicyKnown.ShouldBeFalse();
        applied.CancellationTiers.ShouldBeEmpty();
        applied.RefundableUntil.ShouldBeNull();
        counted.ShouldBe([(1L, "supplier-a"), (1L, "supplier-a")]);
        logs.Entries.Count(static e => e.Level == LogLevel.Warning).ShouldBe(1);
    }

    [Fact]
    public void A_supplier_without_rules_is_unknown()
    {
        var offer = PolicyHarness.Offer(SupplierId.Parse("supplier-z"), ("cancellationText", "Free cancellation until 2027-02-27."));

        PolicyHarness.Create().Normalize(offer, PolicyHarness.Stay, PolicyHarness.Now).Known.ShouldBeFalse();
    }

    [Fact]
    public void Apply_writes_the_canonical_fields_on_the_offer_and_keeps_the_raw_extras()
    {
        var offer = PolicyHarness.Offer(A, ("cancellationText", "Free cancellation until 2027-02-27. Afterwards the first night is charged."));

        var applied = Normalizer.Apply(offer, PolicyHarness.Stay, PolicyHarness.Now);

        applied.Refundable.ShouldBeTrue();
        applied.PolicyKnown.ShouldBeTrue();
        applied.RefundableUntil.ShouldBe(PolicyHarness.Day(2, 27));
        applied.CancellationTiers.Count.ShouldBe(3);
        applied.Extra["cancellationText"].ShouldBe(offer.Extra["cancellationText"]);
    }
}

public sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
