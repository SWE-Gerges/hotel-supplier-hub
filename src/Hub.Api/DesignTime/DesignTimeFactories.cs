using Hub.Booking;
using Hub.Mapping;
using Hub.Migrations.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hub.Api;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build each context without starting the host. The tooling only looks for these
/// factories in the startup project, which is why they live here and not next to the migrations.
/// </summary>
public sealed class MappingDesignTimeFactory : IDesignTimeDbContextFactory<MappingDbContext>
{
    public MappingDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<MappingDbContext>();
        builder.ConfigureMapping("Host=localhost;Database=design_time_only;Username=design;Password=design");
        return new MappingDbContext(builder.Options);
    }
}

public sealed class CrosswalkDesignTimeFactory : IDesignTimeDbContextFactory<CrosswalkDbContext>
{
    public CrosswalkDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<CrosswalkDbContext>();
        builder.ConfigureCrosswalk("Host=localhost;Database=design_time_only;Username=design;Password=design");
        return new CrosswalkDbContext(builder.Options);
    }
}

public sealed class BookingDesignTimeFactory : IDesignTimeDbContextFactory<BookingDbContext>
{
    public BookingDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<BookingDbContext>();
        builder.ConfigureBooking("Host=localhost;Database=design_time_only;Username=design;Password=design");
        return new BookingDbContext(builder.Options);
    }
}
