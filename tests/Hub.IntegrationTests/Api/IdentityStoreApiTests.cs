using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Hub.Abstractions;
using Hub.Mapping;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public sealed class SqliteApiFactory(string path) : HubApiFactory
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>(base.Settings)
    {
        ["Hub:IdentityStore:Provider"] = "Sqlite",
        ["Hub:IdentityStore:ConnectionString"] = $"Data Source={path};Pooling=False",
    };
}

/// <summary>An identity store that can be switched off, to stand in for a database outage.</summary>
public sealed class SwitchableIdentityStore(InMemoryHotelIdentityStore inner) : IHotelIdentityStore, IAliasStore
{
    public volatile bool Down;

    private void ThrowIfDown()
    {
        if (Down)
        {
            throw new InvalidOperationException("The identity database cannot be reached.");
        }
    }

    public ValueTask<IReadOnlyDictionary<string, string>> FindCanonicalIdsAsync(SupplierId supplier, IReadOnlyCollection<string> supplierCodes, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        return inner.FindCanonicalIdsAsync(supplier, supplierCodes, cancellationToken);
    }

    public ValueTask<IReadOnlyList<IdentityRecord>> GetByCanonicalIdAsync(string canonicalId, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        return inner.GetByCanonicalIdAsync(canonicalId, cancellationToken);
    }

    public ValueTask<bool> CanonicalIdExistsAsync(string canonicalId, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        return inner.CanonicalIdExistsAsync(canonicalId, cancellationToken);
    }

    public ValueTask<AddIdentityResult> AddAsync(AddIdentityRequest request, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        return inner.AddAsync(request, cancellationToken);
    }

    public ValueTask PingAsync(CancellationToken cancellationToken)
    {
        ThrowIfDown();
        return inner.PingAsync(cancellationToken);
    }

    public ValueTask<string?> GetSurvivorAsync(string retiredId, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        return inner.GetSurvivorAsync(retiredId, cancellationToken);
    }

    public ValueTask SetAliasAsync(string retiredId, string survivorId, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        return inner.SetAliasAsync(retiredId, survivorId, cancellationToken);
    }

    public ValueTask<IReadOnlyList<string>> GetRetiredIdsAsync(string survivorId, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        return inner.GetRetiredIdsAsync(survivorId, cancellationToken);
    }
}

public sealed class SwitchableStoreApiFactory : HubApiFactory
{
    protected override void ConfigureServicesForTest(IServiceCollection services)
    {
        services.AddSingleton<SwitchableIdentityStore>(sp => new SwitchableIdentityStore(sp.GetRequiredService<InMemoryHotelIdentityStore>()));
        services.AddSingleton<IHotelIdentityStore>(sp => sp.GetRequiredService<SwitchableIdentityStore>());
        services.AddSingleton<IAliasStore>(sp => sp.GetRequiredService<SwitchableIdentityStore>());
    }

    public SwitchableIdentityStore Store => Services.GetRequiredService<SwitchableIdentityStore>();
}

public sealed class SqliteApiTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"hub-api-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public async Task Identities_live_in_the_database_survive_a_restart_and_drive_merging()
    {
        JsonNode firstRun;
        await using (var factory = new SqliteApiFactory(_path))
        {
            var client = factory.ClientFor(HubApiFactory.ClientKey);
            var body = await HubApiFactory.ReadJsonAsync(await client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody()));
            factory.Logs.Entries.Where(static e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Warning)
                .ShouldBeEmpty("starting on a fresh database and searching writes no warnings or errors");
            body["summary"]!["status"]!.GetValue<string>().ShouldBe("completed");
            body["hotels"]!.AsArray().ShouldContain(static h => h!["sources"]!.AsArray().Count > 1, "hotels merge through identities stored in the database");
            firstRun = await HubApiFactory.ReadJsonAsync(await factory.ClientFor(HubApiFactory.AdminKey).GetAsync("/api/v1/mapping/hotels/H-000001"));

            var added = await factory.ClientFor(HubApiFactory.AdminKey).PostAsJsonAsync(
                "/api/v1/mapping/identities", new { supplier = "supplier-a", supplierCode = "added-at-runtime" });
            added.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        await using var restarted = new SqliteApiFactory(_path);
        var admin = restarted.ClientFor(HubApiFactory.AdminKey);

        var again = await HubApiFactory.ReadJsonAsync(await admin.GetAsync("/api/v1/mapping/hotels/H-000001"));
        again.ToJsonString().ShouldBe(firstRun.ToJsonString(), "re-seeding on restart changed nothing");
        var runtime = await admin.PostAsJsonAsync("/api/v1/mapping/identities", new { supplier = "supplier-a", supplierCode = "added-at-runtime" });
        runtime.StatusCode.ShouldBe(HttpStatusCode.OK, "the record added before the restart is still there");
    }
}

public sealed class IdentityStoreOutageApiTests
{
    [Fact]
    public async Task Readiness_reports_the_outage_while_searches_fail_open_and_keep_returning_hotels()
    {
        await using var factory = new SwitchableStoreApiFactory();
        var anonymous = factory.CreateClient();
        var client = factory.ClientFor(HubApiFactory.ClientKey);
        (await anonymous.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);

        factory.Store.Down = true;

        var ready = await anonymous.GetAsync("/health/ready");
        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ready.Content.ReadAsStringAsync()).ShouldNotContain("InvalidOperationException", Case.Insensitive);
        var live = await anonymous.GetAsync("/health/live");
        live.StatusCode.ShouldBe(HttpStatusCode.OK, "being alive does not depend on the database");

        var body = await HubApiFactory.ReadJsonAsync(await client.PostAsJsonAsync("/api/v1/searches", HubApiFactory.SearchBody()));
        body["summary"]!["status"]!.GetValue<string>().ShouldBe("completed");
        body["hotels"]!.AsArray().ShouldNotBeEmpty("an unreachable identity store must not make hotels disappear");
        body["hotels"]!.AsArray().ShouldAllBe(static h => h!["canonicalId"] == null, "they are simply not merged");
    }
}
