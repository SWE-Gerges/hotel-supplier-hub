using Hub.Abstractions;
using Hub.Core;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class RoomExtraMapperRegistryTests
{
    private sealed record WireRoom(string Code, string Text);

    private sealed class WireRoomMapper(SupplierId supplier) : IRoomExtraMapper<WireRoom>
    {
        public SupplierId Supplier => supplier;

        public Type WireRoomType => typeof(WireRoom);

        public void Map(WireRoom room, RoomExtraContext context, IDictionary<string, string> extra)
        {
            extra["code"] = room.Code;
            extra["hotel"] = context.HotelCode;
        }
    }

    [Fact]
    public void A_registered_mapper_fills_the_extras_of_its_supplier()
    {
        var registry = RoomExtraMapperRegistry.Of(new WireRoomMapper(TestData.A));

        var extra = registry.Map(new WireRoom("R1", "text"), new RoomExtraContext(TestData.A, "H9"));

        extra.ShouldBe(new Dictionary<string, string> { ["code"] = "R1", ["hotel"] = "H9" });
    }

    [Fact]
    public void A_supplier_without_a_mapper_contributes_no_extras()
    {
        var registry = RoomExtraMapperRegistry.Of(new WireRoomMapper(TestData.A));

        registry.Map(new WireRoom("R1", "text"), new RoomExtraContext(TestData.B, "H9")).ShouldBeEmpty();
        registry.Map("another wire type", new RoomExtraContext(TestData.A, "H9")).ShouldBeEmpty();
    }

    [Fact]
    public void Two_mappers_for_the_same_supplier_and_wire_type_are_refused()
    {
        Should.Throw<InvalidOperationException>(() => RoomExtraMapperRegistry.Of(new WireRoomMapper(TestData.A), new WireRoomMapper(TestData.A)));
    }

    [Fact]
    public void Each_supplier_can_have_its_own_mapper_for_the_same_wire_type()
    {
        var registry = RoomExtraMapperRegistry.Of(new WireRoomMapper(TestData.A), new WireRoomMapper(TestData.B));

        registry.Map(new WireRoom("R1", "t"), new RoomExtraContext(TestData.A, "x")).ShouldNotBeEmpty();
        registry.Map(new WireRoom("R1", "t"), new RoomExtraContext(TestData.B, "x")).ShouldNotBeEmpty();
    }
}
