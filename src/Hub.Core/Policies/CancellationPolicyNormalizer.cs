using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.RegularExpressions;
using Hub.Abstractions;
using Microsoft.Extensions.Logging;

namespace Hub.Core;

/// <summary>A supplier's cancellation policy in canonical terms.</summary>
/// <param name="Known">False when the supplier's representation could not be understood.</param>
/// <param name="Refundable">True only when a free-cancellation window was found; never true for an unknown policy.</param>
/// <param name="RefundableUntil">When the free window ends (the earlier reading of a date, midnight UTC).</param>
public sealed record CancellationPolicy(bool Known, bool Refundable, DateTimeOffset? RefundableUntil, IReadOnlyList<CancellationTier> Tiers)
{
    /// <summary>The fail-safe result: nothing is claimed, so the offer is treated as non-refundable.</summary>
    public static CancellationPolicy Unknown { get; } = new(false, false, null, []);
}

public interface ICancellationPolicyNormalizer
{
    /// <summary>Reads the supplier's raw policy from <paramref name="offer"/> and returns the offer with the canonical fields filled.</summary>
    /// <param name="stay">The stay the offer is for; its check-in ends the policy and its length prices "nights".</param>
    /// <param name="now">When the policy is read; a policy that is already in force starts here.</param>
    RoomOffer Apply(RoomOffer offer, DateRange stay, DateTimeOffset now);

    CancellationPolicy Normalize(RoomOffer offer, DateRange stay, DateTimeOffset now);
}

/// <summary>
/// Applies the declarative rules. It is the only writer of the policy fields of a canonical offer: whatever a supplier
/// normaliser guessed, the hub trusts nothing it cannot prove. A policy that does not parse, has tiers that get cheaper
/// over time, or has no rules at all is <see cref="CancellationPolicy.Unknown"/>, which is treated as non-refundable.
/// </summary>
public sealed class CancellationPolicyNormalizer : ICancellationPolicyNormalizer
{
    private static readonly Regex Ordinals = new(@"(?<=\d)(?:st|nd|rd|th)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, PolicyRuleBook.RegexTimeout);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant, PolicyRuleBook.RegexTimeout);

    private readonly PolicyRuleBook _rules;
    private readonly ILogger<CancellationPolicyNormalizer> _logger;
    private readonly Counter<long> _unknown;
    private readonly HashSet<SupplierId> _warned = [];

    public CancellationPolicyNormalizer(PolicyRuleBook rules, IMeterFactory meters, ILogger<CancellationPolicyNormalizer> logger)
    {
        _rules = rules;
        _logger = logger;
        _unknown = meters.Create(MeterName).CreateCounter<long>("hub.policy.unknown", description: "Offers whose cancellation policy could not be understood and were treated as non-refundable.");
    }

    public const string MeterName = "Hub.Core.Policies";

    public RoomOffer Apply(RoomOffer offer, DateRange stay, DateTimeOffset now)
    {
        var policy = Normalize(offer, stay, now);
        return offer with
        {
            CancellationTiers = policy.Tiers,
            RefundableUntil = policy.RefundableUntil,
            Refundable = policy.Refundable,
            PolicyKnown = policy.Known,
        };
    }

    public CancellationPolicy Normalize(RoomOffer offer, DateRange stay, DateTimeOffset now)
    {
        if (_rules.For(offer.Supplier) is not { } rules)
        {
            return Unreadable(offer, "no rules are configured for the supplier");
        }

        var tiers = rules.Strategy switch
        {
            "textPatterns" => FromText(rules, offer, stay, now),
            "codeTable" => FromCodes(rules, offer, now),
            _ => FromDelimited(rules, offer, now),
        };
        if (tiers is null)
        {
            return Unreadable(offer, "the supplier's policy does not match any rule");
        }

        return Finish(tiers, offer, stay, now) ?? Unreadable(offer, "the policy tiers do not make sense (they get cheaper over time)");
    }

    private CancellationPolicy Unreadable(RoomOffer offer, string reason)
    {
        _unknown.Add(1, new KeyValuePair<string, object?>("supplier", offer.Supplier.Value));
        _logger.LogDebug("Offer {OfferId} from {Supplier}: {Reason}; it is treated as non-refundable.", offer.OfferId, offer.Supplier, reason);
        bool first;
        lock (_warned)
        {
            first = _warned.Add(offer.Supplier);
        }

        if (first)
        {
            _logger.LogWarning("Cancellation policies from {Supplier} could not be understood ({Reason}); such offers are treated as non-refundable. Further cases are counted, not logged.", offer.Supplier, reason);
        }

        return CancellationPolicy.Unknown;
    }

    // ---- reading the supplier's representation ----

    private static List<CancellationTier>? FromText(CompiledPolicyRules rules, RoomOffer offer, DateRange stay, DateTimeOffset now)
    {
        if (!offer.Extra.TryGetValue(rules.File.Source!, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = Spaces.Replace(raw.Trim(), " ");
        text = Ordinals.Replace(text, string.Empty);
        try
        {
            foreach (var (regex, replace) in rules.Replacements)
            {
                text = regex.Replace(text, replace);
            }

            foreach (var (pattern, regex) in rules.Patterns)
            {
                if (regex.Match(text) is { Success: true } match)
                {
                    return Instantiate(pattern, match, rules.File.DateFormats!, stay, now);
                }
            }

            return rules.NonRefundable.Any(r => r.IsMatch(text)) ? NonRefundable(now) : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static List<CancellationTier>? Instantiate(PolicyPattern pattern, Match match, List<string> formats, DateRange stay, DateTimeOffset now)
    {
        var tiers = new List<CancellationTier>();
        foreach (var template in pattern.Tiers!)
        {
            DateTimeOffset? from = template.From switch
            {
                "checkIn" => ToUtc(stay.CheckIn),
                "anchor" => now,
                var group => ParseDate(match.Groups[group![1..^1]].Value, formats),
            };
            var penalty = ReadPenalty(template.Penalty!, name => match.Groups[name].Value);
            if (from is null || penalty is null)
            {
                return null;
            }

            tiers.Add(new CancellationTier(from.Value, penalty));
        }

        return tiers;
    }

    private static List<CancellationTier>? FromCodes(CompiledPolicyRules rules, RoomOffer offer, DateTimeOffset now)
    {
        var file = rules.File;
        offer.Extra.TryGetValue(file.CodeKey!, out var code);
        if (code is null || !file.Codes!.TryGetValue(code.Trim(), out var template))
        {
            return null;
        }

        if (!offer.Extra.TryGetValue(file.DateKey!, out var rawDate) || string.IsNullOrWhiteSpace(rawDate))
        {
            return file.MissingDate == "nonRefundable" ? NonRefundable(now) : null;
        }

        var from = ParseDate(rawDate, file.DateFormats!);
        var penalty = ReadPenalty(template, static _ => string.Empty);
        return from is null || penalty is null ? null : [new CancellationTier(from.Value, penalty)];
    }

    private static List<CancellationTier>? FromDelimited(CompiledPolicyRules rules, RoomOffer offer, DateTimeOffset now)
    {
        var file = rules.File;
        if (!offer.Extra.TryGetValue(file.Source!, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var tiers = new List<CancellationTier>();
        foreach (var part in raw.Split(file.TierSeparator!, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = part.Split(file.FieldSeparator!, StringSplitOptions.TrimEntries);
            if (fields.Length != 3 || !file.KindMap!.TryGetValue(fields[1], out var kind))
            {
                return null;
            }

            DateTimeOffset? from = string.Equals(fields[0], "NOW", StringComparison.OrdinalIgnoreCase) ? now : ParseDate(fields[0], file.DateFormats!);
            var penalty = ReadPenalty(new PolicyPenalty { Kind = kind, Value = fields[2] }, static _ => string.Empty);
            if (from is null || penalty is null)
            {
                return null;
            }

            tiers.Add(new CancellationTier(from.Value, penalty));
        }

        return tiers.Count == 0 ? null : tiers;
    }

    private static List<CancellationTier> NonRefundable(DateTimeOffset now) => [new(now, new CancellationPenalty(PenaltyKind.Percent, 100))];

    private static CancellationPenalty? ReadPenalty(PolicyPenalty template, Func<string, string> group)
    {
        var text = template.Value!;
        if (text is { Length: > 2 } && text[0] == '{' && text[^1] == '}')
        {
            text = group(text[1..^1]);
        }

        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            return null;
        }

        var kind = template.Kind switch
        {
            "percent" => PenaltyKind.Percent,
            "nights" => PenaltyKind.Nights,
            _ => PenaltyKind.Fixed,
        };
        return kind == PenaltyKind.Percent && value > 100 ? null : new CancellationPenalty(kind, value);
    }

    private static DateTimeOffset? ParseDate(string text, List<string> formats)
    {
        var cleaned = Spaces.Replace(Ordinals.Replace(text.Trim().TrimEnd('.', ',', ';'), string.Empty), " ");
        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(cleaned, format, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
            {
                return new DateTimeOffset(date, TimeSpan.Zero);
            }
        }

        // Supplier-b style timestamps carry a time of day; only the date is meaningful and the earlier reading wins.
        return DateTimeOffset.TryParseExact(cleaned, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp)
            ? new DateTimeOffset(stamp.UtcDateTime.Date, TimeSpan.Zero)
            : null;
    }

    private static DateTimeOffset ToUtc(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    // ---- turning supplier tiers into a canonical schedule ----

    private static CancellationPolicy? Finish(List<CancellationTier> raw, RoomOffer offer, DateRange stay, DateTimeOffset now)
    {
        var checkIn = ToUtc(stay.CheckIn);
        var nights = Math.Max(1, stay.Nights);
        var price = offer.Price.Amount;

        if (raw.Count == 0)
        {
            return null;
        }

        var tiers = raw
            .Where(t => t.From <= checkIn)
            .OrderBy(static t => t.From)
            .ThenByDescending(t => PercentOf(t.Penalty, nights, price))
            .GroupBy(static t => t.From)
            .Select(static g => g.First())
            .Select(t => t.From < now ? t with { From = now } : t)
            .GroupBy(static t => t.From)
            .Select(g => g.OrderByDescending(t => PercentOf(t.Penalty, nights, price)).First())
            .OrderBy(static t => t.From)
            .ToList();
        if (tiers.Count == 0)
        {
            // Every tier starts after check-in, so nothing is charged before the stay.
            tiers.Add(new CancellationTier(now, new CancellationPenalty(PenaltyKind.Percent, 0)));
        }

        if (tiers[0].From > now)
        {
            tiers.Insert(0, new CancellationTier(now, new CancellationPenalty(PenaltyKind.Percent, 0)));
        }

        for (var i = 1; i < tiers.Count; i++)
        {
            if (PercentOf(tiers[i].Penalty, nights, price) < PercentOf(tiers[i - 1].Penalty, nights, price))
            {
                return null;
            }
        }

        var last = tiers[^1];
        if (PercentOf(last.Penalty, nights, price) < 100m && last.From < checkIn)
        {
            tiers.Add(new CancellationTier(checkIn, new CancellationPenalty(PenaltyKind.Percent, 100)));
        }

        DateTimeOffset? until = null;
        if (PercentOf(tiers[0].Penalty, nights, price) == 0m)
        {
            until = tiers.First(t => PercentOf(t.Penalty, nights, price) > 0m).From;
        }

        return new CancellationPolicy(true, until is { } end && end > now, until, tiers);
    }

    /// <summary>A penalty as a share of the total price, so tiers of different kinds can be compared.</summary>
    internal static decimal PercentOf(CancellationPenalty penalty, int nights, decimal price) => penalty.Kind switch
    {
        PenaltyKind.Percent => penalty.Value,
        PenaltyKind.Nights => Math.Min(100m, penalty.Value / nights * 100m),
        _ => price <= 0m ? 100m : Math.Min(100m, penalty.Value / price * 100m),
    };
}
