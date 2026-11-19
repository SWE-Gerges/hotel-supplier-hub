using Microsoft.EntityFrameworkCore;

namespace Hub.Booking;

public interface IQuoteStore
{
    /// <summary>Stores the new quote and marks every other active quote of the same offer as superseded, in one step.</summary>
    Task AddAsync(QuoteRecord quote, CancellationToken cancellationToken);

    Task<QuoteRecord?> GetAsync(string tenant, string quoteId, CancellationToken cancellationToken);
}

public enum ClaimOutcome
{
    Claimed,

    /// <summary>A booking with the same tenant and idempotency key already exists.</summary>
    DuplicateKey,

    /// <summary>The quote is missing, expired, superseded or already used.</summary>
    QuoteNotLive,
}

public interface IBookingStore
{
    /// <summary>
    /// Claims the idempotency key and consumes the quote in one transaction: the booking row is inserted (the unique
    /// (tenant, key) index decides a race) and the quote is moved from active to used, which only one booking can do.
    /// </summary>
    Task<ClaimOutcome> ClaimAsync(BookingRecord booking, DateTimeOffset now, CancellationToken cancellationToken);

    Task<BookingRecord?> GetAsync(string tenant, string id, CancellationToken cancellationToken);

    Task<BookingRecord?> GetByKeyAsync(string tenant, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Saves a changed booking. Throws <see cref="DbUpdateConcurrencyException"/> if someone else changed it first.</summary>
    Task UpdateAsync(BookingRecord booking, CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingRecord>> ListByStatusAsync(
        IReadOnlyCollection<HubBookingStatus> statuses, DateTimeOffset updatedBefore, int take, CancellationToken cancellationToken);

    /// <summary>Confirmed pay-later bookings that are still unpaid and whose pay-by moment has passed.</summary>
    Task<IReadOnlyList<BookingRecord>> ListPayLaterDueAsync(DateTimeOffset now, int take, CancellationToken cancellationToken);
}

internal sealed class EfQuoteStore(IDbContextFactory<BookingDbContext> contextFactory) : IQuoteStore
{
    public async Task AddAsync(QuoteRecord quote, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.Quotes.Add(quote);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await db.Quotes
            .Where(q => q.Tenant == quote.Tenant && q.OfferId == quote.OfferId && q.Status == QuoteStatus.Active && q.Id != quote.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, QuoteStatus.Superseded), cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<QuoteRecord?> GetAsync(string tenant, string quoteId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Quotes.AsNoTracking().FirstOrDefaultAsync(q => q.Tenant == tenant && q.Id == quoteId, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class EfBookingStore(IDbContextFactory<BookingDbContext> contextFactory) : IBookingStore
{
    public async Task<ClaimOutcome> ClaimAsync(BookingRecord booking, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.Bookings.Add(booking);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            if (await KeyExistsAsync(booking.Tenant, booking.IdempotencyKey, cancellationToken).ConfigureAwait(false))
            {
                return ClaimOutcome.DuplicateKey;
            }

            throw;
        }

        var consumed = await db.Quotes
            .Where(q => q.Tenant == booking.Tenant && q.Id == booking.QuoteId && q.Status == QuoteStatus.Active && q.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, QuoteStatus.Used), cancellationToken)
            .ConfigureAwait(false);
        if (consumed == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ClaimOutcome.QuoteNotLive;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ClaimOutcome.Claimed;
    }

    public async Task<BookingRecord?> GetAsync(string tenant, string id, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Tenant == tenant && b.Id == id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BookingRecord?> GetByKeyAsync(string tenant, string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Bookings.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Tenant == tenant && b.IdempotencyKey == idempotencyKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(BookingRecord booking, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var expected = booking.Version;
        booking.Version = expected + 1;
        db.Bookings.Attach(booking);
        db.Entry(booking).State = EntityState.Modified;
        db.Entry(booking).Property(b => b.Version).OriginalValue = expected;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            booking.Version = expected;
            throw;
        }
    }

    public async Task<IReadOnlyList<BookingRecord>> ListByStatusAsync(
        IReadOnlyCollection<HubBookingStatus> statuses, DateTimeOffset updatedBefore, int take, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Bookings.AsNoTracking()
            .Where(b => statuses.Contains(b.Status) && b.UpdatedAt <= updatedBefore)
            .OrderBy(b => b.UpdatedAt)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BookingRecord>> ListPayLaterDueAsync(DateTimeOffset now, int take, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Bookings.AsNoTracking()
            .Where(b => b.PaymentMode == PaymentMode.PayLater && b.Status == HubBookingStatus.Confirmed && b.PaidAt == null && b.PayBy != null && b.PayBy <= now)
            .OrderBy(b => b.PayBy)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> KeyExistsAsync(string tenant, string key, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Bookings.AnyAsync(b => b.Tenant == tenant && b.IdempotencyKey == key, cancellationToken).ConfigureAwait(false);
    }
}
