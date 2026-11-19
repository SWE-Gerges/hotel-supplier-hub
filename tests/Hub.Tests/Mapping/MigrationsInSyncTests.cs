using Hub.Booking;
using Hub.Mapping;
using Hub.Migrations.Postgres;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Hub.Tests;

/// <summary>
/// Fails whenever an entity or mapping changes without a matching migration, so the committed migrations can never
/// drift from the model the code uses. No database is needed: the comparison is between the model and the snapshot.
/// </summary>
public class MigrationsInSyncTests
{
    private const string NoServer = "Host=localhost;Database=never_connected;Username=x;Password=x";

    private static MappingDbContext NewContext()
    {
        var builder = new DbContextOptionsBuilder<MappingDbContext>();
        builder.ConfigureMapping(NoServer);
        return new MappingDbContext(builder.Options);
    }

    [Fact]
    public void The_mapping_model_has_no_changes_that_are_missing_from_the_migrations()
    {
        using var db = NewContext();

        db.Database.HasPendingModelChanges().ShouldBeFalse(
            "the model changed; add a migration with: dotnet ef migrations add <Name> --project src/Hub.Migrations.Postgres --startup-project src/Hub.Api --context MappingDbContext --output-dir Mapping");
    }

    [Fact]
    public void The_mapping_migrations_are_discovered_in_the_migrations_assembly()
    {
        using var db = NewContext();

        db.Database.GetMigrations().ShouldNotBeEmpty();
        db.Database.GetMigrations().First().ShouldEndWith("InitialMappingSchema");
    }

    private static BookingDbContext NewBookingContext()
    {
        var builder = new DbContextOptionsBuilder<BookingDbContext>();
        builder.ConfigureBooking(NoServer);
        return new BookingDbContext(builder.Options);
    }

    [Fact]
    public void The_booking_model_has_no_changes_that_are_missing_from_the_migrations()
    {
        using var db = NewBookingContext();

        db.Database.HasPendingModelChanges().ShouldBeFalse(
            "the model changed; add a migration with: dotnet ef migrations add <Name> --project src/Hub.Migrations.Postgres --startup-project src/Hub.Api --context BookingDbContext --output-dir Booking");
    }

    [Fact]
    public void The_booking_migrations_are_discovered_in_the_migrations_assembly()
    {
        using var db = NewBookingContext();

        db.Database.GetMigrations().First().ShouldEndWith("InitialBookingSchema");
    }

    [Fact]
    public void The_booking_schema_keeps_the_unique_idempotency_index_the_processor_relies_on()
    {
        using var db = NewBookingContext();

        var bookings = db.Model.FindEntityType(typeof(BookingRecord))!;
        bookings.GetIndexes().ShouldContain(i => i.IsUnique && i.Properties.Select(static p => p.Name).SequenceEqual(new[] { "Tenant", "IdempotencyKey" }));
    }

    private static CrosswalkDbContext NewCrosswalkContext()
    {
        var builder = new DbContextOptionsBuilder<CrosswalkDbContext>();
        builder.ConfigureCrosswalk(NoServer);
        return new CrosswalkDbContext(builder.Options);
    }

    [Fact]
    public void The_crosswalk_model_has_no_changes_that_are_missing_from_the_migrations()
    {
        using var db = NewCrosswalkContext();

        db.Database.HasPendingModelChanges().ShouldBeFalse(
            "the model changed; add a migration with: dotnet ef migrations add <Name> --project src/Hub.Migrations.Postgres --startup-project src/Hub.Api --context CrosswalkDbContext --output-dir Crosswalk");
        db.Database.GetMigrations().First().ShouldEndWith("InitialCrosswalkSchema");
    }

    [Fact]
    public void The_crosswalk_schema_keeps_both_unique_indexes_that_enforce_one_code_per_supplier_per_city()
    {
        using var db = NewCrosswalkContext();

        var entries = db.Model.FindEntityType(typeof(CrosswalkEntry))!;
        var unique = entries.GetIndexes().Where(static i => i.IsUnique).Select(static i => string.Join(',', i.Properties.Select(static p => p.Name))).ToList();
        unique.ShouldContain("Supplier,SupplierCityCode");
        unique.ShouldContain("Supplier,CanonicalCityId");
    }
}
