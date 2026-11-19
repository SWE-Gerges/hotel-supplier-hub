using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hub.Booking;

/// <summary>Periodically settles pending and unknown bookings with their suppliers.</summary>
public sealed class BookingSyncService(
    BookingProcessor processor, IOptions<BookingOptions> options, TimeProvider time, ILogger<BookingSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SyncInterval, time);
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One sweep. Failures are logged and the next sweep tries again; they never stop the service.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settled = await processor.SweepAsync(cancellationToken).ConfigureAwait(false);
            if (settled.Count > 0)
            {
                logger.LogDebug("Booking sync looked at {Count} bookings.", settled.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "A booking sync sweep failed; it will run again at the next interval.");
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// Cancels pay-later bookings that are still unpaid when their pay-by moment arrives, which is ahead of the moment the
/// supplier's free-cancellation window closes, so an unpaid booking never costs a cancellation penalty.
/// </summary>
public sealed class PayLaterExpiryService(
    BookingProcessor processor, IOptions<BookingOptions> options, TimeProvider time, ILogger<PayLaterExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.PayLaterSweepInterval, time);
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One sweep; returns how many unpaid bookings were cancelled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await processor.ExpireUnpaidAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "A pay-later expiry sweep failed; it will run again at the next interval.");
            return 0;
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
