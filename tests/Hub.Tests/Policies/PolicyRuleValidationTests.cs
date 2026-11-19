using Hub.Abstractions;
using Hub.Core;
using Hub.Suppliers.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Hub.Tests;

public class PolicyRuleValidationTests
{
    private sealed class InlineSource(params (string Name, string Json)[] files) : IPolicyRuleSource
    {
        public IEnumerable<(string Name, string Json)> Read() => files;
    }

    private const string ValidText = """
        {
          "schemaVersion": 1, "supplier": "supplier-a", "strategy": "textPatterns", "source": "policy",
          "dateFormats": ["yyyy-MM-dd"],
          "patterns": [ { "name": "p", "regex": "free until (?<date>[0-9-]+)", "tiers": [ { "from": "{date}", "penalty": { "kind": "percent", "value": "100" } } ] } ]
        }
        """;

    private static PolicyRuleException Rejects(string json) =>
        Should.Throw<PolicyRuleException>(() => new PolicyRuleBook([new InlineSource(("test.json", json))]));

    private static string Edit(string from, string to) => ValidText.Replace(from, to, StringComparison.Ordinal);

    [Fact]
    public void A_valid_file_is_accepted()
    {
        new PolicyRuleBook([new InlineSource(("a.json", ValidText))]).Suppliers.ShouldBe([TestData.A]);
    }

    [Fact]
    public void The_rule_files_that_ship_with_the_fakes_are_valid_and_cover_all_three_suppliers()
    {
        new PolicyRuleBook([new EmbeddedPolicyRuleSource()]).Suppliers.OrderBy(static s => s.Value).ShouldBe([TestData.A, TestData.B, TestData.C]);
    }

    [Theory]
    [InlineData("\"surprise\": 1,", "unknown field")]
    public void Unknown_fields_are_rejected(string injected, string _)
    {
        var error = Rejects(ValidText.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, " + injected, StringComparison.Ordinal));

        error.Problems.ShouldHaveSingleItem().ShouldContain("surprise");
    }

    [Fact]
    public void Unknown_fields_inside_a_pattern_are_rejected_too()
    {
        Rejects(Edit("\"name\": \"p\",", "\"name\": \"p\", \"colour\": \"red\",")).Problems.ShouldHaveSingleItem().ShouldContain("colour");
    }

    [Theory]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 2", "schemaVersion")]
    [InlineData("\"supplier\": \"supplier-a\"", "\"supplier\": \"Supplier A\"", "supplier must be")]
    [InlineData("\"strategy\": \"textPatterns\"", "\"strategy\": \"magic\"", "strategy must be")]
    [InlineData("\"dateFormats\": [\"yyyy-MM-dd\"]", "\"dateFormats\": []", "dateFormats needs")]
    [InlineData("\"dateFormats\": [\"yyyy-MM-dd\"]", "\"dateFormats\": [\"MM/dd\"]", "date format")]
    [InlineData("free until (?<date>[0-9-]+)", "free until (?<date>[0-9-]+", "invalid")]
    [InlineData("\"from\": \"{date}\"", "\"from\": \"{other}\"", "'from'")]
    [InlineData("\"kind\": \"percent\"", "\"kind\": \"dollars\"", "penalty kind")]
    [InlineData("\"value\": \"100\"", "\"value\": \"150\"", "not a valid amount")]
    [InlineData("\"value\": \"100\"", "\"value\": \"{missing}\"", "does not capture")]
    [InlineData("\"source\": \"policy\",", "", "needs a source")]
    public void Each_kind_of_mistake_is_reported_and_stops_the_book_from_loading(string from, string to, string expected)
    {
        var error = Rejects(Edit(from, to));

        error.Problems.ShouldContain(p => p.Contains(expected, StringComparison.OrdinalIgnoreCase), string.Join(" | ", error.Problems));
        error.Message.ShouldContain("test.json");
    }

    [Fact]
    public void Members_of_another_strategy_are_rejected()
    {
        var json = """
            { "schemaVersion": 1, "supplier": "supplier-b", "strategy": "codeTable", "dateFormats": ["yyyy-MM-dd"],
              "dateKey": "d", "codeKey": "c", "missingDate": "nonRefundable", "codes": { "X": { "kind": "percent", "value": "10" } },
              "tierSeparator": ";" }
            """;

        Rejects(json).Problems.ShouldContain(static p => p.Contains("does not belong", StringComparison.Ordinal));
    }

    [Fact]
    public void A_code_table_and_a_delimited_file_need_their_own_members()
    {
        Rejects("""{ "schemaVersion": 1, "supplier": "supplier-b", "strategy": "codeTable", "dateFormats": ["yyyy-MM-dd"] }""")
            .Problems.ShouldContain(static p => p.Contains("dateKey", StringComparison.Ordinal));
        Rejects("""{ "schemaVersion": 1, "supplier": "supplier-c", "strategy": "delimited", "dateFormats": ["yyyy-MM-dd"], "source": "x" }""")
            .Problems.ShouldContain(static p => p.Contains("tierSeparator", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_files_for_one_supplier_and_every_problem_in_every_file_are_reported_together()
    {
        var error = Should.Throw<PolicyRuleException>(() => new PolicyRuleBook([new InlineSource(
            ("one.json", ValidText), ("two.json", ValidText), ("broken.json", "{ not json"))]));

        error.Problems.ShouldContain(static p => p.Contains("already has rules", StringComparison.Ordinal));
        error.Problems.ShouldContain(static p => p.StartsWith("broken.json", StringComparison.Ordinal));
        error.Problems.Count.ShouldBe(2);
    }

    [Fact]
    public void Host_start_up_stops_when_a_rule_file_is_invalid()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHubCore(configuration);
        services.AddSingleton<IPolicyRuleSource>(new InlineSource(("bad.json", Edit("\"strategy\": \"textPatterns\"", "\"strategy\": \"magic\""))));
        using var provider = services.BuildServiceProvider();

        Should.Throw<PolicyRuleException>(() => provider.GetServices<IHostedService>().ToList());
    }

    [Fact]
    public void A_rules_directory_adds_files_and_cannot_silently_replace_shipped_rules()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hub-rules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "z.json"), ValidText.Replace("supplier-a", "supplier-z", StringComparison.Ordinal));
            var added = new PolicyRuleBook([new EmbeddedPolicyRuleSource(), new DirectoryPolicyRuleSource(directory)]);
            added.Suppliers.Count.ShouldBe(4);

            File.WriteAllText(Path.Combine(directory, "dup.json"), ValidText);
            Should.Throw<PolicyRuleException>(() => new PolicyRuleBook([new EmbeddedPolicyRuleSource(), new DirectoryPolicyRuleSource(directory)]))
                .Problems.ShouldContain(static p => p.Contains("already has rules", StringComparison.Ordinal));
            Should.Throw<PolicyRuleException>(() => new PolicyRuleBook([new DirectoryPolicyRuleSource(Path.Combine(directory, "missing"))]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_slow_regular_expression_cannot_hang_a_search()
    {
        var json = ValidText.Replace("free until (?<date>[0-9-]+)", "(a+)+b(?<date>x)", StringComparison.Ordinal);
        var normalizer = PolicyHarness.Create(null, null, new InlineSource(("slow.json", json)));
        var offer = PolicyHarness.Offer(TestData.A, ("policy", new string('a', 60) + "c"));

        var policy = normalizer.Normalize(offer, PolicyHarness.Stay, PolicyHarness.Now);

        policy.Known.ShouldBeFalse("the match timed out, which is treated as an unreadable policy");
    }
}
