using System.Globalization;
using System.Text;
using Hub.Abstractions;

namespace Hub.Core;

/// <summary>
/// Builds a key that is equal for near-identical rooms described differently by different suppliers, so the
/// cheapest of them can be shown first. Room names are lower-cased, stripped of diacritics and punctuation,
/// freed of the hotel's brand word, mapped onto standard bed and view tokens, and sorted.
/// </summary>
public static class RoomGroupKey
{
    private static readonly HashSet<string> GenericHotelWords = new(StringComparer.Ordinal)
    {
        "hotel", "the", "resort", "suites", "suite", "inn", "lodge", "residence", "spa", "and", "by", "at", "of",
    };

    private static readonly HashSet<string> NoiseWords = new(StringComparer.Ordinal)
    {
        "room", "rooms", "with", "and", "a", "the", "of", "for", "only", "in",
    };

    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["dbl"] = "double",
        ["twn"] = "twin",
        ["sgl"] = "single",
        ["trpl"] = "triple",
        ["ste"] = "suite",
        ["sup"] = "superior",
        ["dlx"] = "deluxe",
        ["std"] = "standard",
        ["fam"] = "family",
        ["jr"] = "junior",
        ["jnr"] = "junior",
        ["kng"] = "king",
        ["qn"] = "queen",
        ["oceanview"] = "seaview",
        ["ocean"] = "sea",
        ["seaside"] = "sea",
    };

    private static readonly HashSet<string> ViewPrefixes = new(StringComparer.Ordinal)
    {
        "sea", "city", "garden", "pool", "mountain", "park", "river", "lake", "harbor", "courtyard",
    };

    public static string Create(string hotelName, string roomName, BoardType board) =>
        $"{NormalizeRoomName(hotelName, roomName)}|{board.ToString().ToLowerInvariant()}";

    public static string NormalizeRoomName(string hotelName, string roomName)
    {
        var brand = BrandWord(hotelName);
        var tokens = Tokenize(roomName).Select(static t => Synonyms.GetValueOrDefault(t, t)).ToList();

        var merged = new List<string>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] == "view" && merged.Count > 0 && ViewPrefixes.Contains(merged[^1]))
            {
                merged[^1] += "view";
            }
            else
            {
                merged.Add(tokens[i]);
            }
        }

        var result = merged
            .Where(token => !NoiseWords.Contains(token) && token != brand)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return result.Length == 0 ? "room" : string.Join('-', result);
    }

    private static string? BrandWord(string hotelName) =>
        Tokenize(hotelName).FirstOrDefault(static t => !GenericHotelWords.Contains(t));

    // Latin letters with diacritics and their plain forms. A table is used instead of Unicode normalisation because
    // normalisation does nothing when the runtime is configured for invariant globalization, as it is in the container image.
    private const string Accented = "ÀÁÂÃÄÅÇÈÉÊËÌÍÎÏÑÒÓÔÕÖØÙÚÛÜÝàáâãäåçèéêëìíîïñòóôõöøùúûüýÿ"
                                    + "ŠšŽžČčĆćĐđŁłŃńŚśŹźŻżĚěŘřŮůŤťĎďĘęĄąŐőŰű";

    private const string Plain = "AAAAAACEEEEIIIINOOOOOOUUUUYaaaaaaceeeeiiiinoooooouuuuyy"
                                 + "SsZzCcCcDdLlNnSsZzZzEeRrUuTtDdEeAaOoUu";

    private static readonly Dictionary<char, string> Ligatures = new()
    {
        ['ß'] = "ss",
        ['Æ'] = "ae",
        ['æ'] = "ae",
        ['Œ'] = "oe",
        ['œ'] = "oe",
    };

    private static IEnumerable<string> Tokenize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue; // a combining accent that was already split from its letter
            }

            if (Ligatures.TryGetValue(c, out var expansion))
            {
                builder.Append(expansion);
                continue;
            }

            var index = Accented.IndexOf(c, StringComparison.Ordinal);
            var plain = index >= 0 ? Plain[index] : c;
            builder.Append(char.IsLetterOrDigit(plain) ? char.ToLowerInvariant(plain) : ' ');
        }

        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
