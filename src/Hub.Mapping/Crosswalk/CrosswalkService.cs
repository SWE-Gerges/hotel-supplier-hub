using System.Text.Json;
using Hub.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hub.Mapping;

public sealed record CrosswalkSupplierRun(string Supplier, bool Succeeded, string? Error, int Listed, int AlreadyMapped, int Accepted, int Reviewed, int Conflicts, int Unchanged);

public sealed record CrosswalkRunSummary(IReadOnlyList<CrosswalkSupplierRun> Suppliers)
{
    public int Accepted => Suppliers.Sum(static s => s.Accepted);

    public int Reviewed => Suppliers.Sum(static s => s.Reviewed);
}

public enum ReviewResolution
{
    Done,
    NotFound,
    NotPending,
    UnknownCity,
    Conflict,
}

public sealed record ReviewResult(ReviewResolution Resolution, CrosswalkReview? Review, string? Detail);

/// <summary>
/// Keeps the crosswalk: runs the matcher over every supplier's city list, stores what it accepts, queues the rest for review,
/// and applies a person's decision. A canonical city has at most one code per supplier, which the matcher enforces and the
/// database backs with a unique index.
/// </summary>
public sealed class CrosswalkService(
    ICrosswalkStore store,
    CrosswalkMatcher matcher,
    IEnumerable<CityDirectoryRegistration> directories,
    TimeProvider time,
    ILogger<CrosswalkService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _runs = new(1, 1);

    public async Task<CrosswalkRunSummary> RunAsync(CancellationToken cancellationToken)
    {
        await _runs.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<CrosswalkSupplierRun>();
            foreach (var registration in directories.OrderBy(static d => d.Supplier.Value, StringComparer.Ordinal))
            {
                results.Add(await RunSupplierAsync(registration, cancellationToken).ConfigureAwait(false));
            }

            return new CrosswalkRunSummary(results);
        }
        finally
        {
            _runs.Release();
        }
    }

    public Task<IReadOnlyList<CrosswalkReview>> GetReviewsAsync(ReviewStatus? status, string? supplier, CancellationToken cancellationToken) =>
        store.GetReviewsAsync(status, supplier, cancellationToken);

    public Task<IReadOnlyList<CrosswalkEntry>> GetEntriesAsync(string? supplier, CancellationToken cancellationToken) =>
        store.GetEntriesAsync(supplier, cancellationToken);

    /// <summary>Maps the review's supplier city to <paramref name="canonicalCityId"/> (or to the proposal when none is given).</summary>
    public async Task<ReviewResult> ApproveAsync(long id, string? canonicalCityId, CancellationToken cancellationToken)
    {
        if (await store.FindReviewAsync(id, cancellationToken).ConfigureAwait(false) is not { } review)
        {
            return new ReviewResult(ReviewResolution.NotFound, null, null);
        }

        if (review.Status != ReviewStatus.Pending)
        {
            return new ReviewResult(ReviewResolution.NotPending, review, $"The review is already {review.Status.ToString().ToLowerInvariant()}.");
        }

        var target = canonicalCityId ?? review.ProposedCityId;
        if (target is null || matcher.CanonicalCities.All(c => c.Id != target))
        {
            return new ReviewResult(ReviewResolution.UnknownCity, review, target is null ? "The review has no proposal; name the canonical city." : $"'{target}' is not a canonical city.");
        }

        var outcome = await store.TryAddEntryAsync(
            new CrosswalkEntry
            {
                Supplier = review.Supplier,
                SupplierCityCode = review.SupplierCityCode,
                SupplierCountryCode = review.SupplierCountryCode,
                CanonicalCityId = target,
                Score = target == review.ProposedCityId ? review.Score : 0,
                Source = "approved",
                CreatedAt = time.GetUtcNow(),
            },
            cancellationToken).ConfigureAwait(false);
        if (outcome != AddEntryOutcome.Added)
        {
            return new ReviewResult(
                ReviewResolution.Conflict, review,
                outcome == AddEntryOutcome.CodeTaken
                    ? $"Code '{review.SupplierCityCode}' of {review.Supplier} already means a canonical city."
                    : $"{review.Supplier} already has a different code for '{target}'.");
        }

        review.Status = ReviewStatus.Approved;
        review.ResolvedCityId = target;
        review.ResolvedAt = time.GetUtcNow();
        await store.UpdateReviewAsync(review, cancellationToken).ConfigureAwait(false);
        return new ReviewResult(ReviewResolution.Done, review, null);
    }

    public async Task<ReviewResult> RejectAsync(long id, CancellationToken cancellationToken)
    {
        if (await store.FindReviewAsync(id, cancellationToken).ConfigureAwait(false) is not { } review)
        {
            return new ReviewResult(ReviewResolution.NotFound, null, null);
        }

        if (review.Status != ReviewStatus.Pending)
        {
            return new ReviewResult(ReviewResolution.NotPending, review, $"The review is already {review.Status.ToString().ToLowerInvariant()}.");
        }

        review.Status = ReviewStatus.Rejected;
        review.ResolvedAt = time.GetUtcNow();
        await store.UpdateReviewAsync(review, cancellationToken).ConfigureAwait(false);
        return new ReviewResult(ReviewResolution.Done, review, null);
    }

    private async Task<CrosswalkSupplierRun> RunSupplierAsync(CityDirectoryRegistration registration, CancellationToken cancellationToken)
    {
        var supplier = registration.Supplier.Value;
        var listing = await registration.Directory.GetDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (listing.IsFailure)
        {
            logger.LogWarning("Crosswalk: {Supplier} could not list its cities: {Message}", supplier, listing.Error.Message);
            return new CrosswalkSupplierRun(supplier, false, listing.Error.Message, 0, 0, 0, 0, 0, 0);
        }

        var directory = listing.Value;
        var countryNames = directory.Countries.ToDictionary(static c => c.Code, static c => c.Name, StringComparer.Ordinal);
        var entries = await store.GetEntriesAsync(supplier, cancellationToken).ConfigureAwait(false);
        var mappedCodes = entries.Select(static e => e.SupplierCityCode).ToHashSet(StringComparer.Ordinal);
        var mappedCities = entries.ToDictionary(static e => e.CanonicalCityId, static e => e.SupplierCityCode, StringComparer.Ordinal);
        var knownReviews = (await store.GetReviewsAsync(null, supplier, cancellationToken).ConfigureAwait(false)).Select(static r => r.SupplierCityCode).ToHashSet(StringComparer.Ordinal);

        int alreadyMapped = 0, unchanged = 0, accepted = 0, reviewed = 0, conflicts = 0;
        var decided = new List<(SupplierCity City, CityMatch Match)>();
        foreach (var city in directory.Cities)
        {
            if (mappedCodes.Contains(city.Code))
            {
                alreadyMapped++;
            }
            else if (knownReviews.Contains(city.Code))
            {
                unchanged++; // pending, approved or rejected: a person's decision or queue position is not redone by a run
            }
            else
            {
                decided.Add((city, matcher.Match(city, countryNames.GetValueOrDefault(city.CountryCode))));
            }
        }

        async Task QueueAsync(SupplierCity city, CityMatch match, string reason)
        {
            var queued = await store.TryAddReviewAsync(
                new CrosswalkReview
                {
                    Supplier = supplier,
                    SupplierCityCode = city.Code,
                    SupplierCityName = city.Name,
                    SupplierCountryCode = city.CountryCode,
                    SupplierRegion = city.Region,
                    Status = ReviewStatus.Pending,
                    ProposedCityId = match.BestCityId,
                    Score = match.BestScore,
                    RunnerUpScore = match.RunnerUpScore,
                    Reason = reason,
                    CandidatesJson = JsonSerializer.Serialize(match.Candidates.Take(5), Json),
                    CreatedAt = time.GetUtcNow(),
                },
                cancellationToken).ConfigureAwait(false);
            if (queued)
            {
                reviewed++;
            }
            else
            {
                unchanged++;
            }
        }

        foreach (var group in decided.Where(static d => d.Match.Decision == MatchDecision.AutoAccept).GroupBy(static d => d.Match.BestCityId!, StringComparer.Ordinal))
        {
            var ranked = group.OrderByDescending(static d => d.Match.BestScore).ThenBy(static d => d.City.Code, StringComparer.Ordinal).ToList();
            var (winner, winnerMatch) = ranked[0];
            if (mappedCities.TryGetValue(group.Key, out var existingCode))
            {
                conflicts += ranked.Count;
                foreach (var (city, match) in ranked)
                {
                    await QueueAsync(city, match, $"{supplier} already uses code '{existingCode}' for '{group.Key}'; one code per supplier per city.").ConfigureAwait(false);
                }

                continue;
            }

            var added = await store.TryAddEntryAsync(
                new CrosswalkEntry
                {
                    Supplier = supplier,
                    SupplierCityCode = winner.Code,
                    SupplierCountryCode = winner.CountryCode,
                    CanonicalCityId = group.Key,
                    Score = winnerMatch.BestScore,
                    Source = "auto",
                    CreatedAt = time.GetUtcNow(),
                },
                cancellationToken).ConfigureAwait(false);
            if (added == AddEntryOutcome.Added)
            {
                accepted++;
                mappedCities[group.Key] = winner.Code;
            }
            else
            {
                conflicts++;
                await QueueAsync(winner, winnerMatch, "The database already holds a conflicting code for this city.").ConfigureAwait(false);
            }

            foreach (var (city, match) in ranked.Skip(1))
            {
                conflicts++;
                await QueueAsync(city, match, $"'{winner.Code}' ({winner.Name}) matches '{group.Key}' at least as well; one code per supplier per city.").ConfigureAwait(false);
            }
        }

        foreach (var (city, match) in decided.Where(static d => d.Match.Decision == MatchDecision.Review))
        {
            await QueueAsync(city, match, match.Reason).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Crosswalk {Supplier}: {Listed} cities, {Accepted} accepted, {Reviewed} queued for review, {Mapped} already mapped.",
            supplier, directory.Cities.Count, accepted, reviewed, alreadyMapped);
        return new CrosswalkSupplierRun(supplier, true, null, directory.Cities.Count, alreadyMapped, accepted, reviewed, conflicts, unchanged);
    }
}

/// <summary>Resolves a canonical city to a supplier's own codes through the crosswalk.</summary>
public sealed class CrosswalkCityCodeResolver(ICrosswalkStore store) : ICityCodeResolver
{
    public async ValueTask<SupplierCityCodes?> ResolveAsync(string canonicalCityId, SupplierId supplier, CancellationToken cancellationToken) =>
        await store.FindEntryAsync(supplier.Value, canonicalCityId, cancellationToken).ConfigureAwait(false) is { } entry
            ? new SupplierCityCodes(entry.SupplierCityCode, entry.SupplierCountryCode)
            : null;
}

internal sealed class CrosswalkSchemaInitializer(IDbContextFactory<CrosswalkDbContext> contextFactory, bool useMigrations) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (useMigrations)
        {
            await db.ApplyMigrationsAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Matches every supplier's cities when the host starts, so the first search already has codes to use.</summary>
internal sealed class CrosswalkStartupRun(CrosswalkService service, IOptions<CrosswalkOptions> options, ILogger<CrosswalkStartupRun> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.RunAtStartup)
        {
            return;
        }

        var summary = await service.RunAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Crosswalk run at start-up: {Accepted} accepted, {Reviewed} queued for review.", summary.Accepted, summary.Reviewed);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class CrosswalkServiceCollectionExtensions
{
    /// <summary>Registers the matcher, the service and the city-code resolver. Pair it with <see cref="AddCrosswalkDatabase"/>.</summary>
    public static IServiceCollection AddCrosswalk(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CrosswalkOptions>().Bind(configuration.GetSection(CrosswalkOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CrosswalkOptions>, CrosswalkOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<CrosswalkMatcher>();
        services.TryAddSingleton<ICrosswalkStore, EfCrosswalkStore>();
        services.TryAddSingleton<CrosswalkService>();
        services.TryAddSingleton<ICityCodeResolver, CrosswalkCityCodeResolver>();
        return services;
    }

    public static IServiceCollection AddCrosswalkDatabase(this IServiceCollection services, Action<DbContextOptionsBuilder> configure, bool useMigrations)
    {
        services.AddPooledDbContextFactory<CrosswalkDbContext>(configure);
        services.AddSingleton<IHostedService>(sp => new CrosswalkSchemaInitializer(sp.GetRequiredService<IDbContextFactory<CrosswalkDbContext>>(), useMigrations));
        services.AddSingleton<IHostedService, CrosswalkStartupRun>();
        return services;
    }
}
