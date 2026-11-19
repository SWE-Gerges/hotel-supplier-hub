using Hub.Abstractions;

namespace Hub.Suppliers.Fakes;

/// <summary>How a fake supplier misbehaves. All rates are probabilities in [0, 1] applied per supplier call.</summary>
public sealed record FaultProfile
{
    public int MinLatencyMs { get; init; }

    public int MaxLatencyMs { get; init; }

    /// <summary>Share of calls that take <see cref="TailLatencyMs"/> instead of the normal range.</summary>
    public double TailRate { get; init; }

    public int TailLatencyMs { get; init; }

    public double ErrorRate { get; init; }

    /// <summary>The failure produced when an error is injected.</summary>
    public SupplierErrorKind ErrorKind { get; init; } = SupplierErrorKind.Transient;

    /// <summary>Share of calls whose payload is corrupted so the supplier's parser rejects it.</summary>
    public double MalformedRate { get; init; }

    /// <summary>Share of calls that never answer until cancelled.</summary>
    public double HangRate { get; init; }

    /// <summary>
    /// How much dearer (positive) or cheaper (negative) the supplier's price is when asked again after the search, as a
    /// percentage. Search results are unaffected; availability, prebook and booking checks see the moved price.
    /// </summary>
    public double PriceDriftPercent { get; init; }

    /// <summary>Share of bookings the supplier records and then answers with a timeout, so the client cannot tell they went through.</summary>
    public double AcceptedButLostRate { get; init; }

    /// <summary>Share of accepted asynchronous bookings that end up failed instead of confirmed.</summary>
    public double BookingFailRate { get; init; }

    public static FaultProfile None { get; } = new();

    public IEnumerable<string> Validate()
    {
        foreach (var (name, value) in new[]
                 {
                     (nameof(TailRate), TailRate), (nameof(ErrorRate), ErrorRate),
                     (nameof(MalformedRate), MalformedRate), (nameof(HangRate), HangRate),
                     (nameof(AcceptedButLostRate), AcceptedButLostRate), (nameof(BookingFailRate), BookingFailRate),
                 })
        {
            if (value is < 0 or > 1 || double.IsNaN(value))
            {
                yield return $"{name} must be between 0 and 1.";
            }
        }

        if (MinLatencyMs < 0 || MaxLatencyMs < MinLatencyMs || MaxLatencyMs > 120_000)
        {
            yield return "Latency needs 0 <= MinLatencyMs <= MaxLatencyMs <= 120000.";
        }

        if (TailLatencyMs is < 0 or > 120_000)
        {
            yield return "TailLatencyMs must be between 0 and 120000.";
        }

        if (PriceDriftPercent is < -90 or > 500 || double.IsNaN(PriceDriftPercent))
        {
            yield return "PriceDriftPercent must be between -90 and 500.";
        }

        if (!Enum.IsDefined(ErrorKind))
        {
            yield return "ErrorKind is not a known error kind.";
        }
    }
}

/// <summary>What one supplier call should do, decided up front from the current profile.</summary>
public readonly record struct FaultDecision(TimeSpan Latency, bool Hang, SupplierErrorKind? Error, bool Malformed);

/// <summary>Holds a fake supplier's current fault profile and rolls the dice for each call.</summary>
public sealed class FaultController
{
    private readonly Lock _gate = new();
    private readonly Random _random;
    private volatile FaultProfile _profile;

    public FaultController(FaultProfile initial, int seed)
    {
        _profile = initial;
        _random = new Random(seed);
    }

    public FaultProfile Current => _profile;

    public void Set(FaultProfile profile)
    {
        var errors = profile.Validate().ToArray();
        if (errors.Length > 0)
        {
            throw new ArgumentException(string.Join(' ', errors), nameof(profile));
        }

        _profile = profile;
    }

    public FaultDecision Next()
    {
        var profile = _profile;
        lock (_gate)
        {
            var hang = Roll(profile.HangRate);
            var latencyMs = Roll(profile.TailRate) && profile.TailLatencyMs > 0
                ? profile.TailLatencyMs
                : _random.Next(profile.MinLatencyMs, profile.MaxLatencyMs + 1);
            SupplierErrorKind? error = Roll(profile.ErrorRate) ? profile.ErrorKind : null;
            return new FaultDecision(TimeSpan.FromMilliseconds(latencyMs), hang, error, Roll(profile.MalformedRate));
        }
    }

    /// <summary>Decides whether this booking is recorded by the supplier but its answer is lost.</summary>
    public bool RollAcceptedButLost()
    {
        var probability = _profile.AcceptedButLostRate;
        lock (_gate)
        {
            return Roll(probability);
        }
    }

    /// <summary>Decides whether an accepted asynchronous booking will end up failed.</summary>
    public bool RollBookingFails()
    {
        var probability = _profile.BookingFailRate;
        lock (_gate)
        {
            return Roll(probability);
        }
    }

    private bool Roll(double probability) => probability > 0 && (probability >= 1 || _random.NextDouble() < probability);
}

/// <summary>The fault controllers of all fake suppliers, so an admin endpoint can reach them by supplier id.</summary>
public sealed class FakeSupplierFaults
{
    private readonly Dictionary<SupplierId, FaultController> _controllers;

    public FakeSupplierFaults(IReadOnlyDictionary<SupplierId, FaultController> controllers) =>
        _controllers = new Dictionary<SupplierId, FaultController>(controllers);

    public IReadOnlyCollection<SupplierId> Suppliers => _controllers.Keys;

    public FaultController? Get(SupplierId supplier) => _controllers.GetValueOrDefault(supplier);

    internal FaultController Require(SupplierId supplier) =>
        _controllers.TryGetValue(supplier, out var controller)
            ? controller
            : throw new InvalidOperationException($"No fault controller for '{supplier}'.");
}
