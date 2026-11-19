using System.Text.Json;
using Hub.Booking;
using Hub.Core;
using Microsoft.EntityFrameworkCore;
using Hub.Mapping;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hub.Api;

internal sealed class SupplierRegistryHealthCheck(ISupplierRegistry registry) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(registry.Suppliers.Count > 0
            ? HealthCheckResult.Healthy($"{registry.Suppliers.Count} suppliers registered.")
            : HealthCheckResult.Unhealthy("No suppliers are registered."));
}

internal sealed class IdentityStoreHealthCheck(IHotelIdentityStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await store.PingAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The exception is logged by the health check service; the response only says that the store is down.
            return HealthCheckResult.Unhealthy("The identity store cannot be reached.", ex);
        }
    }
}

internal sealed class BookingDatabaseHealthCheck(IDbContextFactory<BookingDbContext> contextFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The booking database cannot be reached.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The booking database cannot be reached.", ex);
        }
    }
}

internal sealed class CrosswalkDatabaseHealthCheck(IDbContextFactory<CrosswalkDbContext> contextFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The crosswalk database cannot be reached.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The crosswalk database cannot be reached.", ex);
        }
    }
}

internal static class HealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(static e => new { name = e.Key, status = e.Value.Status.ToString(), description = e.Value.Description }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
