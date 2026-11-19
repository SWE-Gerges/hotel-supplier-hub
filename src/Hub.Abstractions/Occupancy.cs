using System.Text.Json.Serialization;

namespace Hub.Abstractions;

/// <summary>Guests in one room: adult count plus the ages of any children.</summary>
public sealed record Occupancy
{
    [JsonConstructor]
    public Occupancy(int adults, IReadOnlyList<int> childAges)
    {
        Adults = adults;
        ChildAges = childAges;
    }

    public int Adults { get; init; }

    public IReadOnlyList<int> ChildAges { get; init; }

    public Occupancy(int adults)
        : this(adults, [])
    {
    }
}

/// <summary>A stay, check-in inclusive and check-out exclusive.</summary>
public readonly record struct DateRange(DateOnly CheckIn, DateOnly CheckOut)
{
    public int Nights => CheckOut.DayNumber - CheckIn.DayNumber;
}
