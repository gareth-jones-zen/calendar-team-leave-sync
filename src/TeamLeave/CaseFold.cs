using System.Text;
using System.Text.RegularExpressions;

namespace TeamLeave;

/// <summary>Python's re.IGNORECASE, for the hand-made entry pattern: a name can be in any script, and .NET's
/// RegexOptions.IgnoreCase matches differently from Python's re outside ASCII (a capital sigma doesn't match a final
/// sigma in .NET, for one). So the pattern spells out, for each cased character, every character Python's re would
/// match to it, from Python's own Unicode data (CaseFold.g.cs), and runs with no IgnoreCase at all.</summary>
internal static partial class CaseFold
{
    // Built on first use, from the generated arrays (whose initialisers, in the other half of this partial class,
    // must have run first).
    private static class Tables
    {
        public static readonly Dictionary<int, int> Lower = BuildLower();
        public static readonly HashSet<int> Cased = [.. Lower.Keys, .. CasedLower];
        public static readonly Dictionary<int, List<int>> Groups = BuildGroups(Lower);
        public static readonly Dictionary<int, int[]> Extras = BuildExtras();
    }

    private static Dictionary<int, int> Lower => Tables.Lower;
    private static HashSet<int> Cased => Tables.Cased;
    private static Dictionary<int, List<int>> Groups => Tables.Groups;
    private static Dictionary<int, int[]> Extras => Tables.Extras;

    private static Dictionary<int, int> BuildLower()
    {
        var map = new Dictionary<int, int>(LowerPairs.Length / 2);
        for (var i = 0; i < LowerPairs.Length; i += 2)
            map[LowerPairs[i]] = LowerPairs[i + 1];
        return map;
    }

    private static Dictionary<int, List<int>> BuildGroups(Dictionary<int, int> lower)
    {
        // group[x]: every code point whose lower case is x (x itself, if it's its own lower case)
        var groups = new Dictionary<int, List<int>>();
        foreach (var (from, to) in lower)
        {
            if (!groups.TryGetValue(to, out var list))
                groups[to] = list = [];
            list.Add(from);
        }
        foreach (var (key, list) in groups)
            if (!lower.ContainsKey(key))
                list.Add(key);
        return groups;
    }

    private static Dictionary<int, int[]> BuildExtras()
    {
        var map = new Dictionary<int, int[]>();
        var at = 0;
        foreach (var length in ExtraLengths)
        {
            map[Extra[at]] = Extra[(at + 1)..(at + length)];
            at += length;
        }
        return map;
    }

    public static int ToLower(int c) => Lower.GetValueOrDefault(c, c);

    public static bool IsCased(int c) => Cased.Contains(c);

    private static IEnumerable<int> Group(int lower) => Groups.TryGetValue(lower, out var list) ? list : Lower.ContainsKey(lower) ? [] : [lower];

    /// <summary>Every code point Python's re, ignoring case, matches to c (just c, if c isn't cased), in order.</summary>
    public static SortedSet<int> Matches(int c)
    {
        if (!IsCased(c))
            return [c];
        var lower = ToLower(c);
        var found = new SortedSet<int>(Group(lower));
        foreach (var extra in Extras.GetValueOrDefault(lower, []))
            found.UnionWith(Group(extra));
        return found;
    }

    /// <summary>Text as a .NET pattern matching what Python's re.escape(text) matches with re.IGNORECASE.</summary>
    public static string Pattern(string text)
    {
        var b = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var matches = Matches(rune.Value);
            if (matches.Count == 1)
                b.Append(Regex.Escape(rune.ToString()));
            else
                b.Append("(?:").AppendJoin('|', matches.Select(m => Regex.Escape(char.ConvertFromUtf32(m)))).Append(')');
        }
        return b.ToString();
    }
}
