using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PyCompat;
using WinBackground;

namespace TeamLeave.Tests;

/// <summary>The synthetic scenarios in Golden/scenarios.json, made by running the Python version (tests/parity/
/// scenarios.py): for every command line, the exit code, stdout, stderr, log file, state file, People First calls,
/// Graph reads (URL, scopes, headers), Graph writes (method, URL and the exact body bytes), notifications and hook
/// input must be the same. Set TEAM_LEAVE_GOLDEN to the path of a bigger local run to check that too.</summary>
public sealed class ScenarioTests(ITestOutputHelper output)
{
    public const string Placeholder = Replay.Placeholder;

    public static string GoldenFile(string name) => Path.Combine(AppContext.BaseDirectory, "Golden", name);

    [Fact]
    public void every_committed_scenario_matches_python()
    {
        var (steps, failures) = Check(GoldenFile("scenarios.json"));
        output.WriteLine($"{steps} steps");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
        Assert.True(steps > 100);
    }

    [Fact]
    public void a_local_golden_file_matches_python()
    {
        var path = Environment.GetEnvironmentVariable("TEAM_LEAVE_GOLDEN");
        Assert.SkipWhen(string.IsNullOrEmpty(path), "TEAM_LEAVE_GOLDEN isn't set");
        var (steps, failures) = Check(path!);
        output.WriteLine($"{steps} steps");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    public static (int Steps, List<string> Failures) Check(string path)
    {
        var golden = JsonNode.Parse(File.ReadAllText(path))!;
        var failures = new List<string>();
        var steps = 0;
        var index = 0;
        foreach (var item in golden["cases"]!.AsArray())
        {
            index++;
            using var folder = new TempFolder();
            var config = folder.File("config.toml");
            File.WriteAllText(config, PyStr.WindowsNewlines(item!["settings"]!.GetValue<string>()), new UTF8Encoding(false));
            var n = 0;
            foreach (var step in item["steps"]!.AsArray())
            {
                n++;
                steps++;
                foreach (var problem in Replay.Step(config, folder.Path, step!.AsObject()))
                    failures.Add($"case {index} step {n} ({string.Join(' ', step["argv"]!.AsArray().Select(a => a!.GetValue<string>()))}): {problem}");
            }
        }
        return (steps, failures);
    }
}

