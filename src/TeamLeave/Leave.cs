using System.Text.Json;
using System.Text.Json.Nodes;
using PyCompat;

namespace TeamLeave;

/// <summary>People First's listing couldn't be read safely, so nothing is written.</summary>
public sealed class LeaveError(string message) : Exception(message);

/// <summary>Someone in your team: People First's person id, and the name the calendar shows (People First's
/// "First Last", or the [names] override).</summary>
public sealed record Person(string Id, string Name);

/// <summary>One kept absence. End is inclusive; the parts are FULL, HALF_AM or HALF_PM.</summary>
public sealed record Absence(string Id, Person Person, DateOnly Start, DateOnly End, string StartPart, string EndPart, string Template)
{
    /// <summary>(single-day half, first-day half, last-day half), each null for a whole day.
    /// One day: AM or PM if only one half is off (both halves, or FULL, is a whole day). Several days: the first day's
    /// part says when it starts (PM: from the afternoon), the last day's when it ends (AM: after the morning).</summary>
    public (string? Single, string? First, string? Last) Halves
    {
        get
        {
            if (Start == End)
            {
                var parts = new HashSet<string>(StringComparer.Ordinal) { StartPart, EndPart };
                parts.Remove(Leave.Full);
                return (parts.Count == 1 ? parts.First() : null, null, null);
            }
            return (null, StartPart == Leave.Pm ? Leave.Pm : null, EndPart == Leave.Am ? Leave.Am : null);
        }
    }

    public string Title(Config config)
    {
        var title = PyBits.Format(Template, ("name", Person.Name));
        var (single, first, last) = Halves;
        var t = config.Titles;
        if (single == Leave.Am)
            title = PyBits.Format(t.Am, ("title", title));
        else if (single == Leave.Pm)
            title = PyBits.Format(t.Pm, ("title", title));
        if (first is not null)
            title = PyBits.Format(t.FromPm, ("title", title));
        if (last is not null)
            title = PyBits.Format(t.ToAm, ("title", title));
        return title;
    }
}

/// <summary>People First, as the planner reads it: your id, someone's team, someone's absences (annotate=t). Each
/// answer as Python's json.loads would give it.</summary>
public interface IPeopleFirstApi
{
    string Me();

    IReadOnlyList<JsonNode?> TeamMembers(string person);

    JsonNode? Events(string person, DateOnly start, DateOnly end);
}

/// <summary>Leave from People First: who's off, when, and what the calendar entry is called.
///
/// <b>Read only what's needed, and keep less</b> (data minimisation):
/// - the only listing read is /hrm/people/{id}/events (with annotate=t, which names the types). It has no reason or
///   notes field. /hrm/absences/{id}, which has the reason, and the Calendar API, which has free text, are never read;
/// - <b>only the configured types and states are kept</b>, as each listing is read. Everything else (sickness,
///   appointments, pending requests) is dropped in memory, never logged, stored or reported;
/// - <b>the type never reaches the calendar</b>: each type maps to a title template.</summary>
public static class Leave
{
    public const string Full = "FULL";
    public const string Am = "HALF_AM";
    public const string Pm = "HALF_PM";

    // annotate=t names each day part; the ids are what the events carry.
    private static readonly Dictionary<string, string> PartNames = new(StringComparer.Ordinal)
    {
        ["all day"] = Full,
        ["am"] = Am,
        ["pm"] = Pm,
    };

    /// <summary>display_name(): " ".join of the stripped first and last names, then the [names] override.</summary>
    public static string DisplayName(Config config, JsonNode? member)
    {
        string Part(string key)
        {
            var value = member?[key];
            if (!Py.Truthy(value))
                return "";
            return value is JsonValue v && v.GetValueKind() == JsonValueKind.String
                ? PyStr.Strip(v.GetValue<string>())
                : throw new InvalidOperationException($"People First's {key} isn't text.");
        }
        var full = string.Join(' ', new[] { Part("firstName"), Part("lastName") }.Where(p => p.Length > 0));
        return config.Names.TryGetValue(full, out var name) ? name : full;
    }

    private static JsonNode? Get(JsonNode? obj, string key) => obj is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    private static bool Has(JsonNode? obj, string key) => obj is JsonObject o && o.ContainsKey(key);

    /// <summary>_lookup(): annotate=t's {id: value} for one field, e.g. events.eventTypeId. Keys by Python equality;
    /// each value str(value or "").</summary>
    private static List<(string Key, string Value)> Lookup(JsonNode? meta, string key)
    {
        var found = new List<(string Key, string Value)>();
        var field = Get(meta, key);
        var values = Py.Truthy(field) ? Get(field, "values") : null;
        if (!Py.Truthy(values) || values is not JsonArray array)
            return found;
        foreach (var v in array)
        {
            var id = PyBits.Key(Get(v, "id"));
            var value = Get(v, "value");
            var text = Py.Truthy(value) ? Py.Str(value) : "";
            var at = found.FindIndex(f => f.Key == id);
            if (at >= 0)
                found[at] = (id, text);
            else
                found.Add((id, text));
        }
        return found;
    }

    private static string Part(JsonNode? value, List<(string Key, string Value)> names)
    {
        if (value is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is Full or Am or Pm)
            return v.GetValue<string>();
        var key = PyBits.Key(value);
        var name = names.FirstOrDefault(n => n.Key == key).Value ?? "";
        if (PartNames.TryGetValue(PyStr.Lower(PyStr.Strip(name)), out var named))
            return named;
        throw new LeaveError($"People First gave a day part it doesn't know ({PyBits.Repr(value)}), so leave can't be read safely.");
    }

    /// <summary>absences(): the kept absences in one person's events listing (annotate=t). Throws LeaveError if the
    /// listing has changed shape, rather than guess: a guess could remove someone's leave from the calendar.</summary>
    public static List<Absence> Absences(Config config, Person person, JsonNode? body)
    {
        if (body is not JsonObject || Get(body, "data") is not JsonObject data || !data.ContainsKey("events"))
            throw new LeaveError("People First's events listing has changed shape (no data.events).");
        var metaNode = Get(body, "meta");
        var meta = Py.Truthy(metaNode) ? metaNode : new JsonObject();
        var types = Lookup(meta, "events.eventTypeId");
        if (types.Count == 0)
            throw new LeaveError("People First's events listing didn't name the absence types (annotate=t has changed?), "
                                 + "so leave can't be read safely.");
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, name) in types)
            if (config.TypeTitle(name) is { } title)
                wanted[id] = title;
        var starts = Lookup(meta, "events.startTypeId");
        var ends = Lookup(meta, "events.endTypeId");
        var states = config.States.Select(PyBits.Key).ToHashSet(StringComparer.Ordinal);
        var output = new List<Absence>();
        var events = data["events"];
        if (!Py.Truthy(events))
            return output;
        foreach (var e in (JsonArray)events!)
        {
            if (e is not JsonObject)
                throw new InvalidOperationException("People First's events listing holds something that isn't an event.");
            if (!wanted.TryGetValue(PyBits.Key(Get(e, "eventTypeId")), out var template) || !states.Contains(PyBits.Key(Get(e, "state"))))
                continue;   // dropped unread: not logged, not stored
            try
            {
                var id = Has(e, "eventId") ? Py.Str(e["eventId"]) : throw new KeyNotFoundException("eventId");
                var start = Has(e, "startDate") ? PyBits.DateFromIso(PyStr.Head(Py.Str(e["startDate"]), 10)) : throw new KeyNotFoundException("startDate");
                var end = Has(e, "endDate") ? PyBits.DateFromIso(PyStr.Head(Py.Str(e["endDate"]), 10)) : throw new KeyNotFoundException("endDate");
                var startPart = Part(Py.Truthy(Get(e, "startTypeId")) ? Get(e, "startTypeId") : JsonValue.Create(Full), starts);
                var endPart = Part(Py.Truthy(Get(e, "endTypeId")) ? Get(e, "endTypeId") : JsonValue.Create(Full), ends);
                output.Add(new Absence(id, person, start, end, startPart, endPart, template));
            }
            catch (KeyNotFoundException)
            {
                throw new LeaveError("An absence in People First's listing couldn't be read (KeyError).");
            }
            catch (FormatException)
            {
                throw new LeaveError("An absence in People First's listing couldn't be read (ValueError).");
            }
        }
        return output;
    }

    /// <summary>missing_types(): configured types People First doesn't have at all: almost certainly a typo in [types].</summary>
    public static List<string> MissingTypes(Config config, JsonNode? body)
    {
        var metaNode = Get(body, "meta");
        var names = Lookup(Py.Truthy(metaNode) ? metaNode : new JsonObject(), "events.eventTypeId").Select(t => t.Value).ToHashSet(StringComparer.Ordinal);
        return config.Types.Select(t => t.Key).Where(name => names.Count > 0 && !names.Contains(name)).ToList();
    }

    /// <summary>read(): your reports (to config.depth levels) and their kept absences from today to days_ahead. Any
    /// failure throws: a partial answer must never be mistaken for leave that was cancelled.</summary>
    public static (List<Person> People, List<Absence> Absences) Read(Config config, IPeopleFirstApi api, DateOnly today, Action<string> log)
    {
        var end = today.AddDays(config.DaysAhead);
        var me = api.Me();
        var people = new List<Person>();
        var queue = new Queue<(string Manager, int Level)>();
        queue.Enqueue((me, 0));
        var seen = new HashSet<string>(StringComparer.Ordinal) { me };
        while (queue.Count > 0)
        {
            var (manager, level) = queue.Dequeue();
            if (level >= config.Depth)
                continue;
            foreach (var member in api.TeamMembers(manager))
            {
                if (!Has(member, "personId"))
                    throw new KeyNotFoundException("personId");
                var pid = Py.Str(member!["personId"]);
                if (!seen.Add(pid))
                    continue;
                people.Add(new Person(pid, DisplayName(config, member)));
                queue.Enqueue((pid, level + 1));   // only read if depth allows another level
            }
        }
        var found = new List<Absence>();
        var checkedTypes = false;
        foreach (var person in people)
        {
            var body = api.Events(person.Id, today, end);
            if (!checkedTypes)
            {
                var unknown = MissingTypes(config, body);
                if (unknown.Count > 0)
                    throw new LeaveError($"People First has no absence type called {string.Join(", ", unknown.Select(Py.ReprString))}: "
                                         + "check [types] in the settings.");
                checkedTypes = true;
            }
            found.AddRange(Absences(config, person, body));
        }
        log($"people first: {people.Count} people, {found.Count} absences kept");
        return (people, found);
    }
}
