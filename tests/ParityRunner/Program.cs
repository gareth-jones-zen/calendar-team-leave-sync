// Local parity checks against the Python version, on GJ's laptop only (see tests/parity/live.py). Never published,
// never run by CI. It prints counts and hashes, never a name or a date.
//
//   ParityRunner record-pf <settings.toml> <out.json>               People First, read as team-leave reads it (GETs only)
//   ParityRunner record-graph <settings.toml> <yyyy-mm-dd> <out.json>   owned() and a wide between(): GETs only, silent sign-in
//   ParityRunner replay <golden.json>                               a golden from live.py, replayed; differences by field only
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using M365Graph;
using PyCompat;
using TeamLeave;
using TeamLeave.Tests;

static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

static JsonArray Array(IEnumerable<JsonNode?> items) => new(items.Select(i => i?.DeepClone()).ToArray());

switch (args.FirstOrDefault())
{
    case "record-pf":
    {
        var config = TeamLeave.Config.Load(args[1]);
        var team = new JsonObject();
        var events = new JsonObject();
        var calls = new List<string>();
        string? me = null;
        using (var api = await PeopleFirstClient.Session.OpenAsync(config.PeopleFirst,
                   new PeopleFirstClient.SessionOptions { Log = _ => { }, LockWaitSeconds = Runner.LockWaitSeconds }))
        {
            var real = new PeopleFirstApi(api);
            var recording = new Recording(real, calls, id => me = id, (person, members) => team[person] = Array(members),
                (person, body) => events[person] = body?.DeepClone());
            Leave.Read(config, recording, DateOnly.FromDateTime(DateTime.Now), _ => { });
        }
        var output = new JsonObject { ["me"] = me, ["team"] = team, ["events"] = events, ["calls"] = Array(calls.Select(c => (JsonNode?)c)) };
        File.WriteAllText(args[2], PyJson.Dump(output, sortKeys: true), new UTF8Encoding(false));
        Console.WriteLine($"people first: {calls.Count} calls, {team.Count} team listings, {events.Count} event listings; "
                          + $"sha {Sha(PyJson.Dump(output, sortKeys: true))}");
        return 0;
    }
    case "record-graph":
    {
        var config = TeamLeave.Config.Load(args[1]);
        var today = DateOnly.ParseExact(args[2], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var client = new GraphClient(new GraphClientOptions
        {
            CachePath = config.MsalCache ?? throw new InvalidOperationException("the parity settings must name a cache copy"),
            Tenant = config.Tenant,
            LoginHint = config.LoginHint,
            Scopes = Calendar.Scopes,
            Interactive = false,
            SignInHint = "(parity: silent only)",
            Log = _ => { },
        });
        var fence = new ReadOnlyGraph(new M365GraphClient(client));
        var headers = Calendar.Headers(config.Event.TimeZone);
        var owned = fence.Pages(Calendar.OwnedUrl(), Calendar.Scopes, headers).ToList();
        var between = fence.Pages(Calendar.BetweenUrl(today.AddDays(-60), today.AddDays(config.DaysAhead)), Calendar.Scopes, headers).ToList();
        var output = new JsonObject { ["owned"] = Array(owned), ["between"] = Array(between) };
        File.WriteAllText(args[3], PyJson.Dump(output, sortKeys: true), new UTF8Encoding(false));
        Console.WriteLine($"graph: {owned.Count} owned, {between.Count} in the window; owned sha {Sha(PyJson.Dump(output["owned"], sortKeys: true))}, "
                          + $"window sha {Sha(PyJson.Dump(output["between"], sortKeys: true))}");
        return 0;
    }
    case "replay":
    {
        var golden = JsonNode.Parse(File.ReadAllText(args[1]))!;
        int steps = 0, failed = 0;
        foreach (var item in golden["cases"]!.AsArray())
        {
            var folder = PyPath.Resolve(Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "replay-" + Guid.NewGuid().ToString("N")[..8])).FullName);
            try
            {
                var config = Path.Combine(folder, "config.toml");
                File.WriteAllText(config, PyStr.WindowsNewlines(item!["settings"]!.GetValue<string>()), new UTF8Encoding(false));
                foreach (var step in item["steps"]!.AsArray())
                {
                    steps++;
                    var problems = Replay.Step(config, folder, step!.AsObject(), redact: true);
                    var argv = string.Join(' ', step["argv"]!.AsArray().Select(a => a!.GetValue<string>()));
                    var expected = step["expected"]!;
                    Console.WriteLine($"  {argv}: exit {expected["exit"]}, {expected["reads"]!.AsArray().Count} reads, "
                                      + $"{expected["writes"]!.AsArray().Count} writes, {expected["pf_calls"]!.AsArray().Count} People First calls: "
                                      + (problems.Count == 0 ? "identical" : string.Join("; ", problems)));
                    if (problems.Count > 0)
                        failed++;
                }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
        Console.WriteLine($"{steps} steps, {failed} differing");
        return failed == 0 ? 0 : 1;
    }
    default:
        Console.Error.WriteLine("usage: ParityRunner record-pf|record-graph|replay ...");
        return 2;
}

/// <summary>People First as team-leave reads it, with every answer kept.</summary>
internal sealed class Recording(IPeopleFirstApi inner, List<string> calls, Action<string> me, Action<string, IReadOnlyList<JsonNode?>> team,
    Action<string, JsonNode?> events) : IPeopleFirstApi
{
    public string Me()
    {
        calls.Add("me");
        var id = inner.Me();
        me(id);
        return id;
    }

    public IReadOnlyList<JsonNode?> TeamMembers(string person)
    {
        calls.Add($"team {person}");
        var members = inner.TeamMembers(person);
        team(person, members);
        return members;
    }

    public JsonNode? Events(string person, DateOnly start, DateOnly end)
    {
        calls.Add($"events {person} {PyTime.Iso(start)} {PyTime.Iso(end)}");
        var body = inner.Events(person, start, end);
        events(person, body);
        return body;
    }
}
