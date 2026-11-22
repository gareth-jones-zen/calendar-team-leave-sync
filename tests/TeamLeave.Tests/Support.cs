using System.Text.Json.Nodes;
using PyCompat;

namespace TeamLeave.Tests;

/// <summary>A folder in the temp directory, deleted afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = PyPath.Resolve(Directory.CreateTempSubdirectory("calendar-team-leave-sync-tests-").FullName);

    public string File(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The Python tests' fixtures: "today" is Thu 12 Nov 2026; Alex manages Jordan Lee and Robin Hall.</summary>
public static class Fixtures
{
    public static readonly DateOnly Today = new(2026, 11, 12);
    public static readonly Person Jordan = new("P1", "Jordan Lee");
    public static readonly Person Robin = new("P2", "Robin Hall");

    public const string Settings = """

        login_hint = "alex@example.com"
        max_deletes = 3
        [people_first]
        url = "https://example.people-first.com"
        [types]
        "Holiday" = "{name} annual leave"
        "My Birthday" = "{name} annual leave"

        """;

    private static readonly List<TempFolder> Folders = [];

    public static DateOnly D(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>config(): the settings written to a temp folder and loaded, with the state and log beside them.</summary>
    public static Config Config(string text = Settings)
    {
        var folder = new TempFolder();
        lock (Folders)
            Folders.Add(folder);
        var path = folder.File("config.toml");
        File.WriteAllText(path, text);
        var c = TeamLeave.Config.Load(path);
        c.StateFile = folder.File("state.json");
        c.LogFile = folder.File("log.txt");
        return c;
    }

    public static JsonObject Event(string id, string start, string end, string type = "T1", string state = "Approved",
        string startPart = "FULL", string endPart = "FULL") => new()
    {
        ["eventId"] = id,
        ["personId"] = "P",
        ["startDate"] = start,
        ["endDate"] = end,
        ["eventTypeId"] = type,
        ["state"] = state,
        ["startTypeId"] = startPart,
        ["endTypeId"] = endPart,
        ["absenceDurationUnit"] = "Daily",
    };

    private static JsonArray Values(params (string Id, string Value)[] items) =>
        new(items.Select(i => (JsonNode?)new JsonObject { ["id"] = i.Id, ["value"] = i.Value }).ToArray());

    public static JsonArray Types() => Values(("T1", "Holiday"), ("T2", "My Birthday"), ("T3", "Sickness"), ("T4", "Appointment"));

    public static JsonArray Parts() => Values(("FULL", "All day"), ("HALF_AM", "AM"), ("HALF_PM", "PM"));

    public static JsonObject Listing(params JsonObject[] events) => new()
    {
        ["data"] = new JsonObject { ["events"] = new JsonArray(events.Cast<JsonNode?>().ToArray()) },
        ["meta"] = new JsonObject
        {
            ["events.eventTypeId"] = new JsonObject { ["values"] = Types() },
            ["events.startTypeId"] = new JsonObject { ["values"] = Parts() },
            ["events.endTypeId"] = new JsonObject { ["values"] = Parts() },
        },
    };

    public static Absence Absence(Config c, string id, Person person, string start, string end, string sp = "FULL", string ep = "FULL") =>
        new(id, person, D(start), D(end), sp, ep, c.TypeTitle("Holiday")!);

    /// <summary>owned_entry(): the entry calendar-team-leave-sync would have written for absence a.</summary>
    public static Entry Owned(Config c, Absence a, string id = "E1")
    {
        var payload = Calendar.Body(c, a);
        return new Entry(id, Py.Str(payload["subject"]), a.Start, a.End, true) { Absence = a.Id, Hash = Calendar.Digest(payload) };
    }

    public static Entry Hand(string id, string subject, string start, string end, bool allDay = true) => new(id, subject, D(start), D(end), allDay);

    public static void Quiet(string _)
    {
    }
}

/// <summary>FakeCalendar: entries in memory; every write recorded.</summary>
public sealed class FakeCalendar(IEnumerable<Entry>? entries = null) : ICalendarReads, ICalendarWriter
{
    public readonly List<Entry> Entries = entries?.ToList() ?? [];
    public readonly List<(string Op, string Id, JsonObject? Payload)> Writes = [];
    public int BetweenCalls;

    public IReadOnlyList<Entry> Owned() => Entries.Where(e => e.Absence.Length > 0).ToList();

    public IReadOnlyList<Entry> Between(DateOnly first, DateOnly last)
    {
        BetweenCalls++;
        return Entries.Where(e => e.End >= first && e.Start <= last).ToList();
    }

    public string Create(JsonObject payload)
    {
        Writes.Add(("create", "", payload));
        return "new";
    }

    public void Update(string eventId, JsonObject payload) => Writes.Add(("update", eventId, payload));

    public void Delete(string eventId) => Writes.Add(("delete", eventId, null));
}

/// <summary>A Graph that records every call. Pages answer from a table by URL (empty if not there).</summary>
public sealed class RecordingGraph : IGraph
{
    public readonly List<string> Calls = [];
    public readonly Dictionary<string, List<JsonObject>> Answers = new(StringComparer.Ordinal);

    public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string> scopes, IReadOnlyDictionary<string, string> headers)
    {
        Calls.Add($"GET {url} {string.Join(",", scopes)} {string.Join(",", headers.Select(h => $"{h.Key}={h.Value}"))}");
        return Answers.TryGetValue(url, out var items) ? items.Select(i => (JsonObject)i.DeepClone()).ToList() : [];
    }

    public JsonObject Send(string method, string url, JsonObject? body, IReadOnlyList<string> scopes, IReadOnlyDictionary<string, string> headers)
    {
        Calls.Add($"{method} {url} {(body is null ? "-" : PyJson.Dump(body))} {string.Join(",", scopes)} {string.Join(",", headers.Select(h => $"{h.Key}={h.Value}"))}");
        return method == "POST" ? new JsonObject { ["id"] = "new" } : [];
    }

    public void Token()
    {
    }
}

/// <summary>People First answering from fixed listings.</summary>
public sealed class FakeApi(Func<string, IReadOnlyList<JsonNode?>> team, Func<string, JsonNode?> events) : IPeopleFirstApi
{
    public string Me() => "ME";

    public IReadOnlyList<JsonNode?> TeamMembers(string person) => team(person);

    public JsonNode? Events(string person, DateOnly start, DateOnly end) => events(person);
}
