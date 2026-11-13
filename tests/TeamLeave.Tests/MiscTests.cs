using System.Text.Json.Nodes;
using PyCompat;

namespace TeamLeave.Tests;

/// <summary>Golden/misc.json, made by the Python version (tests/parity/misc_goldens.py): hand_pattern(),
/// date.fromisoformat(), digest() and urllib.parse.quote().</summary>
public sealed class MiscTests
{
    private static readonly JsonNode Golden = JsonNode.Parse(File.ReadAllText(ScenarioTests.GoldenFile("misc.json")))!;

    [Fact]
    public void hand_patterns_match_as_python_matches_them()
    {
        var wrong = Golden["patterns"]!.AsArray()
            .Where(p => (Calendar.HandPattern(p!["name"]!.GetValue<string>()).IsMatch(p["subject"]!.GetValue<string>()))
                        != p["match"]!.GetValue<bool>())
            .Select(p => $"{p!["name"]} / {p["subject"]}")
            .ToList();
        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(20)));
        Assert.True(Golden["patterns"]!.AsArray().Count > 5000);
    }

    [Fact]
    public void dates_parse_as_python_parses_them()
    {
        foreach (var d in Golden["dates"]!.AsArray())
        {
            var text = d!["text"]!.GetValue<string>();
            string? got;
            try
            {
                got = PyTime.Iso(PyBits.DateFromIso(text));
            }
            catch (FormatException)
            {
                got = null;
            }
            Assert.True(d["date"]?.GetValue<string>() == got, $"{Py.ReprString(text)}: python {d["date"]}, dotnet {got}");
        }
    }

    [Fact]
    public void digests_are_byte_for_byte_python()
    {
        foreach (var d in Golden["digests"]!.AsArray())
        {
            var payload = PyJson.Parse(d!["payload"]!.GetValue<string>())!.AsObject();
            Assert.Equal(d["digest"]!.GetValue<string>(), Calendar.Digest(payload));
        }
    }

    [Fact]
    public void graph_queries_are_quoted_as_python_quotes_them()
    {
        foreach (var q in Golden["quotes"]!.AsArray())
            Assert.Equal(q!["quoted"]!.GetValue<string>(), PyBits.Quote(q["text"]!.GetValue<string>()));
    }
}

/// <summary>CaseFold against Python's re itself, for every cased code point in several scripts.</summary>
public sealed class CaseFoldTests
{
    private static readonly JsonNode Golden = JsonNode.Parse(File.ReadAllText(ScenarioTests.GoldenFile("misc.json")))!;

    private static readonly (int From, int To)[] Blocks =
    [
        (0x0, 0x590), (0x10A0, 0x1100), (0x13A0, 0x1400), (0x1C80, 0x1CC0), (0x1E00, 0x2000), (0x2100, 0x2190), (0x2C00, 0x2D30),
        (0xA640, 0xA800), (0xFB00, 0xFB10), (0xFF20, 0xFF60), (0x10400, 0x10450),
    ];

    [Fact]
    public void every_cased_character_matches_what_python_matches()
    {
        // The same blocks misc_goldens.py asked Python's re about.
        var universe = Blocks.SelectMany(b => Enumerable.Range(b.From, b.To - b.From)).ToHashSet();
        var folds = Golden["folds"]!.AsArray();
        var wrong = new List<string>();
        foreach (var fold in folds)
        {
            var c = fold![0]!.GetValue<int>();
            var want = fold[1]!.AsArray().Select(d => d!.GetValue<int>()).Order().ToList();
            var got = CaseFold.Matches(c).Where(universe.Contains).Order().ToList();
            if (!got.SequenceEqual(want))
                wrong.Add($"U+{c:X4}: python {string.Join(",", want.Select(d => $"{d:X}"))}; dotnet {string.Join(",", got.Select(d => $"{d:X}"))}");
        }
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong.Take(20)));
        Assert.True(folds.Count > 2000);
        Assert.Equal(folds.Count, universe.Count(CaseFold.IsCased));
    }

    [Theory]
    [InlineData("ΣΑΣ", "σας")]
    [InlineData("Kelvin", "Kelvin")]
    [InlineData("İlker", "ilker")]
    [InlineData("Sam", "ſam")]
    public void names_python_matches_across_scripts(string name, string written) =>
        Assert.Matches(Calendar.HandPattern(name), $"{written} annual leave");
}
