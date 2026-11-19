using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Hub.Mapping;

/// <summary>A supplier city code that is known to mean a canonical city. Each supplier has at most one code per canonical city.</summary>
public sealed class CrosswalkEntry
{
    public long Id { get; set; }

    public string Supplier { get; set; } = string.Empty;

    public string SupplierCityCode { get; set; } = string.Empty;

    public string SupplierCountryCode { get; set; } = string.Empty;

    public string CanonicalCityId { get; set; } = string.Empty;

    public double Score { get; set; }

    /// <summary><c>auto</c> when the matcher accepted it, <c>approved</c> when a person did.</summary>
    public string Source { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

public enum ReviewStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>A supplier city the matcher would not decide on its own.</summary>
public sealed class CrosswalkReview
{
    public long Id { get; set; }

    public string Supplier { get; set; } = string.Empty;

    public string SupplierCityCode { get; set; } = string.Empty;

    public string SupplierCityName { get; set; } = string.Empty;

    public string SupplierCountryCode { get; set; } = string.Empty;

    public string? SupplierRegion { get; set; }

    public ReviewStatus Status { get; set; }

    /// <summary>The best candidate, when there was one.</summary>
    public string? ProposedCityId { get; set; }

    public double Score { get; set; }

    public double RunnerUpScore { get; set; }

    public string Reason { get; set; } = string.Empty;

    /// <summary>The candidates and their scores, best first, as JSON.</summary>
    public string CandidatesJson { get; set; } = "[]";

    public string? ResolvedCityId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class CrosswalkDbContext(DbContextOptions<CrosswalkDbContext> options) : DbContext(options)
{
    public DbSet<CrosswalkEntry> Entries => Set<CrosswalkEntry>();

    public DbSet<CrosswalkReview> Reviews => Set<CrosswalkReview>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<NullableUtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CrosswalkEntry>(entity =>
        {
            entity.ToTable("crosswalk_entries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Supplier).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SupplierCityCode).HasMaxLength(128).IsRequired();
            entity.Property(e => e.SupplierCountryCode).HasMaxLength(64).IsRequired();
            entity.Property(e => e.CanonicalCityId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Source).HasMaxLength(16).IsRequired();
            // One canonical city per supplier code, and one supplier code per canonical city.
            entity.HasIndex(e => new { e.Supplier, e.SupplierCityCode }).IsUnique();
            entity.HasIndex(e => new { e.Supplier, e.CanonicalCityId }).IsUnique();
        });

        modelBuilder.Entity<CrosswalkReview>(entity =>
        {
            entity.ToTable("crosswalk_reviews");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Supplier).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SupplierCityCode).HasMaxLength(128).IsRequired();
            entity.Property(e => e.SupplierCityName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.SupplierCountryCode).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SupplierRegion).HasMaxLength(200);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.ProposedCityId).HasMaxLength(64);
            entity.Property(e => e.Reason).HasMaxLength(512).IsRequired();
            entity.Property(e => e.CandidatesJson).IsRequired();
            entity.Property(e => e.ResolvedCityId).HasMaxLength(64);
            entity.HasIndex(e => new { e.Supplier, e.SupplierCityCode }).IsUnique();
            entity.HasIndex(e => e.Status);
        });
    }

    private sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
        static value => value.UtcTicks, static ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

    private sealed class NullableUtcTicksConverter() : ValueConverter<DateTimeOffset?, long?>(
        static value => value.HasValue ? value.Value.UtcTicks : null,
        static ticks => ticks.HasValue ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null);
}

public enum AddEntryOutcome
{
    Added,

    /// <summary>The supplier code already means a (possibly different) canonical city.</summary>
    CodeTaken,

    /// <summary>The supplier already has a different code for that canonical city.</summary>
    CityTaken,
}

public interface ICrosswalkStore
{
    Task<IReadOnlyList<CrosswalkEntry>> GetEntriesAsync(string? supplier, CancellationToken cancellationToken);

    Task<CrosswalkEntry?> FindEntryAsync(string supplier, string canonicalCityId, CancellationToken cancellationToken);

    Task<AddEntryOutcome> TryAddEntryAsync(CrosswalkEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<CrosswalkReview>> GetReviewsAsync(ReviewStatus? status, string? supplier, CancellationToken cancellationToken);

    Task<CrosswalkReview?> FindReviewAsync(long id, CancellationToken cancellationToken);

    /// <summary>Adds the review, or returns false when this supplier city already has one.</summary>
    Task<bool> TryAddReviewAsync(CrosswalkReview review, CancellationToken cancellationToken);

    Task UpdateReviewAsync(CrosswalkReview review, CancellationToken cancellationToken);
}

internal sealed class EfCrosswalkStore(IDbContextFactory<CrosswalkDbContext> contextFactory) : ICrosswalkStore
{
    public async Task<IReadOnlyList<CrosswalkEntry>> GetEntriesAsync(string? supplier, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Entries.AsNoTracking().Where(e => supplier == null || e.Supplier == supplier)
            .OrderBy(e => e.Supplier).ThenBy(e => e.CanonicalCityId).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrosswalkEntry?> FindEntryAsync(string supplier, string canonicalCityId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.Supplier == supplier && e.CanonicalCityId == canonicalCityId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AddEntryOutcome> TryAddEntryAsync(CrosswalkEntry entry, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Entries.Add(entry);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return AddEntryOutcome.Added;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.Entries.AnyAsync(e => e.Supplier == entry.Supplier && e.SupplierCityCode == entry.SupplierCityCode, cancellationToken).ConfigureAwait(false))
            {
                return AddEntryOutcome.CodeTaken;
            }

            if (await db.Entries.AnyAsync(e => e.Supplier == entry.Supplier && e.CanonicalCityId == entry.CanonicalCityId, cancellationToken).ConfigureAwait(false))
            {
                return AddEntryOutcome.CityTaken;
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<CrosswalkReview>> GetReviewsAsync(ReviewStatus? status, string? supplier, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Reviews.AsNoTracking()
            .Where(r => (status == null || r.Status == status) && (supplier == null || r.Supplier == supplier))
            .OrderBy(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CrosswalkReview?> FindReviewAsync(long id, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Reviews.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryAddReviewAsync(CrosswalkReview review, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Reviews.Add(review);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.Reviews.AnyAsync(r => r.Supplier == review.Supplier && r.SupplierCityCode == review.SupplierCityCode, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            throw;
        }
    }

    public async Task UpdateReviewAsync(CrosswalkReview review, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Reviews.Update(review);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
