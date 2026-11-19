using Hub.Abstractions;
using Microsoft.Extensions.Options;

namespace Hub.Suppliers.Fakes;

public static class FakeSupplierIds
{
    public const string SupplierAValue = "supplier-a";
    public const string SupplierBValue = "supplier-b";
    public const string SupplierCValue = "supplier-c";

    public static SupplierId SupplierA { get; } = SupplierId.Parse(SupplierAValue);

    public static SupplierId SupplierB { get; } = SupplierId.Parse(SupplierBValue);

    public static SupplierId SupplierC { get; } = SupplierId.Parse(SupplierCValue);
}

public abstract class FakeSupplierOptionsBase
{
    /// <summary>Percentage of the physical hotels this supplier lists.</summary>
    public int Coverage { get; set; } = 90;

    /// <summary>Faults applied from startup; they can be changed at runtime through the admin endpoint.</summary>
    public FaultProfile Faults { get; set; } = new();
}

public sealed class SupplierAOptions : FakeSupplierOptionsBase
{
    /// <summary>Hotel codes requested per rates call.</summary>
    public int PageSize { get; set; } = 8;
}

/// <summary>Whether a fake supplier sends localised names and addresses for its hotels, and whether they are right.</summary>
public enum FakeOverlayMode
{
    /// <summary>No overlays.</summary>
    Off,

    /// <summary>A localised name and address for the same hotel, a few metres from its coordinates.</summary>
    Correct,

    /// <summary>The localised name, address and coordinates of a different hotel.</summary>
    Wrong,
}

public sealed class SupplierBOptions : FakeSupplierOptionsBase
{
    public FakeOverlayMode Overlay { get; set; } = FakeOverlayMode.Off;

    public SupplierBOptions() => Coverage = 80;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>Hard cap on how long the client keeps polling before giving up.</summary>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Hotels per result batch on the supplier side.</summary>
    public int BatchSize { get; set; } = 6;

    /// <summary>How much later each successive batch becomes available.</summary>
    public TimeSpan BatchDelay { get; set; } = TimeSpan.FromMilliseconds(15);

    /// <summary>How long an accepted booking stays pending before it is confirmed (or failed).</summary>
    public TimeSpan BookingConfirmDelay { get; set; } = TimeSpan.FromMilliseconds(150);
}

public sealed class SupplierCOptions : FakeSupplierOptionsBase
{
    public SupplierCOptions() => Coverage = 70;

    /// <summary>How long a session stays valid after the availability call.</summary>
    public TimeSpan SessionTtl { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Hotels whose rooms are fetched concurrently before a batch is emitted.</summary>
    public int RoomsBatchSize { get; set; } = 6;
}

public sealed class FakeSuppliersOptions
{
    public const string SectionName = "Hub:FakeSuppliers";

    public int Seed { get; set; } = 42;

    public int HotelsPerCity { get; set; } = 24;

    public SupplierAOptions SupplierA { get; set; } = new();

    public SupplierBOptions SupplierB { get; set; } = new();

    public SupplierCOptions SupplierC { get; set; } = new();
}

internal sealed class FakeSuppliersOptionsValidator : IValidateOptions<FakeSuppliersOptions>
{
    public ValidateOptionsResult Validate(string? name, FakeSuppliersOptions options)
    {
        var failures = new List<string>();

        if (options.HotelsPerCity is < 1 or > 192)
        {
            failures.Add("HotelsPerCity must be between 1 and 192.");
        }

        foreach (var (supplier, o) in new (string, FakeSupplierOptionsBase)[]
                 {
                     (FakeSupplierIds.SupplierAValue, options.SupplierA),
                     (FakeSupplierIds.SupplierBValue, options.SupplierB),
                     (FakeSupplierIds.SupplierCValue, options.SupplierC),
                 })
        {
            if (o.Coverage is < 1 or > 100)
            {
                failures.Add($"{supplier}: Coverage must be between 1 and 100.");
            }

            failures.AddRange(o.Faults.Validate().Select(error => $"{supplier}: {error}"));
        }

        if (options.SupplierA.PageSize < 1)
        {
            failures.Add("supplier-a: PageSize must be at least 1.");
        }

        if (options.SupplierB.PollInterval <= TimeSpan.Zero || options.SupplierB.MaxWait <= options.SupplierB.PollInterval)
        {
            failures.Add("supplier-b: PollInterval must be positive and smaller than MaxWait.");
        }

        if (options.SupplierB.BatchSize < 1 || options.SupplierB.BatchDelay < TimeSpan.Zero)
        {
            failures.Add("supplier-b: BatchSize must be at least 1 and BatchDelay cannot be negative.");
        }

        if (options.SupplierC.SessionTtl <= TimeSpan.Zero || options.SupplierC.RoomsBatchSize < 1)
        {
            failures.Add("supplier-c: SessionTtl must be positive and RoomsBatchSize at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
