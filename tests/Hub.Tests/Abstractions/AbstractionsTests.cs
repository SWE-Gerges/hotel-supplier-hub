using System.Text.Json;
using Hub.Abstractions;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class SupplierIdTests
{
    [Theory]
    [InlineData("supplier-a")]
    [InlineData("a")]
    [InlineData("x1-y2-z3")]
    [InlineData("0")]
    public void Accepts_lower_kebab_ids(string value)
    {
        SupplierId.TryParse(value, out var id).ShouldBeTrue();

        id.Value.ShouldBe(value);
        id.ToString().ShouldBe(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Supplier-A")]
    [InlineData("supplier_a")]
    [InlineData("supplier a")]
    [InlineData("-supplier")]
    [InlineData("supplier-")]
    [InlineData("supplier--a")]
    [InlineData("supplier.a")]
    [InlineData("supplïer")]
    public void Rejects_everything_else(string? value)
    {
        SupplierId.TryParse(value, out var id).ShouldBeFalse();

        id.IsDefault.ShouldBeTrue();
        if (value is not null)
        {
            Should.Throw<ArgumentException>(() => SupplierId.Parse(value));
        }
    }

    [Fact]
    public void Rejects_ids_longer_than_the_limit()
    {
        SupplierId.TryParse(new string('a', SupplierId.MaxLength), out _).ShouldBeTrue();
        SupplierId.TryParse(new string('a', SupplierId.MaxLength + 1), out _).ShouldBeFalse();
    }

    [Fact]
    public void Compares_by_value_and_has_a_harmless_default()
    {
        SupplierId.Parse("supplier-a").ShouldBe(SupplierId.Parse("supplier-a"));
        SupplierId.Parse("supplier-a").ShouldNotBe(SupplierId.Parse("supplier-b"));
        SupplierId.Parse("supplier-a").CompareTo(SupplierId.Parse("supplier-b")).ShouldBeLessThan(0);
        default(SupplierId).Value.ShouldBe(string.Empty);
        default(SupplierId).IsDefault.ShouldBeTrue();
    }

    [Fact]
    public void Serialises_as_a_plain_string_also_as_a_dictionary_key()
    {
        var payload = new Dictionary<SupplierId, SupplierId> { [SupplierId.Parse("supplier-a")] = SupplierId.Parse("supplier-b") };

        var json = JsonSerializer.Serialize(payload);
        var back = JsonSerializer.Deserialize<Dictionary<SupplierId, SupplierId>>(json)!;

        json.ShouldBe("{\"supplier-a\":\"supplier-b\"}");
        back[SupplierId.Parse("supplier-a")].ShouldBe(SupplierId.Parse("supplier-b"));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<SupplierId>("\"Not Valid\""));
    }
}

public class MoneyTests
{
    [Fact]
    public void Requires_a_three_letter_upper_case_currency()
    {
        new Money(1m, "EUR").Currency.ShouldBe("EUR");
        Should.Throw<ArgumentException>(() => new Money(1m, "eur"));
        Should.Throw<ArgumentException>(() => new Money(1m, "EURO"));
        Should.Throw<ArgumentException>(() => new Money(1m, null!));
    }

    [Fact]
    public void Multiplies_with_rounding_away_from_zero()
    {
        new Money(10.005m, "EUR").Multiply(1m).Amount.ShouldBe(10.01m);
        new Money(100m, "USD").Multiply(1.1m).ShouldBe(new Money(110m, "USD"));
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var json = JsonSerializer.Serialize(new Money(12.5m, "EUR"), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.ShouldBe("{\"amount\":12.5,\"currency\":\"EUR\"}");
        JsonSerializer.Deserialize<Money>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)).ShouldBe(new Money(12.5m, "EUR"));
    }
}

public class ResultTests
{
    [Fact]
    public void A_success_exposes_its_value_and_no_error()
    {
        var result = Result.Ok(42);

        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.Value.ShouldBe(42);
        Should.Throw<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void A_failure_exposes_its_error_and_refuses_to_give_a_value()
    {
        var result = Result.Fail<int>(SupplierError.Transient("busy"));

        result.IsFailure.ShouldBeTrue();
        result.Error.Kind.ShouldBe(SupplierErrorKind.Transient);
        result.Error.Retryable.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void The_default_result_is_a_success_of_the_default_value_so_structs_stay_usable_in_arrays()
    {
        default(Result<int>).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Match_picks_the_matching_branch()
    {
        Result.Ok(2).Match(v => v * 10, _ => -1).ShouldBe(20);
        Result.Fail<int>(SupplierError.Unknown("x")).Match(v => v, e => e.Message.Length).ShouldBe(1);
    }

    [Fact]
    public void A_failure_needs_an_error()
    {
        Should.Throw<ArgumentNullException>(() => Result.Fail<int>(null!));
    }

    [Fact]
    public void Only_transient_errors_are_retryable_by_default()
    {
        SupplierError.Transient("x").Retryable.ShouldBeTrue();
        SupplierError.Unavailable("x").Retryable.ShouldBeFalse();
        SupplierError.Timeout("x").Retryable.ShouldBeFalse();
        SupplierError.InvalidRequest("x").Retryable.ShouldBeFalse();
        SupplierError.Unsupported("x").Retryable.ShouldBeFalse();
        SupplierError.Unknown("x").Retryable.ShouldBeFalse();
    }
}

public class HotelSearchRequestValidationTests
{
    [Fact]
    public void A_good_request_has_no_errors()
    {
        TestData.Request().Validate().ShouldBeEmpty();
    }

    public static TheoryData<string, Func<HotelSearchRequest, HotelSearchRequest>> Invalid => new()
    {
        { "Destination", r => r with { Destination = " " } },
        { "CheckOut", r => r with { CheckOut = r.CheckIn } },
        { "CheckOut", r => r with { CheckOut = r.CheckIn.AddDays(31) } },
        { "Rooms", r => r with { Rooms = [] } },
        { "Rooms", r => r with { Rooms = Enumerable.Repeat(new Occupancy(1), 5).ToArray() } },
        { "Rooms", r => r with { Rooms = [new Occupancy(0)] } },
        { "Rooms", r => r with { Rooms = [new Occupancy(2, [18])] } },
        { "Rooms", r => r with { Rooms = [new Occupancy(2, [1, 2, 3, 4, 5])] } },
        { "Nationality", r => r with { Nationality = "xa" } },
        { "Nationality", r => r with { Nationality = "XAA" } },
        { "Currency", r => r with { Currency = "eur" } },
        { "Currency", r => r with { Currency = "" } },
        { "MaxResults", r => r with { MaxResults = 0 } },
        { "MaxResults", r => r with { MaxResults = 501 } },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Reports_each_invalid_field(string field, Func<HotelSearchRequest, HotelSearchRequest> mutate)
    {
        var errors = mutate(TestData.Request()).Validate();

        errors.ShouldContainKey(field);
        errors[field].ShouldNotBeEmpty();
    }

    [Fact]
    public void Reports_all_problems_at_once()
    {
        var errors = (TestData.Request() with { Destination = "", Currency = "x", Nationality = "y" }).Validate();

        errors.Keys.ShouldBe(["Destination", "Nationality", "Currency"], ignoreOrder: true);
    }

    [Fact]
    public void The_stay_knows_its_length()
    {
        TestData.Request().Stay.Nights.ShouldBe(3);
    }
}
