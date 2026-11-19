using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Hub.Booking;

public static class SchemaExtensions
{
    /// <summary>
    /// Applies the pending migrations. The migrations history table is created first with the script EF itself would use,
    /// so that EF's unlocked read of it on a database that was never migrated succeeds instead of logging an error.
    /// </summary>
    public static async Task ApplyBookingMigrationsAsync(this DbContext db, CancellationToken cancellationToken)
    {
        var history = db.GetService<IHistoryRepository>();
        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
