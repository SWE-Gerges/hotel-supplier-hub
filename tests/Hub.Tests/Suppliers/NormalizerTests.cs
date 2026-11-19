using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Hub.Abstractions;
using Hub.Suppliers.Fakes;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class TolerantDecimalTests
{
    [Theory]
    [InlineData("123.45", 123.45)]
    [InlineData("12,30", 12.30)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1234", 1234)]
    [InlineData("0,5", 0.5)]
    [InlineData("12,345", 12345)]
    [InlineData("1.234.567", 1234567)]
    [InlineData(" 99.90 ", 99.90)]
    [InlineData("-5,25", -5.25)]
    [InlineData("1 234,50", 1234.50)]
    public void Parses_the_formats_suppliers_use(string text, double expected)
    {
        TolerantDecimal.TryParse(text, out var value).ShouldBeTrue();

        value.ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("n/a")]
    [InlineData("12,3x")]
    [InlineData("1,2,x")]
    public void Rejects_text_that_is_not_a_number(string? text)
    {
        TolerantDecimal.TryParse(text, out _).ShouldBeFalse();
    }
}

public class SupplierANormalizerTests
{
    private static readonly SupplierANormalizer Normalizer = new();

    private static Result<IReadOnlyList<Hotel>> Normalize(string json) =>
        Normalizer.Normalize(JsonSerializer.Deserialize<AHotelsResponse>(json, SupplierAJson.Options)!, TestData.Query());

    private static string Hotel(string rooms, string stars = "4*") =>
        $$"""
        {"hotels":[{"hotelCode":"300001","hotelName":"  Maple Central Hotel ","starRating":"{{stars}}",
          "geo":{"lat":12.5,"lng":31.5},"address":{"line1":"1 Test Street","city":"Aldermoor","countryCode":"XA"},
          "rooms":[{{rooms}}]}]}
        """;

    private static string Room(string net, string? taxes, string board = "bb", string policy = "Free cancellation until 2027-02-27.")
    {
        var taxesJson = taxes is null ? string.Empty : "\"taxes\":{\"amount\":\"" + taxes + "\",\"currency\":\"EUR\"},";
        return "{\"roomCode\":\"std\",\"roomDescription\":\"Standard Double Room\",\"boardCode\":\"" + board + "\","
               + "\"rate\":{\"net\":{\"amount\":\"" + net + "\",\"currency\":\"EUR\"}," + taxesJson
               + "\"cancellationPolicy\":\"" + policy + "\"}}";
    }

    [Fact]
    public void Adds_net_and_taxes_even_when_one_uses_a_decimal_comma()
    {
        var result = Normalize(Hotel(Room("100.50", "10,05")));

        var hotel = result.Value.ShouldHaveSingleItem();
        hotel.Name.ShouldBe("Maple Central Hotel");
        hotel.Stars.ShouldBe(4);
        hotel.Location.ShouldBe(new GeoPoint(12.5, 31.5));
        hotel.Address.ShouldBe("1 Test Street, Aldermoor");
        var offer = hotel.Offers.ShouldHaveSingleItem();
        offer.Price.ShouldBe(new Money(110.55m, "EUR"));
        offer.SellingPrice.ShouldBe(offer.Price);
        offer.Board.ShouldBe(BoardType.Breakfast);
        offer.RawBoard.ShouldBe("bb");
        offer.Refundable.ShouldBeFalse("the hub's policy rules decide, not the supplier normaliser");
        offer.PolicyKnown.ShouldBeFalse();
        offer.Extra["cancellationText"].ShouldStartWith("Free cancellation");
        offer.Extra["roomCode"].ShouldBe("std");
        offer.Extra["boardCode"].ShouldBe("bb");
        offer.OfferId.ShouldBe("supplier-a.300001.std.bb.20270301.3");
    }

    [Fact]
    public void Taxes_are_optional()
    {
        var offer = Normalize(Hotel(Room("80.00", null))).Value.Single().Offers.Single();

        offer.Price.Amount.ShouldBe(80.00m);
    }

    [Fact]
    public void The_raw_policy_text_is_kept_for_the_policy_rules_whatever_it_says()
    {
        var offer = Normalize(Hotel(Room("80.00", null, policy: "Non-refundable."))).Value.Single().Offers.Single();

        offer.Refundable.ShouldBeFalse();
        offer.Extra["cancellationText"].ShouldBe("Non-refundable.");
    }

    [Fact]
    public void An_offer_with_an_unreadable_price_is_dropped_and_a_hotel_left_without_offers_disappears()
    {
        var result = Normalize(Hotel(Room("n/a", null)));

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
    }

    [Fact]
    public void An_unknown_board_code_is_kept_raw_and_normalised_to_Unknown()
    {
        var offer = Normalize(Hotel(Room("80.00", null, board: "XX"))).Value.Single().Offers.Single();

        offer.Board.ShouldBe(BoardType.Unknown);
        offer.RawBoard.ShouldBe("XX");
    }

    [Theory]
    [InlineData("RO", BoardType.RoomOnly)]
    [InlineData("bb", BoardType.Breakfast)]
    [InlineData(" HB ", BoardType.HalfBoard)]
    [InlineData("FB", BoardType.FullBoard)]
    [InlineData("AI", BoardType.AllInclusive)]
    [InlineData("", BoardType.Unknown)]
    [InlineData(null, BoardType.Unknown)]
    public void Maps_board_codes(string? code, BoardType expected) =>
        SupplierANormalizer.ParseBoard(code).ShouldBe(expected);

    [Theory]
    [InlineData("4", 4)]
    [InlineData("4*", 4)]
    [InlineData("5 stars", 5)]
    [InlineData("9", 5)]
    [InlineData("", 0)]
    [InlineData("n/a", 0)]
    public void Reads_star_ratings_tolerantly(string rating, int expected) =>
        SupplierANormalizer.ParseStars(rating).ShouldBe(expected);

    [Theory]
    [InlineData("SERVICE_BUSY", SupplierErrorKind.Transient, true)]
    [InlineData("TOO_MANY_REQUESTS", SupplierErrorKind.RateLimited, false)]
    [InlineData("SERVICE_DOWN", SupplierErrorKind.Unavailable, false)]
    [InlineData("INVALID_CREDENTIALS", SupplierErrorKind.Auth, false)]
    [InlineData("BAD_REQUEST", SupplierErrorKind.InvalidRequest, false)]
    [InlineData("UPSTREAM_TIMEOUT", SupplierErrorKind.Timeout, false)]
    [InlineData("SOMETHING_NEW", SupplierErrorKind.Unknown, false)]
    public void Maps_error_codes_and_only_retries_transient_ones(string code, SupplierErrorKind kind, bool retryable)
    {
        var result = Normalizer.Normalize(new AHotelsResponse(null, new AError(code, "message")), TestData.Query());

        result.Error.Kind.ShouldBe(kind);
        result.Error.Retryable.ShouldBe(retryable);
    }
}

public class SupplierBNormalizerTests
{
    private static readonly SupplierBNormalizer Normalizer = new();

    private static Result<IReadOnlyList<Hotel>> Normalize(string json) =>
        Normalizer.Normalize(JsonSerializer.Deserialize<BBatch>(json, SupplierBJson.Options)!, TestData.Query());

    private const string Sample =
        """
        {"batch_id":2,"hotels":[{"hotel_id":"BX-1","name":"HOTEL MAPLE CENTRAL","category":9,"coordinates":[31.5,12.5],"street":"1 Test Street",
          "rooms":[{"id":"r1","title":"STANDARD DBL","meal":3,"price_cents":12345,"currency":"eur","cancel_until":null},
                   {"id":"r2","title":"STANDARD TWN","meal":2,"price_cents":-5,"currency":"EUR","cancel_until":null},
                   {"id":"r3","title":"DELUXE DBL","meal":2,"price_cents":20000,"currency":"EUR","cancel_until":"2027-02-27T00:00:00Z"}]}]}
        """;

    [Fact]
    public void Converts_minor_units_and_reads_longitude_before_latitude()
    {
        var hotel = Normalize(Sample).Value.ShouldHaveSingleItem();

        hotel.Stars.ShouldBe(5, "a category above five is clamped");
        hotel.Location.ShouldBe(new GeoPoint(12.5, 31.5));
        var offer = hotel.Offers.First();
        offer.Price.ShouldBe(new Money(123.45m, "EUR"));
        offer.Board.ShouldBe(BoardType.HalfBoard);
        offer.RawBoard.ShouldBe("3");
        offer.Refundable.ShouldBeFalse();
        offer.Extra.ShouldNotContainKey("cancelUntil", "no cancel_until in the wire room, so none in the extras");
    }

    [Fact]
    public void Drops_offers_with_negative_prices_and_keeps_cancellation_dates_for_refundable_ones()
    {
        var hotel = Normalize(Sample).Value.Single();

        hotel.Offers.Select(static o => o.OfferId.Split('.')[2]).ShouldBe(["r1", "r3"]);
        var refundable = hotel.Offers.Last();
        refundable.Extra["cancelUntil"].ShouldBe("2027-02-27T00:00:00Z");
        refundable.Extra["roomId"].ShouldBe("r3");
    }

    [Theory]
    [InlineData(1, BoardType.RoomOnly)]
    [InlineData(2, BoardType.Breakfast)]
    [InlineData(3, BoardType.HalfBoard)]
    [InlineData(4, BoardType.FullBoard)]
    [InlineData(5, BoardType.AllInclusive)]
    [InlineData(0, BoardType.Unknown)]
    [InlineData(99, BoardType.Unknown)]
    public void Maps_numeric_meal_codes(int meal, BoardType expected) =>
        SupplierBNormalizer.ParseBoard(meal).ShouldBe(expected);

    [Theory]
    [InlineData(503, SupplierErrorKind.Transient)]
    [InlineData(429, SupplierErrorKind.RateLimited)]
    [InlineData(410, SupplierErrorKind.Unavailable)]
    [InlineData(401, SupplierErrorKind.Auth)]
    [InlineData(400, SupplierErrorKind.InvalidRequest)]
    [InlineData(504, SupplierErrorKind.Timeout)]
    [InlineData(418, SupplierErrorKind.Unknown)]
    public void Maps_status_codes(int code, SupplierErrorKind kind) =>
        SupplierBNormalizer.MapError(code, "boom").Kind.ShouldBe(kind);
}

public class SupplierCNormalizerTests
{
    private const string Ns = "urn:example:supplier-c:v1";
    private static readonly SupplierCNormalizer Normalizer = new();

    private static Result<IReadOnlyList<Hotel>> Normalize(string hotelXml, string? roomsXml) =>
        Normalizer.Normalize(
            new CBatch([new CHotelPayload(XElement.Parse(hotelXml), roomsXml is null ? null : XElement.Parse(roomsXml))]),
            TestData.Query());

    private static string HotelXml(string inner = "<Name>Maple Central</Name>", string attributes = "Code=\"HTL00001\" Stars=\"4\"") =>
        $"<Hotel xmlns=\"{Ns}\" {attributes}>{inner}</Hotel>";

    private static string Rooms(string rooms) => $"<Rooms xmlns=\"{Ns}\">{rooms}</Rooms>";

    private const string GoodRoom =
        "<Room Id=\"r1\" Name=\"Double Standard\" MealPlan=\"BREAKFAST_INCLUDED\" Refundable=\"true\"><Price Currency=\"EUR\">123.45</Price><CancelPolicy>Free cancellation until 2027-02-27.</CancelPolicy></Room>";

    [Fact]
    public void Reads_a_namespaced_hotel_with_rooms()
    {
        var hotelXml = HotelXml("<Name>Maple Central</Name><Position Lat=\"12.5\" Lon=\"31.5\"/><Address>1 Test Street, Aldermoor</Address>");

        var hotel = Normalize(hotelXml, Rooms(GoodRoom)).Value.ShouldHaveSingleItem();

        hotel.Name.ShouldBe("Maple Central");
        hotel.Stars.ShouldBe(4);
        hotel.Location.ShouldBe(new GeoPoint(12.5, 31.5));
        hotel.Address.ShouldBe("1 Test Street, Aldermoor");
        var offer = hotel.Offers.ShouldHaveSingleItem();
        offer.Price.ShouldBe(new Money(123.45m, "EUR"));
        offer.Board.ShouldBe(BoardType.Breakfast);
        offer.Refundable.ShouldBeFalse("the hub's policy rules decide, not the supplier normaliser");
        offer.Extra["roomId"].ShouldBe("r1");
        offer.Extra["mealPlan"].ShouldBe("BREAKFAST_INCLUDED");
        offer.OfferId.ShouldBe("supplier-c.HTL00001.r1.BREAKFAST_INCLUDED.20270301.3");
    }

    [Fact]
    public void Decodes_entities_and_keeps_cdata_text()
    {
        var room = "<Room Id=\"r1\" Name=\"Suite &amp; Spa\" MealPlan=\"ROOM_ONLY\"><Price Currency=\"EUR\">50.00</Price><CancelPolicy><![CDATA[Free cancellation <b>until</b> 2027-02-27]]></CancelPolicy></Room>";

        var hotel = Normalize(HotelXml("<Name>Maple &amp; Birch &lt;Central&gt;</Name>"), Rooms(room)).Value.Single();

        hotel.Name.ShouldBe("Maple & Birch <Central>");
        var offer = hotel.Offers.Single();
        offer.RoomName.ShouldBe("Suite & Spa");
        offer.Extra["cancellationText"].ShouldBe("Free cancellation <b>until</b> 2027-02-27");
        offer.Extra.ShouldNotContainKey("policyTiers", "no structured policy element, so no structured tiers");
    }

    [Fact]
    public void Tolerates_missing_optional_parts()
    {
        var hotel = Normalize(HotelXml(attributes: "Code=\"HTL00001\""), Rooms(GoodRoom)).Value.Single();

        hotel.Stars.ShouldBe(0);
        hotel.Location.ShouldBeNull();
        hotel.Address.ShouldBeEmpty();
    }

    [Fact]
    public void A_hotel_without_rooms_is_dropped()
    {
        Normalize(HotelXml(), Rooms(string.Empty)).Value.ShouldBeEmpty();
        Normalize(HotelXml(), null).Value.ShouldBeEmpty();
    }

    [Fact]
    public void A_hotel_without_a_code_or_name_is_dropped()
    {
        Normalize(HotelXml(attributes: "Stars=\"3\""), Rooms(GoodRoom)).Value.ShouldBeEmpty();
        Normalize(HotelXml("<Address>x</Address>"), Rooms(GoodRoom)).Value.ShouldBeEmpty();
    }

    [Fact]
    public void Elements_in_another_namespace_are_not_mistaken_for_supplier_elements()
    {
        var foreign = "<Hotel xmlns=\"urn:example:other\" Code=\"HTL00001\"><Name>Maple</Name></Hotel>";

        Normalize(foreign, Rooms(GoodRoom)).Value.ShouldBeEmpty();
    }

    [Fact]
    public void A_price_that_is_not_in_invariant_format_drops_that_room_only()
    {
        var bad = "<Room Id=\"r2\" Name=\"Bad\" MealPlan=\"ROOM_ONLY\"><Price Currency=\"EUR\">12,50</Price></Room>";

        var hotel = Normalize(HotelXml(), Rooms(bad + GoodRoom)).Value.Single();

        hotel.Offers.ShouldHaveSingleItem().OfferId.ShouldContain(".r1.");
    }

    [Fact]
    public void An_unknown_meal_plan_is_kept_raw_and_normalised_to_Unknown()
    {
        var room = GoodRoom.Replace("BREAKFAST_INCLUDED", "MYSTERY_PLAN", StringComparison.Ordinal);

        var offer = Normalize(HotelXml(), Rooms(room)).Value.Single().Offers.Single();

        offer.Board.ShouldBe(BoardType.Unknown);
        offer.RawBoard.ShouldBe("MYSTERY_PLAN");
    }


    [Theory]
    [InlineData("E503", SupplierErrorKind.Transient, true)]
    [InlineData("E429", SupplierErrorKind.RateLimited, false)]
    [InlineData("E410", SupplierErrorKind.Unavailable, false)]
    [InlineData("E401", SupplierErrorKind.Auth, false)]
    [InlineData("E400", SupplierErrorKind.InvalidRequest, false)]
    [InlineData("E504", SupplierErrorKind.Timeout, false)]
    [InlineData("E999", SupplierErrorKind.Unknown, false)]
    public void Maps_error_documents(string code, SupplierErrorKind kind, bool retryable)
    {
        var error = SupplierCNormalizer.ReadError(XElement.Parse($"<Error xmlns=\"{Ns}\" Code=\"{code}\">boom</Error>"));

        error.ShouldNotBeNull();
        error.Kind.ShouldBe(kind);
        error.Retryable.ShouldBe(retryable);
    }

    [Fact]
    public void A_regular_document_is_not_an_error()
    {
        SupplierCNormalizer.ReadError(XElement.Parse($"<AvailabilityResponse xmlns=\"{Ns}\"/>")).ShouldBeNull();
    }

    [Fact]
    public void Documents_with_a_doctype_are_rejected_instead_of_resolving_entities()
    {
        const string xxe = "<!DOCTYPE x [<!ENTITY secret SYSTEM \"file:///etc/hostname\">]><x>&secret;</x>";

        Should.Throw<XmlException>(() => SupplierCXml.Parse(xxe));
    }

    [Fact]
    public void Truncated_documents_are_rejected()
    {
        Should.Throw<XmlException>(() => SupplierCXml.Parse("<AvailabilityResponse><Hotels>"));
    }
}
