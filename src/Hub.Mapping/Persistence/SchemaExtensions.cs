using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Hub.Mapping;

public static class SchemaExtensions
{
    /// <summary>
    /// Applies the pending migrations of <paramref name="db"/>. EF's own <c>Migrate</c> first reads the migrations history
    /// table without a lock and, on a database that has never been migrated, that read fails and is logged as an error
    /// even though nothing is wrong. Creating the (empty) history table first, with the script EF itself would use, makes
    /// the read succeed, so a first start leaves no error-level line behind.
    /// </summary>
    public static async Task ApplyMigrationsAsync(this DbContext db, CancellationToken cancellationToken)
    {
        var history = db.GetService<IHistoryRepository>();
        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
