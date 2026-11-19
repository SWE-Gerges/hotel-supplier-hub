using System.Diagnostics.CodeAnalysis;
using Hub.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Hub.Core;

/// <summary>A supplier known to the hub: its descriptor plus whichever capability ports it implements.</summary>
public sealed record SupplierEntry(
    ISupplierDescriptor Descriptor,
    IHotelSearch? Search,
    IRoomAvailability? Availability = null,
    IPreBook? PreBook = null,
    IBook? Book = null,
    ICancel? Cancel = null,
    IRetrieve? Retrieve = null)
{
    public SupplierId Id => Descriptor.SupplierId;
}

public interface ISupplierRegistry
{
    IReadOnlyList<SupplierEntry> Suppliers { get; }

    bool TryGet(SupplierId id, [NotNullWhen(true)] out SupplierEntry? entry);
}

/// <summary>A fixed registry, for hosting suppliers that are not wired through keyed DI.</summary>
public sealed class StaticSupplierRegistry : ISupplierRegistry
{
    private readonly Dictionary<SupplierId, SupplierEntry> _byId;

    public StaticSupplierRegistry(IEnumerable<SupplierEntry> suppliers)
    {
        Suppliers = suppliers.ToArray();
        _byId = Suppliers.ToDictionary(static s => s.Id);
    }

    public IReadOnlyList<SupplierEntry> Suppliers { get; }

    public bool TryGet(SupplierId id, [NotNullWhen(true)] out SupplierEntry? entry) => _byId.TryGetValue(id, out entry);
}

/// <summary>Marks a supplier id as registered; the registry resolves the keyed services behind it.</summary>
internal sealed record SupplierRegistration(SupplierId Id);

/// <summary>Resolves suppliers registered with <c>AddHotelSupplier</c> from keyed DI.</summary>
internal sealed class KeyedSupplierRegistry : ISupplierRegistry
{
    private readonly StaticSupplierRegistry _inner;

    public KeyedSupplierRegistry(IServiceProvider services, IEnumerable<SupplierRegistration> registrations)
    {
        var entries = new List<SupplierEntry>();
        foreach (var registration in registrations)
        {
            var descriptor = services.GetRequiredKeyedService<ISupplierDescriptor>(registration.Id);
            if (descriptor.SupplierId != registration.Id)
            {
                throw new InvalidOperationException(
                    $"Supplier registered as '{registration.Id}' reports its id as '{descriptor.SupplierId}'.");
            }

            entries.Add(new SupplierEntry(
                descriptor,
                services.GetKeyedService<IHotelSearch>(registration.Id),
                services.GetKeyedService<IRoomAvailability>(registration.Id),
                services.GetKeyedService<IPreBook>(registration.Id),
                services.GetKeyedService<IBook>(registration.Id),
                services.GetKeyedService<ICancel>(registration.Id),
                services.GetKeyedService<IRetrieve>(registration.Id)));
        }

        _inner = new StaticSupplierRegistry(entries);
    }

    public IReadOnlyList<SupplierEntry> Suppliers => _inner.Suppliers;

    public bool TryGet(SupplierId id, [NotNullWhen(true)] out SupplierEntry? entry) => _inner.TryGet(id, out entry);
}
