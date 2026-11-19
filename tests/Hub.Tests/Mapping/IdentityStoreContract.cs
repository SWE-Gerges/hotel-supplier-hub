using Hub.Abstractions;
using Hub.Mapping;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public sealed class StoreHandle(IHotelIdentityStore store, IAliasStore aliases, Func<ValueTask>? cleanup = null) : IAsyncDisposable
{
    public IHotelIdentityStore Store { get; } = store;

    public IAliasStore Aliases { get; } = aliases;

    public ValueTask DisposeAsync() => cleanup?.Invoke() ?? ValueTask.CompletedTask;
}

/// <summary>What every identity and alias store must do, in memory or in a database.</summary>
public abstract class IdentityStoreContractTests
{
    private static readonly SupplierId A = TestData.A;
    private static readonly SupplierId B = TestData.B;

    protected abstract Task<StoreHandle> CreateAsync(int maxRecordsPerCanonicalId = 50);

    [Fact]
    public async Task Minting_gives_sequential_opaque_canonical_ids()
    {
        await using var h = await CreateAsync();

        var first = await h.Store.AddAsync(new AddIdentityRequest(A, "a1"), default);
        var second = await h.Store.AddAsync(new AddIdentityRequest(A, "a2"), default);

        first.ShouldBe(new AddIdentityResult(AddIdentityStatus.Created, "H-000001"));
        second.ShouldBe(new AddIdentityResult(AddIdentityStatus.Created, "H-000002"));
    }

    [Fact]
    public async Task Several_suppliers_can_share_one_canonical_hotel()
    {
        await using var h = await CreateAsync();
        var minted = await h.Store.AddAsync(new AddIdentityRequest(A, "a1"), default);

        var attached = await h.Store.AddAsync(new AddIdentityRequest(B, "b1", minted.CanonicalId), default);

        attached.Status.ShouldBe(AddIdentityStatus.Created);
        attached.CanonicalId.ShouldBe(minted.CanonicalId);
        var records = await h.Store.GetByCanonicalIdAsync(minted.CanonicalId!, default);
        records.ShouldBe([new IdentityRecord(minted.CanonicalId!, A, "a1"), new IdentityRecord(minted.CanonicalId!, B, "b1")]);
    }

    [Fact]
    public async Task Adding_the_same_supplier_code_again_is_idempotent_unless_it_belongs_elsewhere()
    {
        await using var h = await CreateAsync();
        var first = await h.Store.AddAsync(new AddIdentityRequest(A, "a1"), default);
        var other = await h.Store.AddAsync(new AddIdentityRequest(A, "a2"), default);

        (await h.Store.AddAsync(new AddIdentityRequest(A, "a1"), default)).ShouldBe(new AddIdentityResult(AddIdentityStatus.AlreadyExists, first.CanonicalId));
        (await h.Store.AddAsync(new AddIdentityRequest(A, "a1", first.CanonicalId), default)).Status.ShouldBe(AddIdentityStatus.AlreadyExists);
        var conflict = await h.Store.AddAsync(new AddIdentityRequest(A, "a1", other.CanonicalId), default);

        conflict.ShouldBe(new AddIdentityResult(AddIdentityStatus.Conflict, first.CanonicalId));
        (await h.Store.GetByCanonicalIdAsync(other.CanonicalId!, default)).Count.ShouldBe(1, "the conflicting add changed nothing");
    }

    [Theory]
    [InlineData("H-999999")]
    [InlineData("banana")]
    [InlineData("H-")]
    public async Task Attaching_to_a_canonical_id_that_does_not_exist_is_refused(string canonicalId)
    {
        await using var h = await CreateAsync();

        var result = await h.Store.AddAsync(new AddIdentityRequest(A, "a1", canonicalId), default);

        result.ShouldBe(new AddIdentityResult(AddIdentityStatus.UnknownCanonicalId, null));
        (await h.Store.FindCanonicalIdsAsync(A, ["a1"], default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_canonical_hotel_owns_at_most_the_configured_number_of_records()
    {
        await using var h = await CreateAsync(maxRecordsPerCanonicalId: 2);
        var minted = await h.Store.AddAsync(new AddIdentityRequest(A, "a1"), default);
        (await h.Store.AddAsync(new AddIdentityRequest(B, "b1", minted.CanonicalId), default)).Status.ShouldBe(AddIdentityStatus.Created);

        var third = await h.Store.AddAsync(new AddIdentityRequest(A, "a2", minted.CanonicalId), default);

        third.ShouldBe(new AddIdentityResult(AddIdentityStatus.CapExceeded, minted.CanonicalId));
        (await h.Store.GetByCanonicalIdAsync(minted.CanonicalId!, default)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Lookups_by_supplier_code_are_batched_scoped_to_the_supplier_and_ignore_unknown_codes()
    {
        await using var h = await CreateAsync();
        var one = await h.Store.AddAsync(new AddIdentityRequest(A, "shared-code"), default);
        var two = await h.Store.AddAsync(new AddIdentityRequest(B, "shared-code"), default);
        await h.Store.AddAsync(new AddIdentityRequest(A, "a2", one.CanonicalId), default);

        var found = await h.Store.FindCanonicalIdsAsync(A, ["shared-code", "a2", "unknown"], default);

        found.Count.ShouldBe(2);
        found["shared-code"].ShouldBe(one.CanonicalId);
        found["a2"].ShouldBe(one.CanonicalId);
        (await h.Store.FindCanonicalIdsAsync(B, ["shared-code"], default))["shared-code"].ShouldBe(two.CanonicalId);
        (await h.Store.FindCanonicalIdsAsync(A, [], default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Supplier_codes_are_case_sensitive()
    {
        await using var h = await CreateAsync();

        var upper = await h.Store.AddAsync(new AddIdentityRequest(A, "ABC"), default);
        var lower = await h.Store.AddAsync(new AddIdentityRequest(A, "abc"), default);

        upper.Status.ShouldBe(AddIdentityStatus.Created);
        lower.Status.ShouldBe(AddIdentityStatus.Created);
        lower.CanonicalId.ShouldNotBe(upper.CanonicalId);
    }

    [Fact]
    public async Task Canonical_existence_and_unknown_lookups()
    {
        await using var h = await CreateAsync();
        var minted = await h.Store.AddAsync(new AddIdentityRequest(A, "a1"), default);

        (await h.Store.CanonicalIdExistsAsync(minted.CanonicalId!, default)).ShouldBeTrue();
        (await h.Store.CanonicalIdExistsAsync("H-424242", default)).ShouldBeFalse();
        (await h.Store.CanonicalIdExistsAsync("not-an-id", default)).ShouldBeFalse();
        (await h.Store.GetByCanonicalIdAsync("H-424242", default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Racing_adds_of_one_supplier_code_produce_exactly_one_record()
    {
        await using var h = await CreateAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            await h.Store.AddAsync(new AddIdentityRequest(A, "contested"), default))));

        results.Count(static r => r.Status == AddIdentityStatus.Created).ShouldBe(1);
        results.Where(static r => r.Status != AddIdentityStatus.Created).ShouldAllBe(static r => r.Status == AddIdentityStatus.AlreadyExists);
        results.Select(static r => r.CanonicalId).Distinct().Count().ShouldBe(1);
        (await h.Store.GetByCanonicalIdAsync(results[0].CanonicalId!, default)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Aliases_can_be_set_replaced_and_listed_by_survivor()
    {
        await using var h = await CreateAsync();

        (await h.Aliases.GetSurvivorAsync("H-000002", default)).ShouldBeNull();
        await h.Aliases.SetAliasAsync("H-000002", "H-000001", default);
        await h.Aliases.SetAliasAsync("H-000003", "H-000001", default);
        await h.Aliases.SetAliasAsync("H-000004", "H-000009", default);

        (await h.Aliases.GetSurvivorAsync("H-000002", default)).ShouldBe("H-000001");
        (await h.Aliases.GetRetiredIdsAsync("H-000001", default)).ShouldBe(["H-000002", "H-000003"]);

        await h.Aliases.SetAliasAsync("H-000003", "H-000009", default);
        (await h.Aliases.GetSurvivorAsync("H-000003", default)).ShouldBe("H-000009");
        (await h.Aliases.GetRetiredIdsAsync("H-000001", default)).ShouldBe(["H-000002"]);
        (await h.Aliases.GetRetiredIdsAsync("H-000777", default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_store_can_be_pinged()
    {
        await using var h = await CreateAsync();

        await h.Store.PingAsync(default);
    }
}
