using Hub.Abstractions;

namespace Hub.Core;

/// <summary>Finds the room-extra mapper of a supplier by the wire room type its normaliser reads.</summary>
public sealed class RoomExtraMapperRegistry
{
    private readonly Dictionary<(SupplierId Supplier, Type WireRoom), IRoomExtraMapper> _mappers;

    public RoomExtraMapperRegistry(IEnumerable<IRoomExtraMapper> mappers)
    {
        _mappers = [];
        foreach (var mapper in mappers)
        {
            if (!_mappers.TryAdd((mapper.Supplier, mapper.WireRoomType), mapper))
            {
                throw new InvalidOperationException($"Supplier '{mapper.Supplier}' has two room-extra mappers for {mapper.WireRoomType.Name}.");
            }
        }
    }

    public static RoomExtraMapperRegistry Of(params IRoomExtraMapper[] mappers) => new(mappers);

    /// <summary>The extras for one wire room; empty when the supplier registered no mapper for that type.</summary>
    public IReadOnlyDictionary<string, string> Map<TWireRoom>(TWireRoom room, RoomExtraContext context)
    {
        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_mappers.TryGetValue((context.Supplier, typeof(TWireRoom)), out var found))
        {
            ((IRoomExtraMapper<TWireRoom>)found).Map(room, context, extra);
        }

        return extra;
    }
}
