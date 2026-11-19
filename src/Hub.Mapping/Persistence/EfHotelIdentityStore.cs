using Hub.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hub.Mapping;

/// <summary>Relational identity and alias store. The (supplier, code) pair is unique in the database.</summary>
public sealed class EfHotelIdentityStore(IDbContextFactory<MappingDbContext> contextFactory, IOptions<MappingOptions> options)
    : IHotelIdentityStore, IAliasStore
{
    public async ValueTask<IReadOnlyDictionary<string, string>> FindCanonicalIdsAsync(
        SupplierId supplier, IReadOnlyCollection<string> supplierCodes, CancellationToken cancellationToken)
    {
        if (supplierCodes.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var supplierKey = supplier.Value;
        var codes = supplierCodes.ToArray();

        var rows = await db.Identities
            .AsNoTracking()
            .Where(i => i.Supplier == supplierKey && codes.Contains(i.SupplierCode))
            .Select(static i => new { i.SupplierCode, i.CanonicalId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(static r => r.SupplierCode, static r => r.CanonicalId, StringComparer.Ordinal);
    }

    public async ValueTask<IReadOnlyList<IdentityRecord>> GetByCanonicalIdAsync(string canonicalId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Identities
            .AsNoTracking()
            .Where(i => i.CanonicalId == canonicalId)
            .OrderBy(static i => i.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(static r => new IdentityRecord(r.CanonicalId, SupplierId.Parse(r.Supplier), r.SupplierCode)).ToArray();
    }

    public async ValueTask<bool> CanonicalIdExistsAsync(string canonicalId, CancellationToken cancellationToken)
    {
        if (!CanonicalIds.TryParse(canonicalId, out var sequence))
        {
            return false;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.CanonicalHotels.AnyAsync(h => h.Id == sequence, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AddIdentityResult> AddAsync(AddIdentityRequest request, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var supplierKey = request.Supplier.Value;

        var existing = await FindOwnerAsync(db, supplierKey, request.SupplierCode, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return ExistingResult(request, existing);
        }

        long? existingSequence = null;
        if (request.CanonicalId is not null)
        {
            if (!CanonicalIds.TryParse(request.CanonicalId, out var parsed)
                || !await db.CanonicalHotels.AnyAsync(h => h.Id == parsed, cancellationToken).ConfigureAwait(false))
            {
                return new AddIdentityResult(AddIdentityStatus.UnknownCanonicalId, null);
            }

            existingSequence = parsed;
            var count = await db.Identities.CountAsync(i => i.CanonicalId == request.CanonicalId, cancellationToken).ConfigureAwait(false);
            if (count >= options.Value.MaxRecordsPerCanonicalId)
            {
                return new AddIdentityResult(AddIdentityStatus.CapExceeded, request.CanonicalId);
            }
        }

        // Minting the canonical row and attaching the record commit together, so a lost race leaves no orphan id.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sequence = existingSequence;
            if (sequence is null)
            {
                var minted = new CanonicalHotelEntity();
                db.CanonicalHotels.Add(minted);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                sequence = minted.Id;
            }

            var canonicalId = CanonicalIds.Format(sequence.Value);
            db.Identities.Add(new HotelIdentityEntity { CanonicalId = canonicalId, Supplier = supplierKey, SupplierCode = request.SupplierCode });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AddIdentityResult(AddIdentityStatus.Created, canonicalId);
        }
        catch (DbUpdateException)
        {
            // Most likely another writer claimed the same (supplier, code); report what is stored now.
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            var winner = await FindOwnerAsync(db, supplierKey, request.SupplierCode, cancellationToken).ConfigureAwait(false);
            if (winner is null)
            {
                throw;
            }

            return ExistingResult(request, winner);
        }
    }

    public async ValueTask PingAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The identity database cannot be reached.");
        }
    }

    public async ValueTask<string?> GetSurvivorAsync(string retiredId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Aliases
            .AsNoTracking()
            .Where(a => a.RetiredId == retiredId)
            .Select(static a => a.SurvivorId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask SetAliasAsync(string retiredId, string survivorId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var alias = await db.Aliases.FirstOrDefaultAsync(a => a.RetiredId == retiredId, cancellationToken).ConfigureAwait(false);
        if (alias is null)
        {
            db.Aliases.Add(new HotelAliasEntity { RetiredId = retiredId, SurvivorId = survivorId });
        }
        else
        {
            alias.SurvivorId = survivorId;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<string>> GetRetiredIdsAsync(string survivorId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Aliases
            .AsNoTracking()
            .Where(a => a.SurvivorId == survivorId)
            .OrderBy(static a => a.RetiredId)
            .Select(static a => a.RetiredId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task<string?> FindOwnerAsync(MappingDbContext db, string supplier, string code, CancellationToken cancellationToken) =>
        db.Identities
            .AsNoTracking()
            .Where(i => i.Supplier == supplier && i.SupplierCode == code)
            .Select(static i => i.CanonicalId)
            .FirstOrDefaultAsync(cancellationToken);

    private static AddIdentityResult ExistingResult(AddIdentityRequest request, string owner)
    {
        var same = request.CanonicalId is null || string.Equals(request.CanonicalId, owner, StringComparison.Ordinal);
        return new AddIdentityResult(same ? AddIdentityStatus.AlreadyExists : AddIdentityStatus.Conflict, owner);
    }
}
