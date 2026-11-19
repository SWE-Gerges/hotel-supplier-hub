using System.Text;

namespace Hub.Abstractions;

/// <summary>
/// Reads names the way different suppliers write them: any case, with or without diacritics, abbreviated, split or with
/// filler words, and scores how alike two names are.
/// </summary>
public static class NameSimilarity
{
    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.Ordinal)
    {
        ["st"] = "saint", ["ste"] = "sainte", ["mt"] = "mount", ["ft"] = "fort", ["pt"] = "port",
    };

    /// <summary>Words that say little about which place a name is: dropped unless nothing else is left.</summary>
    public static IReadOnlySet<string> DefaultFiller { get; } = new HashSet<string>(StringComparer.Ordinal) { "the", "of", "de", "la", "le" };

    /// <summary>
    /// Letters with marks folded to their plain form. A table rather than Unicode normalisation, because the host runs in
    /// invariant-globalization mode, where normalisation does not decompose letters.
    /// </summary>
    private static readonly Dictionary<char, string> Folded = BuildFolding();

    private static Dictionary<char, string> BuildFolding()
    {
        var map = new Dictionary<char, string>();
        foreach (var group in new[]
                 {
                     "aàáâãäåāăą", "cçćĉċč", "dďđ",
                     "eèéêëēĕėęě", "gĝğġģ", "hĥħ",
                     "iìíîïĩīĭįı", "jĵ", "kķ", "lĺļľŀł",
                     "nñńņň", "oòóôõöøōŏő", "rŕŗř",
                     "sśŝşš", "tţťŧ", "uùúûüũūŭůűų",
                     "wŵ", "yýÿŷ", "zźżž",
                 })
        {
            foreach (var c in group.Skip(1))
            {
                map[c] = group[..1];
            }
        }

        map['ß'] = "ss";
        map['æ'] = "ae";
        map['œ'] = "oe";
        return map;
    }

    public static string[] Tokens(string name, IReadOnlySet<string>? filler = null)
    {
        var text = new StringBuilder(name.Length);
        foreach (var raw in name)
        {
            var c = char.ToLowerInvariant(raw);
            if (c is '\'' or '’')
            {
                continue;
            }

            if (Folded.TryGetValue(c, out var plain))
            {
                text.Append(plain);
            }
            else
            {
                text.Append(char.IsLetterOrDigit(c) ? c : ' ');
            }
        }

        var tokens = text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(static t => Abbreviations.GetValueOrDefault(t, t)).ToArray();
        var meaningful = tokens.Where(t => !(filler ?? DefaultFiller).Contains(t)).ToArray();
        return meaningful.Length > 0 ? meaningful : tokens;
    }

    public static string Normalize(string name, IReadOnlySet<string>? filler = null) => string.Join(' ', Tokens(name, filler));

    /// <summary>1 for equal strings, falling to 0 as the number of edits approaches the length.</summary>
    public static double EditSimilarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
        {
            return 1;
        }

        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return 1.0 - ((double)previous[b.Length] / Math.Max(a.Length, b.Length));
    }

    /// <summary>
    /// A Jaccard score over the words of two names where a word counts in proportion to how well it matches a word of the
    /// other name, so a one-letter spelling difference costs a little and a missing word costs a lot.
    /// </summary>
    public static double TokenSetSimilarity(string[] a, string[] b)
    {
        var used = new bool[b.Length];
        double matched = 0;
        var pairs = 0;
        foreach (var token in a)
        {
            var best = -1;
            var bestScore = 0.75;
            for (var j = 0; j < b.Length; j++)
            {
                var score = used[j] ? 0 : EditSimilarity(token, b[j]);
                if (score >= bestScore)
                {
                    best = j;
                    bestScore = score;
                }
            }

            if (best >= 0)
            {
                used[best] = true;
                matched += bestScore;
                pairs++;
            }
        }

        var union = a.Length + b.Length - pairs;
        return union == 0 ? 0 : matched / union;
    }

    /// <summary>How alike two names are, from 0 to 1. Names that differ only in spacing or punctuation are equal.</summary>
    public static double Similarity(string left, string right, IReadOnlySet<string>? filler = null)
    {
        var a = Tokens(left, filler);
        var b = Tokens(right, filler);
        var squashedA = string.Concat(a);
        var squashedB = string.Concat(b);
        if (squashedA == squashedB)
        {
            return 1;
        }

        return (0.5 * TokenSetSimilarity(a, b)) + (0.5 * EditSimilarity(string.Join(' ', a), string.Join(' ', b)));
    }
}
