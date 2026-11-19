using Hub.Abstractions;
using Hub.Core;
using Hub.Suppliers.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Hub.Tests;

internal static class FakeSuppliersHarness
{
    public static ServiceProvider Build(TimeProvider? time = null, params (string Key, string Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Hub:FakeSuppliers:Seed"] = "7",
            ["Hub:FakeSuppliers:HotelsPerCity"] = "24",
            ["Hub:FakeSuppliers:SupplierA:PageSize"] = "5",
            ["Hub:FakeSuppliers:SupplierB:BatchSize"] = "4",
            ["Hub:FakeSuppliers:SupplierB:BatchDelay"] = "00:00:00",
            ["Hub:FakeSuppliers:SupplierB:PollInterval"] = "00:00:00.005",
            ["Hub:FakeSuppliers:SupplierC:RoomsBatchSize"] = "4",
        };
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(time ?? TimeProvider.System);
        services.AddFakeSuppliers(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }
}

/// <summary>
/// The behaviour every supplier must show, whatever its protocol. Each fake runs the same tests, which is what
/// keeps three very different wire formats behind one port.
/// </summary>
public abstract class HotelSearchContractTests : IDisposable
{
    protected HotelSearchContractTests()
    {
        Provider = FakeSuppliersHarness.Build();
        Search = Provider.GetRequiredKeyedService<IHotelSearch>(Id);
        Descriptor = Provider.GetRequiredKeyedService<ISupplierDescriptor>(Id);
        Faults = Provider.GetRequiredService<FakeSupplierFaults>().Get(Id)!;
    }

    protected abstract SupplierId Id { get; }

    protected abstract SupplierCityCodes Codes { get; }

    private ServiceProvider Provider { get; }

    protected IHotelSearch Search { get; }

    protected ISupplierDescriptor Descriptor { get; }

    protected FaultController Faults { get; }

    public void Dispose() => Provider.Dispose();

    private SupplierSearchQuery Query(string currency = "EUR", DateOnly? checkIn = null, DateOnly? checkOut = null) => new(
        new HotelSearchRequest
        {
            Destination = "city-aldermoor",
            CheckIn = checkIn ?? new DateOnly(2027, 3, 1),
            CheckOut = checkOut ?? new DateOnly(2027, 3, 4),
            Rooms = [new Occupancy(2)],
            Nationality = "XA",
            Currency = currency,
        },
        Codes);

    private async Task<List<Result<IReadOnlyList<Hotel>>>> RunAsync(SupplierSearchQuery? query = null) =>
        await Search.SearchAsync(query ?? Query(), CancellationToken.None).ToListAsync();

    private async Task<List<Hotel>> HotelsAsync(SupplierSearchQuery? query = null)
    {
        var batches = await RunAsync(query);
        batches.ShouldAllBe(static b => b.IsSuccess);
        return batches.SelectMany(static b => b.Value).ToList();
    }

    [Fact]
    public void Descriptor_identifies_the_supplier_and_its_search_capability()
    {
        Descriptor.SupplierId.ShouldBe(Id);
        Descriptor.Capabilities.HasFlag(SupplierCapabilities.Search).ShouldBeTrue();
        Descriptor.DefaultTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
        Descriptor.MaxConcurrency.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task The_raw_policy_the_supplier_sends_normalises_to_sensible_tiers_for_every_offer()
    {
        var normalizer = PolicyHarness.Create();
        var stay = new DateRange(new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 4));
        var offers = (await HotelsAsync()).SelectMany(static h => h.Offers).ToList();
        offers.ShouldNotBeEmpty();

        foreach (var offer in offers)
        {
            offer.Refundable.ShouldBeFalse("a supplier normaliser leaves the decision to the policy rules");
            var policy = normalizer.Normalize(offer, stay, PolicyHarness.Now);

            policy.Known.ShouldBeTrue($"{offer.OfferId} carried a policy the rules should understand");
            policy.Tiers[0].From.ShouldBe(PolicyHarness.Now);
            policy.Tiers[^1].From.ShouldBeLessThanOrEqualTo(PolicyHarness.CheckIn);
            policy.Tiers.Select(static t => t.From).ShouldBeInOrder();
            if (policy.Refundable)
            {
                policy.RefundableUntil.ShouldBe(PolicyHarness.Day(2, 27), "the fakes give free cancellation until two days before check-in");
            }
        }

        offers.Select(o => normalizer.Normalize(o, stay, PolicyHarness.Now).Refundable).Distinct().Count().ShouldBe(2, "the catalogue has refundable and non-refundable rates");
    }

    [Fact]
    public async Task Returns_canonical_hotels_with_priced_offers()
    {
        var hotels = await HotelsAsync();

        hotels.ShouldNotBeEmpty();
        foreach (var hotel in hotels)
        {
            hotel.Name.ShouldNotBeNullOrWhiteSpace();
            hotel.Stars.ShouldBeInRange(1, 5);
            hotel.Location.ShouldNotBeNull();
            hotel.CanonicalId.ShouldBeNull("suppliers know nothing about canonical ids");
            hotel.Sources.ShouldHaveSingleItem().Supplier.ShouldBe(Id);
            hotel.Offers.ShouldNotBeEmpty();
            hotel.Offers.ShouldAllBe(o => o.Supplier == Id
                                          && o.SupplierHotelCode == hotel.Sources[0].Code
                                          && o.Price.Amount > 0
                                          && o.Price.Currency == "EUR"
                                          && o.SellingPrice == o.Price
                                          && o.Board != BoardType.Unknown
                                          && !string.IsNullOrEmpty(o.RawBoard)
                                          && !string.IsNullOrWhiteSpace(o.RoomName));
        }

        var offerIds = hotels.SelectMany(static h => h.Offers).Select(static o => o.OfferId).ToList();
        offerIds.Distinct(StringComparer.Ordinal).Count().ShouldBe(offerIds.Count, "offer ids are unique");
        hotels.Select(static h => h.Sources[0].Code).Distinct(StringComparer.Ordinal).Count().ShouldBe(hotels.Count);
    }

    [Fact]
    public async Task Streams_more_than_one_batch_for_a_larger_catalogue()
    {
        var batches = await RunAsync();

        batches.Count.ShouldBeGreaterThan(1);
        batches.ShouldAllBe(static b => b.IsSuccess);
    }

    [Fact]
    public async Task Prices_in_the_requested_currency()
    {
        var hotels = await HotelsAsync(Query("USD"));

        hotels.ShouldNotBeEmpty();
        hotels.SelectMany(static h => h.Offers).ShouldAllBe(static o => o.Price.Currency == "USD");
    }

    [Fact]
    public async Task Gives_the_same_answer_for_the_same_request()
    {
        var first = await HotelsAsync();
        var second = await HotelsAsync();

        string Fingerprint(IEnumerable<Hotel> hotels) => string.Join(
            '|', hotels.OrderBy(static h => h.Sources[0].Code, StringComparer.Ordinal)
                .SelectMany(static h => h.Offers.Select(static o => $"{o.OfferId}={o.Price.Amount}")));

        Fingerprint(second).ShouldBe(Fingerprint(first));
    }

    [Fact]
    public async Task A_destination_the_supplier_does_not_know_yields_no_hotels_and_no_error()
    {
        var query = Query() with { Codes = new SupplierCityCodes("NOWHERE", "XX") };

        var batches = await RunAsync(query);

        batches.ShouldAllBe(static b => b.IsSuccess);
        batches.SelectMany(static b => b.Value).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_invalid_request_is_an_InvalidRequest_failure_and_not_an_exception()
    {
        var query = Query(checkIn: new DateOnly(2027, 3, 4), checkOut: new DateOnly(2027, 3, 1));

        var batches = await RunAsync(query);

        var failure = batches.ShouldHaveSingleItem();
        failure.IsFailure.ShouldBeTrue();
        failure.Error.Kind.ShouldBe(SupplierErrorKind.InvalidRequest);
        failure.Error.Retryable.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unsupported_currency_is_an_InvalidRequest_failure()
    {
        var batches = await RunAsync(Query("JPY"));

        batches.ShouldHaveSingleItem().Error.Kind.ShouldBe(SupplierErrorKind.InvalidRequest);
    }

    [Theory]
    [InlineData(SupplierErrorKind.Transient, true)]
    [InlineData(SupplierErrorKind.RateLimited, false)]
    [InlineData(SupplierErrorKind.Unavailable, false)]
    [InlineData(SupplierErrorKind.Auth, false)]
    [InlineData(SupplierErrorKind.InvalidRequest, false)]
    [InlineData(SupplierErrorKind.Timeout, false)]
    public async Task A_supplier_error_is_converted_to_a_failure_of_the_matching_kind(SupplierErrorKind kind, bool retryable)
    {
        Faults.Set(new FaultProfile { ErrorRate = 1, ErrorKind = kind });

        var batches = await RunAsync();

        var failure = batches.ShouldHaveSingleItem();
        failure.IsFailure.ShouldBeTrue();
        failure.Error.Kind.ShouldBe(kind);
        failure.Error.Retryable.ShouldBe(retryable);
        failure.Error.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_malformed_payload_is_an_Unknown_failure_and_not_an_exception()
    {
        Faults.Set(new FaultProfile { MalformedRate = 1 });

        var batches = await RunAsync();

        var failure = batches.ShouldHaveSingleItem();
        failure.Error.Kind.ShouldBe(SupplierErrorKind.Unknown);
        failure.Error.Retryable.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failure_ends_the_stream()
    {
        Faults.Set(new FaultProfile { ErrorRate = 1 });

        var batches = await RunAsync();

        batches.Count.ShouldBe(1);
        batches[0].IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Cancellation_stops_a_hung_supplier()
    {
        Faults.Set(new FaultProfile { HangRate = 1 });
        using var cts = new CancellationTokenSource();

        var run = Search.SearchAsync(Query(), cts.Token).ToListAsync(cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task An_already_cancelled_token_is_honoured_before_any_work_is_done()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Search.SearchAsync(Query(), cts.Token).ToListAsync(cts.Token));
    }

    [Fact]
    public async Task Cancelling_between_batches_stops_the_stream()
    {
        using var cts = new CancellationTokenSource();
        Faults.Set(new FaultProfile { MinLatencyMs = 5, MaxLatencyMs = 5 });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Search.SearchAsync(Query(), cts.Token))
            {
                await cts.CancelAsync();
            }
        });
    }
}

public sealed class SupplierAContractTests : HotelSearchContractTests
{
    protected override SupplierId Id => FakeSupplierIds.SupplierA;

    protected override SupplierCityCodes Codes => new("ALD", "XA");
}

public sealed class SupplierBContractTests : HotelSearchContractTests
{
    protected override SupplierId Id => FakeSupplierIds.SupplierB;

    protected override SupplierCityCodes Codes => new("5001", "900");
}

public sealed class SupplierCContractTests : HotelSearchContractTests
{
    protected override SupplierId Id => FakeSupplierIds.SupplierC;

    protected override SupplierCityCodes Codes => new("ALDERMOOR", "XAL");
}
