using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Hub.Abstractions;

namespace Hub.Core;

// Cancellation-policy rules are data: one JSON file per supplier says how that supplier's own representation of a policy
// (free text, penalty codes, structured tiers) maps to canonical tiers. The files are loaded and checked at startup. Unknown
// fields are rejected, so a typo in a rule file stops the host instead of silently doing nothing.

public sealed class PolicyRuleFile
{
    public int SchemaVersion { get; set; }

    public string? Supplier { get; set; }

    /// <summary><c>textPatterns</c>, <c>codeTable</c> or <c>delimited</c>.</summary>
    public string? Strategy { get; set; }

    /// <summary>The <see cref="RoomOffer.Extra"/> key that holds the supplier's policy (text and delimited strategies).</summary>
    public string? Source { get; set; }

    public List<string>? DateFormats { get; set; }

    /// <summary>Text rewrites applied before matching, for instance "the first night" to "1 night".</summary>
    public List<PolicyReplacement>? Replacements { get; set; }

    public List<string>? NonRefundablePatterns { get; set; }

    public List<PolicyPattern>? Patterns { get; set; }

    public string? DateKey { get; set; }

    public string? CodeKey { get; set; }

    /// <summary>What a missing date means for the code table: <c>nonRefundable</c> or <c>unknown</c>.</summary>
    public string? MissingDate { get; set; }

    public Dictionary<string, PolicyPenalty>? Codes { get; set; }

    public string? TierSeparator { get; set; }

    public string? FieldSeparator { get; set; }

    public List<string>? Fields { get; set; }

    public Dictionary<string, string>? KindMap { get; set; }
}

public sealed class PolicyReplacement
{
    public string? Regex { get; set; }

    public string? Replace { get; set; }
}

public sealed class PolicyPattern
{
    public string? Name { get; set; }

    public string? Regex { get; set; }

    public List<PolicyTier>? Tiers { get; set; }
}

public sealed class PolicyTier
{
    /// <summary><c>{group}</c> (a date captured by the pattern), <c>checkIn</c> or <c>anchor</c> (the moment the policy is read).</summary>
    public string? From { get; set; }

    public PolicyPenalty? Penalty { get; set; }
}

public sealed class PolicyPenalty
{
    /// <summary><c>percent</c>, <c>nights</c> or <c>fixed</c> (fixed amounts are in the offer's currency).</summary>
    public string? Kind { get; set; }

    /// <summary>A number, or <c>{group}</c> to read it from the pattern.</summary>
    public string? Value { get; set; }
}

public sealed class PolicyRuleException(string message, IReadOnlyList<string> problems) : Exception(message)
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>A rule file that passed every check, with its regular expressions compiled.</summary>
internal sealed class CompiledPolicyRules
{
    public required SupplierId Supplier { get; init; }

    public required string Strategy { get; init; }

    public required PolicyRuleFile File { get; init; }

    public required IReadOnlyList<(Regex Regex, string Replace)> Replacements { get; init; }

    public required IReadOnlyList<Regex> NonRefundable { get; init; }

    public required IReadOnlyList<(PolicyPattern Pattern, Regex Regex)> Patterns { get; init; }
}

/// <summary>Where rule files come from: embedded in a supplier assembly, or read from a directory.</summary>
public interface IPolicyRuleSource
{
    IEnumerable<(string Name, string Json)> Read();
}

public sealed class PolicyRuleBook
{
    private const string Penalties = "percent, nights or fixed";
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
    };

    private readonly Dictionary<SupplierId, CompiledPolicyRules> _rules = [];

    /// <exception cref="PolicyRuleException">Any file is invalid; the message lists every problem found.</exception>
    public PolicyRuleBook(IEnumerable<IPolicyRuleSource> sources)
    {
        var problems = new List<string>();
        foreach (var (name, json) in sources.SelectMany(static s => s.Read()))
        {
            var compiled = Compile(name, json, problems);
            if (compiled is null)
            {
                continue;
            }

            if (!_rules.TryAdd(compiled.Supplier, compiled))
            {
                problems.Add($"{name}: supplier '{compiled.Supplier}' already has rules from another file.");
            }
        }

        if (problems.Count > 0)
        {
            throw new PolicyRuleException($"The cancellation-policy rules are invalid:{Environment.NewLine}{string.Join(Environment.NewLine, problems.Select(static p => "  - " + p))}", problems);
        }
    }

    public IReadOnlyCollection<SupplierId> Suppliers => _rules.Keys;

    internal CompiledPolicyRules? For(SupplierId supplier) => _rules.GetValueOrDefault(supplier);

    private static CompiledPolicyRules? Compile(string name, string json, List<string> problems)
    {
        PolicyRuleFile? file;
        try
        {
            file = JsonSerializer.Deserialize<PolicyRuleFile>(json, Json);
        }
        catch (JsonException ex)
        {
            problems.Add($"{name}: {ex.Message}");
            return null;
        }

        if (file is null)
        {
            problems.Add($"{name}: the file is empty.");
            return null;
        }

        var before = problems.Count;
        void Problem(string message) => problems.Add($"{name}: {message}");

        if (file.SchemaVersion != 1)
        {
            Problem($"schemaVersion must be 1, not {file.SchemaVersion}.");
        }

        SupplierId supplier = default;
        if (file.Supplier is null || !SupplierId.TryParse(file.Supplier, out supplier))
        {
            Problem("supplier must be a valid supplier id.");
        }

        var strategy = file.Strategy;
        if (strategy is not ("textPatterns" or "codeTable" or "delimited"))
        {
            Problem("strategy must be textPatterns, codeTable or delimited.");
            return null;
        }

        CheckMembers(file, strategy, Problem);
        var formats = file.DateFormats ?? [];
        if (formats.Count == 0)
        {
            Problem("dateFormats needs at least one format.");
        }

        foreach (var format in formats)
        {
            var sample = new DateTime(2027, 2, 27, 0, 0, 0, DateTimeKind.Utc);
            if (string.IsNullOrWhiteSpace(format)
                || !DateTime.TryParseExact(sample.ToString(format, CultureInfo.InvariantCulture), format, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var readBack)
                || readBack.Date != sample.Date)
            {
                Problem($"date format '{format}' cannot read back a date it writes.");
            }
        }

        var replacements = new List<(Regex, string)>();
        foreach (var replacement in file.Replacements ?? [])
        {
            if (replacement.Regex is null || replacement.Replace is null)
            {
                Problem("a replacement needs regex and replace.");
            }
            else if (TryRegex(replacement.Regex, Problem) is { } regex)
            {
                replacements.Add((regex, replacement.Replace));
            }
        }

        var nonRefundable = (file.NonRefundablePatterns ?? []).Select(p => TryRegex(p, Problem)).OfType<Regex>().ToList();
        var patterns = new List<(PolicyPattern, Regex)>();
        foreach (var pattern in file.Patterns ?? [])
        {
            if (CheckPattern(pattern, Problem) is { } regex)
            {
                patterns.Add((pattern, regex));
            }
        }

        switch (strategy)
        {
            case "textPatterns":
                if (string.IsNullOrWhiteSpace(file.Source))
                {
                    Problem("textPatterns needs a source key.");
                }

                if (patterns.Count == 0 && nonRefundable.Count == 0)
                {
                    Problem("textPatterns needs at least one pattern or nonRefundable pattern.");
                }

                break;
            case "codeTable":
                if (string.IsNullOrWhiteSpace(file.DateKey) || string.IsNullOrWhiteSpace(file.CodeKey))
                {
                    Problem("codeTable needs dateKey and codeKey.");
                }

                if (file.MissingDate is not ("nonRefundable" or "unknown"))
                {
                    Problem("codeTable needs missingDate: nonRefundable or unknown.");
                }

                if (file.Codes is null or { Count: 0 })
                {
                    Problem("codeTable needs codes.");
                }

                foreach (var (code, penalty) in file.Codes ?? [])
                {
                    CheckPenalty(penalty, $"code '{code}'", null, Problem);
                }

                break;
            default:
                if (string.IsNullOrWhiteSpace(file.Source) || string.IsNullOrEmpty(file.TierSeparator) || string.IsNullOrEmpty(file.FieldSeparator))
                {
                    Problem("delimited needs source, tierSeparator and fieldSeparator.");
                }

                if (file.Fields is not { Count: 3 } fields || !fields.SequenceEqual(["from", "kind", "value"]))
                {
                    Problem("delimited fields must be exactly from, kind, value.");
                }

                if (file.KindMap is null or { Count: 0 } || file.KindMap.Values.Any(static v => v is not ("percent" or "nights" or "fixed")))
                {
                    Problem($"delimited needs a kindMap whose values are {Penalties}.");
                }

                break;
        }

        return problems.Count > before
            ? null
            : new CompiledPolicyRules
            {
                Supplier = supplier,
                Strategy = strategy,
                File = file,
                Replacements = replacements,
                NonRefundable = nonRefundable,
                Patterns = patterns,
            };
    }

    private static void CheckMembers(PolicyRuleFile file, string strategy, Action<string> problem)
    {
        void Forbid(bool present, string member)
        {
            if (present)
            {
                problem($"{member} does not belong to the {strategy} strategy.");
            }
        }

        var text = strategy == "textPatterns";
        var code = strategy == "codeTable";
        var delimited = strategy == "delimited";
        Forbid(!text && (file.Patterns is not null || file.NonRefundablePatterns is not null || file.Replacements is not null), "patterns, nonRefundablePatterns and replacements");
        Forbid(!code && (file.DateKey is not null || file.CodeKey is not null || file.MissingDate is not null || file.Codes is not null), "dateKey, codeKey, missingDate and codes");
        Forbid(!delimited && (file.TierSeparator is not null || file.FieldSeparator is not null || file.Fields is not null || file.KindMap is not null), "tierSeparator, fieldSeparator, fields and kindMap");
        Forbid(code && file.Source is not null, "source");
    }

    private static Regex? TryRegex(string? pattern, Action<string> problem)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            problem("a regular expression is empty.");
            return null;
        }

        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            problem($"regular expression '{pattern}' is invalid: {ex.Message}");
            return null;
        }
    }

    private static Regex? CheckPattern(PolicyPattern pattern, Action<string> problem)
    {
        var label = $"pattern '{pattern.Name}'";
        if (string.IsNullOrWhiteSpace(pattern.Name))
        {
            problem("a pattern needs a name.");
        }

        var regex = TryRegex(pattern.Regex, problem);
        if (pattern.Tiers is null or { Count: 0 })
        {
            problem($"{label} needs at least one tier.");
            return null;
        }

        var groups = regex?.GetGroupNames().ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var tier in pattern.Tiers)
        {
            var from = tier.From;
            if (from is not ("checkIn" or "anchor") && !(from is { Length: > 2 } && from[0] == '{' && from[^1] == '}' && groups.Contains(from[1..^1])))
            {
                problem($"{label}: tier 'from' must be checkIn, anchor or a {{group}} the regular expression captures.");
            }

            CheckPenalty(tier.Penalty, label, groups, problem);
        }

        return regex;
    }

    private static void CheckPenalty(PolicyPenalty? penalty, string label, HashSet<string>? groups, Action<string> problem)
    {
        if (penalty?.Kind is not ("percent" or "nights" or "fixed"))
        {
            problem($"{label}: penalty kind must be {Penalties}.");
            return;
        }

        var value = penalty.Value;
        if (value is { Length: > 2 } && value[0] == '{' && value[^1] == '}')
        {
            if (groups is null || !groups.Contains(value[1..^1]))
            {
                problem($"{label}: penalty value {value} names a group the regular expression does not capture.");
            }

            return;
        }

        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || number < 0
            || (penalty.Kind == "percent" && number > 100))
        {
            problem($"{label}: penalty value '{value}' is not a valid amount for {penalty.Kind}.");
        }
    }
}

/// <summary>Rule files read from a directory, so an operator can add or replace a supplier's rules without a build.</summary>
public sealed class DirectoryPolicyRuleSource(string directory) : IPolicyRuleSource
{
    public IEnumerable<(string Name, string Json)> Read() =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal).Select(static f => (Path.GetFileName(f), File.ReadAllText(f)))
            : throw new PolicyRuleException($"The policy rules directory '{directory}' does not exist.", [directory]);
}
