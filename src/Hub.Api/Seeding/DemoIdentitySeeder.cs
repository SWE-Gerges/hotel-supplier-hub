using Hub.Abstractions;
using Hub.Mapping;
using Hub.Suppliers.Fakes;
using Microsoft.Extensions.Options;

namespace Hub.Api;

/// <summary>
/// Registers every fake-supplier hotel in the identity store, one canonical id per physical hotel, so the demo can
/// merge across suppliers. Safe to run on every start: existing records are left alone.
/// </summary>
internal sealed class DemoIdentitySeeder(
    SyntheticCatalog catalog,
    IHotelIdentityStore store,
    IOptions<DemoOptions> options,
    ILogger<DemoIdentitySeeder> logger) : IHostedService
{
    private static readonly SupplierId[] Suppliers = [FakeSupplierIds.SupplierA, FakeSupplierIds.SupplierB, FakeSupplierIds.SupplierC];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.SeedIdentities)
        {
            return;
        }

        var created = 0;
        foreach (var hotel in catalog.Hotels)
        {
            var listings = Suppliers
                .Where(s => catalog.Lists(s, hotel))
                .Select(s => (Supplier: s, Code: catalog.CodeFor(s, hotel)))
                .ToArray();

            string? canonicalId = null;
            foreach (var (supplier, code) in listings)
            {
                var existing = await store.FindCanonicalIdsAsync(supplier, [code], cancellationToken).ConfigureAwait(false);
                if (existing.TryGetValue(code, out var found))
                {
                    canonicalId = found;
                    break;
                }
            }

            foreach (var (supplier, code) in listings)
            {
                var result = await store.AddAsync(new AddIdentityRequest(supplier, code, canonicalId), cancellationToken).ConfigureAwait(false);
                canonicalId ??= result.CanonicalId;
                if (result.Status == AddIdentityStatus.Created)
                {
                    created++;
                }
            }
        }

        logger.LogInformation("Demo identity seeding finished: {Created} supplier records added.", created);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
