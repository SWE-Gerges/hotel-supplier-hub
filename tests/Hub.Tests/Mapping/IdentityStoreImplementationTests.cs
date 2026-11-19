using Hub.Abstractions;
using Hub.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public sealed class InMemoryIdentityStoreTests : IdentityStoreContractTests
{
    protected override Task<StoreHandle> CreateAsync(int maxRecordsPerCanonicalId = 50)
    {
        var store = new InMemoryHotelIdentityStore(Options.Create(new MappingOptions { MaxRecordsPerCanonicalId = maxRecordsPerCanonicalId }));
        return Task.FromResult(new StoreHandle(store, store));
    }
}

public sealed class SqliteIdentityStoreTests : IdentityStoreContractTests
{
    protected override async Task<StoreHandle> CreateAsync(int maxRecordsPerCanonicalId = 50)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hub-identity-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<MappingDbContext>(o => o.UseSqlite($"Data Source={path};Pooling=False"));
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<MappingDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var store = new EfHotelIdentityStore(factory, Options.Create(new MappingOptions { MaxRecordsPerCanonicalId = maxRecordsPerCanonicalId }));
        return new StoreHandle(store, store, async () =>
        {
            await provider.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        });
    }

    [Fact]
    public async Task A_lost_race_leaves_no_orphan_canonical_id_behind()
    {
        await using var h = await CreateAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            await h.Store.AddAsync(new AddIdentityRequest(TestData.A, "contested"), default))));

        results.Count(static r => r.Status == AddIdentityStatus.Created).ShouldBe(1);
        results.Select(static r => r.CanonicalId).Distinct().ShouldHaveSingleItem().ShouldBe("H-000001");
        var next = await h.Store.AddAsync(new AddIdentityRequest(TestData.A, "after"), default);
        next.CanonicalId.ShouldBe("H-000002", "the losers rolled their minted ids back, so the sequence has no gap");
    }

    [Fact]
    public async Task The_database_itself_enforces_one_owner_per_supplier_code()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hub-identity-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<MappingDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new MappingDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.Identities.Add(new HotelIdentityEntity { CanonicalId = "H-000001", Supplier = "supplier-a", SupplierCode = "x" });
            await db.SaveChangesAsync();
            db.Identities.Add(new HotelIdentityEntity { CanonicalId = "H-000002", Supplier = "supplier-a", SupplierCode = "x" });

            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
