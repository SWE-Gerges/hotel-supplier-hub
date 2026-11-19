using Hub.Abstractions;

namespace Hub.Suppliers.Fakes;

/// <summary>
/// The city and country lists the fake suppliers publish. Each uses its own code scheme and spelling habits (diacritics,
/// upper case, abbreviations, split words, region names), and each lists a few cities that look like canonical ones but
/// are not, so the crosswalk has real work to do.
/// </summary>
internal static class FakeCityDirectories
{
    public static SupplierDirectory A { get; } = new(
        [new("XA", "Xaria"), new("XB", "Xbaland")],
        [
            new("ALD", "Aldermoor", "XA", "Northmark"),
            new("BRW", "Brightwater", "XA", "Eastshore"),
            new("CDF", "Cedarfall", "XA", "Highland"),
            new("SOR", "Saint Orrin", "XA", "Northmark"),
            new("BWB", "Brightwater Bay", "XA", "Eastshore"),
            new("HGT", "Aldermoor Heights", "XA", "Northmark"),
            new("ALX", "Aldermoor", "XB", "Westvale"),
        ]);

    public static SupplierDirectory B { get; } = new(
        [new("900", "Xaria"), new("901", "Xbaland")],
        [
            new("5001", "Äldermoor", "900", "Northmark Region"),
            new("5002", "BRIGHTWATER", "900", "Eastshore Region"),
            new("5003", "Cedar Fall", "900", "Highland Region"),
            new("5004", "St. Orrin", "900", "Northmark Region"),
            new("5005", "Brightwater Bay", "900", "Eastshore Region"),
            new("5006", "Aldermoor", "901", "Westvale Region"),
            new("5007", "Brightwatr", "900", null),
        ]);

    public static SupplierDirectory C { get; } = new(
        [new("XAL", "Xaria"), new("XBL", "Xbaland")],
        [
            new("ALDERMOOR", "Aldermoor", "XAL", null),
            new("BRIGHTWATER", "Brightwater", "XAL", null),
            new("CEDARFALL", "Cedarfall", "XAL", null),
            new("SAINT-ORRIN", "Saint-Orrin", "XAL", null),
            new("ALDERMOOR-XB", "Aldermoor", "XBL", null),
            new("NOWHEREVILLE", "Nowhereville", "XAL", null),
            new("ALDERMOOR-ZZ", "Aldermoor", "ZZZ", null),
        ]);
}
