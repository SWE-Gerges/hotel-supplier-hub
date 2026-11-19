using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hub.Migrations.Postgres;

/// <summary>
/// The PostgreSQL provider settings and the committed migrations of every hub database context. Keeping them in one place
/// means the host, the design-time tooling and the tests all configure a context the same way.
/// </summary>
public static class PostgresMigrations
{
    public const string MappingHistoryTable = "__ef_migrations_mapping";

    public const string CrosswalkHistoryTable = "__ef_migrations_crosswalk";

    public static DbContextOptionsBuilder ConfigureCrosswalk(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, npgsql => npgsql
            .MigrationsAssembly(typeof(PostgresMigrations).Assembly.GetName().Name)
            .MigrationsHistoryTable(CrosswalkHistoryTable));

    public const string BookingHistoryTable = "__ef_migrations_booking";

    public static DbContextOptionsBuilder ConfigureBooking(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, npgsql => npgsql
            .MigrationsAssembly(typeof(PostgresMigrations).Assembly.GetName().Name)
            .MigrationsHistoryTable(BookingHistoryTable));

    public static DbContextOptionsBuilder ConfigureMapping(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, npgsql => npgsql
            .MigrationsAssembly(typeof(PostgresMigrations).Assembly.GetName().Name)
            .MigrationsHistoryTable(MappingHistoryTable));

    /// <summary>
    /// Creates the database named in the connection string when the server does not have it yet. Without this, EF finds
    /// out that the database is missing by failing to connect and logs that failure as an error before creating it.
    /// Asking the server first keeps a normal first start free of error-level log lines.
    /// </summary>
    public static async Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken cancellationToken)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var name = target.Database ?? throw new InvalidOperationException("The connection string names no database.");
        var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };

        await using var connection = new NpgsqlConnection(maintenance.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", name);
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"", connection);
        try
        {
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateDatabase)
        {
            // Another instance created it between the check and the statement; that is the outcome we wanted.
        }
    }
}
