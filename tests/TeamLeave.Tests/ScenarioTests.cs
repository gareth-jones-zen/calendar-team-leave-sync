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
    public const string Placeholder = "{DIR}";

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

/// <summary>One recorded step run through the .NET command line, with every way out replaced by the recording.</summary>
public static class Replay
{
    private static string Placeholders(string text, string folder) =>
        text.Replace(PyJson.Quote(folder)[1..^1], ScenarioTests.Placeholder, StringComparison.Ordinal)
            .Replace(folder, ScenarioTests.Placeholder, StringComparison.Ordinal);

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    public static List<string> Step(string config, string folder, JsonObject step)
    {
        var expected = step["expected"]!.AsObject();
        var problems = new List<string>();
        if (expected.ContainsKey("crash"))
            return problems;   // Python crashed: nothing to compare (none in the committed set)
        var graph = new ReplayGraph(step);
        var pfCalls = new List<string>();
        var alerts = new JsonArray();
        var hooks = new List<string>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var now = PyTime.FromIso(step["now"]!.GetValue<string>());
        var pf = step["pf"]?.AsObject() ?? [];
        var code = Cli.Run(["--config", config, .. step["argv"]!.AsArray().Select(a => a!.GetValue<string>())], new CliHost
        {
            Out = stdout,
            Err = stderr,
            Graph = (_, _) => graph,
            ReadPeopleFirst = (c, today, log) =>
            {
                var session = pf["session"]?.GetValue<string>();
                var message = pf["session_message"]?.GetValue<string>();
                switch (session)
                {
                    case "sign-in":
                        throw new PeopleFirstClient.SignInNeededException(message ?? "People First's sign-in wants a password.");
                    case "timeout":
                        throw new TimeoutException(message ?? "Another tool kept the profile.");
                    case "error":
                        throw new PeopleFirstClient.PeopleFirstException(message ?? "Couldn't start Edge for People First");
                }
                return Leave.Read(c, new ReplayApi(pf, pfCalls), today, log);
            },
            Notify = (title, lines) => alerts.Add((JsonNode)new JsonObject
            {
                ["title"] = title,
                ["lines"] = new JsonArray(lines.Select(l => (JsonNode?)JsonValue.Create(Placeholders(l, folder))).ToArray()),
            }),
            HookRunner = (_, input, _, _) =>
            {
                hooks.Add(Placeholders(input, folder));
                if (step["hook_missing"]?.GetValue<bool>() == true)
                    throw new System.ComponentModel.Win32Exception(2);
                return new Completed(step["hook_exit"]?.GetValue<int>() ?? 0, step["hook_stdout"]?.GetValue<string>() ?? "",
                    step["hook_stderr"]?.GetValue<string>() ?? "");
            },
            Tasks = _ => new NoTask(),
            Now = () => now,
        });

        void Same(string what, string? want, string? got)
        {
            if (want != got)
                problems.Add($"{what} differs:\n  python: {Show(want)}\n  dotnet: {Show(got)}");
        }
        Same("exit", Py.Str(expected["exit"]), code.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Same("stdout", expected["stdout"]!.GetValue<string>(), Placeholders(Lf(stdout.ToString()), folder));
        Same("stderr", expected["stderr"]!.GetValue<string>(), Placeholders(Lf(stderr.ToString()), folder));
        var state = Path.Combine(folder, "state.json");
        Same("state", expected["state"]?.GetValue<string>(), File.Exists(state) ? PyStr.ReadText(state) : null);
        var log = Path.Combine(folder, "log.txt");
        Same("log", expected["log"]?.GetValue<string>(), File.Exists(log) ? Placeholders(Encoding.UTF8.GetString(File.ReadAllBytes(log)), folder) : null);
        Same("people first calls", string.Join(" | ", expected["pf_calls"]!.AsArray().Select(c => c!.GetValue<string>())), string.Join(" | ", pfCalls));
        Same("reads", string.Join("\n", expected["reads"]!.AsArray().Select(r => $"{r!["url"]} {r["scopes"]!.ToJsonString()} {r["headers"]!.ToJsonString()}")),
            string.Join("\n", graph.Reads));
        Same("writes", string.Join("\n", expected["writes"]!.AsArray().Select(w => w!.ToJsonString())), string.Join("\n", graph.Writes.Select(w => w.ToJsonString())));
        Same("alerts", expected["alerts"]!.ToJsonString(), alerts.ToJsonString());
        Same("hooks", string.Join("\n", expected["hooks"]!.AsArray().Select(h => h!.GetValue<string>())), string.Join("\n", hooks));
        return problems;
    }

    private static string Show(string? text) => text is null ? "(none)" : text.Length > 1500 ? text[..1500] + "..." : text;

    private sealed class NoTask : ITask
    {
        public string Name => Autostart.TaskName;
        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir) => throw new InvalidOperationException("no tasks in tests");
        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir, TaskSchedule schedule) => throw new InvalidOperationException("no tasks in tests");
        public bool Disable() => false;
        public bool Stop() => false;
        public bool Start() => false;
        public IReadOnlyDictionary<string, string>? Status() => null;
    }
}

/// <summary>People First replaying the step's tables, recording each call as oracle.py's FakeApi does.</summary>
public sealed class ReplayApi(JsonObject pf, List<string> calls) : IPeopleFirstApi
{
    private void Call(string key)
    {
        calls.Add(key);
        if (pf["fail"] is JsonObject fail && fail["at"]!.GetValue<string>() == key)
        {
            var message = fail["message"]!.GetValue<string>();
            if (fail["kind"]!.GetValue<string>() == "sign-in")
                throw new PeopleFirstClient.SignInNeededException(message);
            throw new PeopleFirstClient.PeopleFirstException(message);
        }
    }

    public string Me()
    {
        Call("me");
        return pf["me"]?.GetValue<string>() ?? "ME";
    }

    public IReadOnlyList<JsonNode?> TeamMembers(string person)
    {
        Call($"team {person}");
        return pf["team"]?[person] is JsonArray team ? team.Select(m => m?.DeepClone()).ToList() : [];
    }

    public JsonNode? Events(string person, DateOnly start, DateOnly end)
    {
        Call($"events {person} {PyTime.Iso(start)} {PyTime.Iso(end)}");
        return pf["events"]?[person]?.DeepClone() ?? new JsonObject { ["data"] = new JsonObject { ["events"] = new JsonArray() }, ["meta"] = new JsonObject() };
    }
}

/// <summary>Graph replaying the recorded reads in order, and recording writes as oracle.py's FakeGraph does.</summary>
public sealed class ReplayGraph(JsonObject step) : IGraph
{
    private readonly Queue<JsonObject> reads = new((step["expected"]!["reads"]!.AsArray()).Select(r => r!.AsObject()));
    public readonly List<string> Reads = [];
    public readonly List<JsonObject> Writes = [];

    public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string> scopes, IReadOnlyDictionary<string, string> headers)
    {
        if (step["graph_sign_in"] is { } signIn)
            throw new M365Graph.SignInNeededException(signIn.GetValue<string>());
        var headerJson = new JsonObject();
        foreach (var (k, v) in headers)
            headerJson[k] = v;
        Reads.Add($"{url} {new JsonArray(scopes.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()).ToJsonString()} {headerJson.ToJsonString()}");
        if (reads.Count == 0)
            return [];
        return reads.Dequeue()["items"]!.AsArray().Select(i => (JsonObject)i!.DeepClone()).ToList();
    }

    public JsonObject Send(string method, string url, JsonObject? body, IReadOnlyList<string> scopes, IReadOnlyDictionary<string, string> headers)
    {
        var headerJson = new JsonObject();
        foreach (var (k, v) in headers)
            headerJson[k] = v;
        Writes.Add(new JsonObject
        {
            ["method"] = method,
            ["url"] = url,
            ["body"] = body is null ? null : PyJson.Dump(body),
            ["scopes"] = new JsonArray(scopes.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["headers"] = headerJson,
        });
        if (step["fail_write"] is { } fail && fail.GetValue<int>() == Writes.Count)
            throw new M365Graph.GraphRequestException((HttpStatusCode)(step["fail_status"]?.GetValue<int>() ?? 503), method, url, "");
        return method == "POST" ? new JsonObject { ["id"] = $"new{Writes.Count}" } : [];
    }

    public void Token()
    {
    }
}
