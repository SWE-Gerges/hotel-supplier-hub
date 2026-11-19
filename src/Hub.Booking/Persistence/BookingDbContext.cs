using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Hub.Booking;

/// <summary>Quotes and bookings. Provider-neutral: the host picks SQLite or PostgreSQL.</summary>
public sealed class BookingDbContext(DbContextOptions<BookingDbContext> options) : DbContext(options)
{
    public DbSet<QuoteRecord> Quotes => Set<QuoteRecord>();

    public DbSet<BookingRecord> Bookings => Set<BookingRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Instants are stored as UTC ticks: comparable and sortable in every provider, which a DateTimeOffset column is not in SQLite.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<NullableUtcTicksConverter>();
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
        configurationBuilder.Properties<decimal?>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QuoteRecord>(entity =>
        {
            entity.ToTable("quotes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(40);
            entity.Property(e => e.Tenant).HasMaxLength(64).IsRequired();
            entity.Property(e => e.OfferId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Supplier).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SellingCurrency).HasMaxLength(3).IsRequired();
            entity.Property(e => e.SupplierCurrency).HasMaxLength(3).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.OfferJson).IsRequired();
            entity.Property(e => e.SearchJson).IsRequired();
            entity.Ignore(e => e.Selling);
            entity.Ignore(e => e.SupplierPrice);
            entity.HasIndex(e => new { e.Tenant, e.OfferId, e.Status });
        });

        modelBuilder.Entity<BookingRecord>(entity =>
        {
            entity.ToTable("bookings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(40);
            entity.Property(e => e.Tenant).HasMaxLength(64).IsRequired();
            entity.Property(e => e.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(e => e.PayloadHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.QuoteId).HasMaxLength(40).IsRequired();
            entity.Property(e => e.OfferId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Supplier).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SupplierHotelCode).HasMaxLength(128).IsRequired();
            entity.Property(e => e.SupplierReference).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SupplierBookingRef).HasMaxLength(128);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.FailureReason).HasMaxLength(256);
            entity.Property(e => e.Note).HasMaxLength(512);
            entity.Property(e => e.SellingCurrency).HasMaxLength(3).IsRequired();
            entity.Property(e => e.SupplierCurrency).HasMaxLength(3).IsRequired();
            entity.Property(e => e.LeadGuestName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.PaymentMode).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.CancelStatus).HasMaxLength(32);
            entity.Property(e => e.OfferJson).IsRequired();
            entity.Property(e => e.SearchJson).IsRequired();
            entity.Property(e => e.Version).IsConcurrencyToken();
            entity.Ignore(e => e.Selling);
            entity.Ignore(e => e.SupplierPrice);
            entity.HasIndex(e => new { e.Tenant, e.IdempotencyKey }).IsUnique();
            entity.HasIndex(e => new { e.Status, e.UpdatedAt });
            entity.HasIndex(e => new { e.PaymentMode, e.PayBy });
        });
    }

    private sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
        static value => value.UtcTicks, static ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

    private sealed class NullableUtcTicksConverter() : ValueConverter<DateTimeOffset?, long?>(
        static value => value.HasValue ? value.Value.UtcTicks : null,
        static ticks => ticks.HasValue ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null);
}
