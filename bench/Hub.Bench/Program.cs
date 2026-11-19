using System.Diagnostics;
using System.Globalization;
using System.Text;
using Hub.Abstractions;
using Hub.Core;
using Hub.Mapping;
using Hub.Suppliers.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Compares asking the three fake suppliers one after another with the hub's fan-out, under injected latency.
// Usage: dotnet run -c Release --project bench/Hub.Bench -- [--iterations 20] [--warmup 3] [--out bench/RESULTS.md]
var iterations = IntArg("--iterations", 20);
var warmup = IntArg("--warmup", 3);
var outPath = Arg("--out");

var repoRoot = FindRepoRoot();
var configuration = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(repoRoot, "src", "Hub.Api", "appsettings.json"), optional: false)
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Hub:Resilience:Defaults:RetryBaseDelay"] = "00:00:00.010",
        ["Hub:Search:GlobalDeadline"] = "00:00:30",
    })
    .Build();

var services = new ServiceCollection();
services.AddLogging(static b => b.SetMinimumLevel(LogLevel.Warning));
services.AddHubCore(configuration);
services.AddHotelMapping(configuration);
services.AddCrosswalk(configuration);
services.AddInMemoryIdentityStore();
services.AddFakeSuppliers(configuration);
var crosswalkPath = Path.Combine(Path.GetTempPath(), $"hub-bench-crosswalk-{Guid.NewGuid():N}.db");
services.AddCrosswalkDatabase(options => options.UseSqlite($"Data Source={crosswalkPath};Pooling=False"), useMigrations: false);
await using var provider = services.BuildServiceProvider();
await using (var db = await provider.GetRequiredService<IDbContextFactory<CrosswalkDbContext>>().CreateDbContextAsync())
{
    await db.Database.EnsureCreatedAsync();
}

await provider.GetRequiredService<CrosswalkService>().RunAsync(default);

var orchestrator = provider.GetRequiredService<SearchOrchestrator>();
var gateway = provider.GetRequiredService<ISupplierGateway>();
var cities = provider.GetRequiredService<ICityCodeResolver>();
var selector = provider.GetRequiredService<ITenantSupplierSelector>();
var faults = provider.GetRequiredService<FakeSupplierFaults>();

var request = new HotelSearchRequest
{
    Destination = "city-aldermoor",
    CheckIn = new DateOnly(2027, 3, 1),
    CheckOut = new DateOnly(2027, 3, 4),
    Rooms = [new Occupancy(2, [])],
    Nationality = "XA",
    Currency = "EUR",
};
var owner = new SearchOwner("tenant-demo", "bench");
var suppliers = selector.Select(owner.Tenant, null);

var scenarios = new (string Name, int MinMs, int MaxMs)[]
{
    ("no injected latency", 0, 0),
    ("40-80 ms per supplier call", 40, 80),
    ("100-200 ms per supplier call", 100, 200),
};

var report = new StringBuilder();
report.AppendLine("# Benchmark results");
report.AppendLine();
report.AppendLine("Produced by `dotnet run -c Release --project bench/Hub.Bench`. The three fake suppliers run in the same process, so the numbers show");
report.AppendLine("how latency adds up when suppliers are asked one after another and how much of it fan-out hides. They say nothing about real suppliers.");
report.AppendLine();
report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- Run: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC, {iterations} measured iterations after {warmup} warm-up iterations per scenario."));
report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- Machine: {Environment.ProcessorCount} logical processors, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}."));
report.AppendLine("- Search: one city, 3 nights, 1 room with 2 adults. Sequential asks supplier-a, then supplier-b, then supplier-c through the same resilience gateway and does nothing with the answers. Fan-out is the whole orchestrator: it also resolves city codes, prices every offer, normalises every cancellation policy, stores the offers and merges the hotels.");
report.AppendLine("- Latency is injected on every supplier call (a search makes several calls per supplier), uniformly between the two bounds.");
report.AppendLine("- Results depend on the machine and on what else was running; treat them as one sample, not a guarantee.");
report.AppendLine();
report.AppendLine("| Scenario | Sequential median (ms) | Sequential p95 (ms) | Fan-out median (ms) | Fan-out p95 (ms) | Median speed-up |");
report.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");

foreach (var (name, minMs, maxMs) in scenarios)
{
    foreach (var id in faults.Suppliers)
    {
        faults.Get(id)!.Set(new FaultProfile { MinLatencyMs = minMs, MaxLatencyMs = maxMs });
    }

    var sequential = new List<double>();
    var fanOut = new List<double>();
    for (var i = 0; i < warmup + iterations; i++)
    {
        var seq = await RunSequentialAsync();
        var fan = await RunFanOutAsync();
        if (i >= warmup)
        {
            sequential.Add(seq);
            fanOut.Add(fan);
        }
    }

    var seqMedian = Percentile(sequential, 0.5);
    var fanMedian = Percentile(fanOut, 0.5);
    var row = string.Create(
        CultureInfo.InvariantCulture,
        $"| {name} | {seqMedian:0} | {Percentile(sequential, 0.95):0} | {fanMedian:0} | {Percentile(fanOut, 0.95):0} | {seqMedian / Math.Max(fanMedian, 0.001):0.0}x |");
    report.AppendLine(row);
    Console.WriteLine(row);
}

report.AppendLine();
report.AppendLine("Reading the table: with no injected latency the fan-out row is the slower one, because it does all of the hub's own work on the");
report.AppendLine("answers (pricing, policy normalisation, merging) while the sequential row only waits for the suppliers; supplier-b alone also needs");
report.AppendLine("about 60 ms by itself (its configured poll interval and batch delay). With latency on every call the sequential path pays for all");
report.AppendLine("three suppliers one after another, while fan-out pays about as much as the slowest supplier plus the hub's own work.");

if (outPath is not null)
{
    var target = Path.IsPathRooted(outPath) ? outPath : Path.Combine(repoRoot, outPath);
    await File.WriteAllTextAsync(target, report.ToString());
    Console.WriteLine($"Wrote {target}");
}

File.Delete(crosswalkPath);
return 0;

async Task<double> RunSequentialAsync()
{
    var clock = Stopwatch.StartNew();
    foreach (var supplier in suppliers)
    {
        var codes = await cities.ResolveAsync(request.Destination, supplier, CancellationToken.None) ?? throw new InvalidOperationException($"No city codes for {supplier}.");
        var result = await gateway.SearchAsync(supplier, new SupplierSearchQuery(request, codes), static (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"{supplier} failed: {result.Error.Message}");
        }
    }

    return clock.Elapsed.TotalMilliseconds;
}

async Task<double> RunFanOutAsync()
{
    var clock = Stopwatch.StartNew();
    var result = await orchestrator.SearchToCompletionAsync(request, owner);
    if (result.Summary.Status != SearchStatus.Completed)
    {
        throw new InvalidOperationException($"The search ended as {result.Summary.Status}.");
    }

    return clock.Elapsed.TotalMilliseconds;
}

static double Percentile(List<double> values, double p)
{
    var sorted = values.Order().ToArray();
    var index = (int)Math.Ceiling(p * sorted.Length) - 1;
    return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "HotelSupplierHub.slnx")))
        {
            return dir.FullName;
        }
    }

    throw new InvalidOperationException("Run the benchmark from inside the repository.");
}

int IntArg(string name, int fallback) => Arg(name) is { } text && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : fallback;

string? Arg(string name)
{
    var args = Environment.GetCommandLineArgs();
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
