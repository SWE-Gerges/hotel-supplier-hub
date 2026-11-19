using Hub.Abstractions;
using Hub.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class SearchRunRegistryTests
{
    private static readonly SearchOwner Alice = new("tenant-1", "alice");
    private static readonly SearchOwner Bob = new("tenant-1", "bob");
    private static readonly SearchOwner AliceElsewhere = new("tenant-2", "alice");

    private static SearchRunRegistry Create() => new(TimeProvider.System);

    [Fact]
    public void A_started_run_is_active_until_completed()
    {
        var registry = Create();

        var run = registry.Start(Alice);

        registry.ActiveCount.ShouldBe(1);
        run.Id.ShouldNotBeNullOrEmpty();
        run.Owner.ShouldBe(Alice);
        registry.Complete(run);
        registry.ActiveCount.ShouldBe(0);
    }

    [Fact]
    public void The_owner_can_cancel_a_run_and_nobody_else_can()
    {
        var registry = Create();
        var run = registry.Start(Alice, "run-1");

        registry.Cancel("run-1", Bob).ShouldBe(CancelOutcome.NotOwner);
        registry.Cancel("run-1", AliceElsewhere).ShouldBe(CancelOutcome.NotOwner, "the same client name in another tenant is a different owner");
        run.IsCancelled.ShouldBeFalse();

        registry.Cancel("run-1", Alice).ShouldBe(CancelOutcome.Cancelled);
        run.Token.IsCancellationRequested.ShouldBeTrue();
        registry.Cancel("nope", Alice).ShouldBe(CancelOutcome.NotFound);
    }

    [Fact]
    public void Replacing_cancels_the_previous_run_of_the_same_owner()
    {
        var registry = Create();
        var previous = registry.Start(Alice, "run-1");

        var next = registry.Start(Alice, "run-2", replacesRunId: "run-1");

        previous.IsCancelled.ShouldBeTrue();
        next.IsCancelled.ShouldBeFalse();
        registry.ActiveCount.ShouldBe(1);
        registry.Cancel("run-1", Alice).ShouldBe(CancelOutcome.NotFound);
    }

    [Fact]
    public void Replacing_someone_elses_run_is_refused_and_changes_nothing()
    {
        var registry = Create();
        var victim = registry.Start(Alice, "run-1");

        Should.Throw<SearchRunAccessDeniedException>(() => registry.Start(Bob, "run-2", replacesRunId: "run-1"));

        victim.IsCancelled.ShouldBeFalse();
        registry.ActiveCount.ShouldBe(1);
    }

    [Fact]
    public void Replacing_a_run_that_is_gone_is_not_an_error()
    {
        var registry = Create();

        registry.Start(Alice, "run-2", replacesRunId: "long-finished");

        registry.ActiveCount.ShouldBe(1);
    }

    [Fact]
    public void A_run_id_that_is_in_use_is_refused()
    {
        var registry = Create();
        registry.Start(Alice, "run-1");

        Should.Throw<SearchRunConflictException>(() => registry.Start(Bob, "run-1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public void Unsafe_run_ids_are_refused(string id)
    {
        Should.Throw<ArgumentException>(() => Create().Start(Alice, id));
    }

    [Fact]
    public void Completing_a_replaced_run_does_not_remove_its_replacement()
    {
        var registry = Create();
        var original = registry.Start(Alice, "run-1");
        var replacement = registry.Start(Alice, "run-1b", replacesRunId: "run-1");

        registry.Complete(original);

        registry.ActiveCount.ShouldBe(1);
        registry.Cancel(replacement.Id, Alice).ShouldBe(CancelOutcome.Cancelled);
    }

    [Fact]
    public async Task Racing_starts_with_one_id_produce_exactly_one_winner()
    {
        var registry = Create();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            try
            {
                registry.Start(new SearchOwner("t", $"c{i}"), "contested");
                return true;
            }
            catch (SearchRunConflictException)
            {
                return false;
            }
        })));

        outcomes.Count(static won => won).ShouldBe(1);
        registry.ActiveCount.ShouldBe(1);
    }
}

public class TenantAndPricingTests
{
    private sealed class SearchOnly(string id) : ISupplierDescriptor, IHotelSearch
    {
        public SupplierId SupplierId { get; } = SupplierId.Parse(id);

        public SupplierCapabilities Capabilities => SupplierCapabilities.Search;

        public SearchStyle SearchStyle => SearchStyle.PagedSync;

        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(1);

        public int MaxConcurrency => 1;

        public IAsyncEnumerable<Result<IReadOnlyList<Hotel>>> SearchAsync(SupplierSearchQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class BookOnly(string id) : ISupplierDescriptor
    {
        public SupplierId SupplierId { get; } = SupplierId.Parse(id);

        public SupplierCapabilities Capabilities => SupplierCapabilities.Book;

        public SearchStyle SearchStyle => SearchStyle.PagedSync;

        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(1);

        public int MaxConcurrency => 1;
    }

    private static TenantSupplierSelector Selector(Dictionary<string, TenantConfig> tenants) =>
        new(
            new StaticSupplierRegistry(
            [
                new SupplierEntry(new SearchOnly("supplier-a"), new SearchOnly("supplier-a")),
                new SupplierEntry(new SearchOnly("supplier-b"), new SearchOnly("supplier-b")),
                new SupplierEntry(new BookOnly("supplier-c"), null),
            ]),
            Options.Create(new TenantOptions { Tenants = tenants }));

    [Fact]
    public void Suppliers_come_back_in_the_tenants_priority_order()
    {
        var selector = Selector(new() { ["t"] = new() { EnabledSuppliers = ["supplier-b", "supplier-a"] } });

        selector.Select("t", null).ShouldBe([TestData.B, TestData.A]);
    }

    [Fact]
    public void The_request_filter_narrows_the_list_without_changing_the_order()
    {
        var selector = Selector(new() { ["t"] = new() { EnabledSuppliers = ["supplier-b", "supplier-a"] } });

        selector.Select("t", [TestData.A]).ShouldBe([TestData.A]);
        selector.Select("t", [TestData.A, TestData.B]).ShouldBe([TestData.B, TestData.A]);
    }

    [Fact]
    public void Unknown_tenants_unregistered_suppliers_and_suppliers_that_cannot_search_are_skipped_not_errors()
    {
        var selector = Selector(new() { ["t"] = new() { EnabledSuppliers = ["supplier-a", "supplier-c", "supplier-zzz"] } });

        selector.Select("t", null).ShouldBe([TestData.A]);
        selector.Select("ghost", null).ShouldBeEmpty();
    }

    [Fact]
    public void Enablement_is_per_tenant()
    {
        var selector = Selector(new() { ["t"] = new() { EnabledSuppliers = ["supplier-a"] } });

        selector.IsEnabled("t", TestData.A).ShouldBeTrue();
        selector.IsEnabled("t", TestData.B).ShouldBeFalse();
        selector.IsEnabled("other", TestData.A).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, 100.00, 100.00)]
    [InlineData(10, 100.00, 110.00)]
    [InlineData(12.5, 80.00, 90.00)]
    [InlineData(7, 33.33, 35.66)]
    public void Markup_is_added_to_the_supplier_price_and_rounded_to_cents(double percent, double price, double expected)
    {
        var pricing = new TenantMarkupPricing(Options.Create(new TenantOptions
        {
            Tenants = new() { ["t"] = new() { MarkupPercent = (decimal)percent } },
        }));

        pricing.ToSellingPrice("t", TestData.A, new Money((decimal)price, "EUR"), "EUR").ShouldBe(new Money((decimal)expected, "EUR"));
        pricing.ToSellingPrice("unknown", TestData.A, new Money((decimal)price, "EUR"), "EUR")!.Value.Amount.ShouldBe((decimal)price);
    }
}

public class KeyedSupplierRegistrationTests
{
    private sealed class FullSupplier : ISupplierDescriptor, IHotelSearch, IBook
    {
        public SupplierId SupplierId => TestData.A;

        public SupplierCapabilities Capabilities => SupplierCapabilities.Search | SupplierCapabilities.Book;

        public SearchStyle SearchStyle => SearchStyle.PagedSync;

        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(1);

        public int MaxConcurrency => 1;

        public IAsyncEnumerable<Result<IReadOnlyList<Hotel>>> SearchAsync(SupplierSearchQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<BookingConfirmation>> BookAsync(BookRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class DescriptorOnly : ISupplierDescriptor
    {
        public SupplierId SupplierId => TestData.B;

        public SupplierCapabilities Capabilities => SupplierCapabilities.None;

        public SearchStyle SearchStyle => SearchStyle.PagedSync;

        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(1);

        public int MaxConcurrency => 1;
    }

    private sealed class WrongId : ISupplierDescriptor
    {
        public SupplierId SupplierId => TestData.C;

        public SupplierCapabilities Capabilities => SupplierCapabilities.None;

        public SearchStyle SearchStyle => SearchStyle.PagedSync;

        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(1);

        public int MaxConcurrency => 1;
    }

    [Fact]
    public void A_supplier_is_exposed_through_exactly_the_ports_it_implements()
    {
        var services = new ServiceCollection();
        services.AddHotelSupplier<FullSupplier>(TestData.A);
        services.AddHotelSupplier<DescriptorOnly>(TestData.B);
        using var provider = services.BuildServiceProvider();

        provider.GetKeyedService<IHotelSearch>(TestData.A).ShouldNotBeNull();
        provider.GetKeyedService<IBook>(TestData.A).ShouldNotBeNull();
        provider.GetKeyedService<ICancel>(TestData.A).ShouldBeNull();
        provider.GetKeyedService<IHotelSearch>(TestData.B).ShouldBeNull();
        provider.GetRequiredKeyedService<ISupplierDescriptor>(TestData.B).SupplierId.ShouldBe(TestData.B);
    }

    [Fact]
    public void The_registry_lists_suppliers_in_registration_order_with_their_search_port()
    {
        var services = new ServiceCollection();
        services.AddHotelSupplier<FullSupplier>(TestData.A);
        services.AddHotelSupplier<DescriptorOnly>(TestData.B);
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ISupplierRegistry>();

        registry.Suppliers.Select(static s => s.Id).ShouldBe([TestData.A, TestData.B]);
        registry.TryGet(TestData.A, out var a).ShouldBeTrue();
        a!.Search.ShouldNotBeNull();
        registry.TryGet(TestData.B, out var b).ShouldBeTrue();
        b!.Search.ShouldBeNull("a supplier without the search capability simply has no search port");
        registry.TryGet(TestData.C, out _).ShouldBeFalse();
    }

    [Fact]
    public void Registering_the_same_id_twice_is_refused()
    {
        var services = new ServiceCollection();
        services.AddHotelSupplier<FullSupplier>(TestData.A);

        Should.Throw<InvalidOperationException>(() => services.AddHotelSupplier<DescriptorOnly>(TestData.A));
    }

    [Fact]
    public void A_supplier_whose_descriptor_disagrees_with_its_registered_id_is_caught_when_the_registry_is_built()
    {
        var services = new ServiceCollection();
        services.AddHotelSupplier<WrongId>(TestData.A);
        using var provider = services.BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<ISupplierRegistry>());
    }

    [Fact]
    public void The_default_id_is_not_a_valid_registration()
    {
        Should.Throw<ArgumentException>(() => new ServiceCollection().AddHotelSupplier<FullSupplier>(default));
    }
}

public class OptionsValidationTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHubCore(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Defaults_are_valid()
    {
        using var provider = Build([]);

        provider.GetRequiredService<IOptions<ResilienceOptions>>().Value.ShouldNotBeNull();
        provider.GetRequiredService<IOptions<SearchOptions>>().Value.GlobalDeadline.ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Theory]
    [InlineData("Hub:Resilience:Defaults:MaxAttempts", "0")]
    [InlineData("Hub:Resilience:Defaults:MaxAttempts", "11")]
    [InlineData("Hub:Resilience:Defaults:CircuitFailureRatio", "0")]
    [InlineData("Hub:Resilience:Defaults:CircuitMinimumThroughput", "1")]
    [InlineData("Hub:Resilience:Defaults:CircuitBreakDuration", "00:00:00.100")]
    [InlineData("Hub:Resilience:Defaults:QueueLimit", "-1")]
    [InlineData("Hub:Resilience:Defaults:Timeout", "00:00:00")]
    [InlineData("Hub:Resilience:Suppliers:NOT A SUPPLIER:MaxAttempts", "2")]
    [InlineData("Hub:Resilience:Suppliers:supplier-a:MaxConcurrency", "0")]
    [InlineData("Hub:Search:GlobalDeadline", "00:00:00")]
    [InlineData("Hub:Search:UpdateChannelCapacity", "0")]
    [InlineData("Hub:Search:MaxOffersPerHotel", "0")]
    [InlineData("Hub:Tenancy:Tenants:t:EnabledSuppliers:0", "Bad Supplier")]
    [InlineData("Hub:Tenancy:Tenants:t:MarkupPercent", "150")]
    public void Invalid_settings_are_rejected_when_the_options_are_first_used(string key, string value)
    {
        using var provider = Build(new Dictionary<string, string?> { [key] = value });

        Should.Throw<OptionsValidationException>(() =>
        {
            _ = provider.GetRequiredService<IOptions<ResilienceOptions>>().Value;
            _ = provider.GetRequiredService<IOptions<SearchOptions>>().Value;
            _ = provider.GetRequiredService<IOptions<TenantOptions>>().Value;
        });
    }

    [Fact]
    public void A_tenant_cannot_list_a_supplier_twice()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["Hub:Tenancy:Tenants:t:EnabledSuppliers:0"] = "supplier-a",
            ["Hub:Tenancy:Tenants:t:EnabledSuppliers:1"] = "supplier-a",
        });

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TenantOptions>>().Value);
    }

    [Fact]
    public void Resilience_settings_resolve_from_override_then_defaults_then_the_supplier_descriptor()
    {
        var options = new ResilienceOptions
        {
            Defaults = new SupplierResilienceOptions { MaxAttempts = 4, QueueLimit = 7 },
            Suppliers = new() { ["supplier-a"] = new SupplierResilienceOptions { MaxAttempts = 2, MaxConcurrency = 3 } },
        };
        var descriptor = new StubSupplier(TestData.A, (_, _, _) => StubSupplier.One(Result.Ok<IReadOnlyList<Hotel>>([])), maxConcurrency: 9)
        {
            DefaultTimeout = TimeSpan.FromSeconds(8),
        };

        var resolved = options.Resolve(descriptor);

        resolved.MaxAttempts.ShouldBe(2, "the supplier override wins");
        resolved.MaxConcurrency.ShouldBe(3);
        resolved.QueueLimit.ShouldBe(7, "defaults fill the gaps");
        resolved.Timeout.ShouldBe(TimeSpan.FromSeconds(8), "the descriptor is the last fallback");

        var other = options.Resolve(new StubSupplier(TestData.B, (_, _, _) => StubSupplier.One(Result.Ok<IReadOnlyList<Hotel>>([])), maxConcurrency: 9));
        other.MaxAttempts.ShouldBe(4);
        other.MaxConcurrency.ShouldBe(9);
    }
}
