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
    /// <summary>(single-day half, first-day half, last-day half) as "AM" / "PM", each null for a whole day: the
    /// library's <see cref="CalendarDays.ReportLeaveTitles.Halves"/> from People First's day parts.</summary>
    public (string? Single, string? First, string? Last) Halves =>
        CalendarDays.ReportLeaveTitles.Halves(Start, End, StartPart, EndPart, Leave.Full, Leave.Am, Leave.Pm);

    /// <summary>The calendar entry's title, built by the library's <see cref="CalendarDays.ReportLeaveTitles.Format"/>,
    /// the one definition mail-triage reads back (22 Nov 2026; byte for byte what this tool wrote before, which
    /// TitleTests checks). A template the library can't fill is a PyValueError, so it's reported, not a crash.</summary>
    public string Title(Config config)
    {
        var t = config.Titles;
        try
        {
            return CalendarDays.ReportLeaveTitles.Format(Template, Person.Name, Halves,
                new CalendarDays.ReportLeaveFormat(t.Am, t.Pm, t.FromPm, t.ToAm));
        }
        catch (FormatException error)
        {
            throw new PyValueError($"The title template couldn't be filled: {error.Message}", error);
        }
    }
}

/// <summary>A leave request waiting for your approval (People First's AwaitingApproval), of one of [approvals] types.
/// End is inclusive; the parts are as an Absence's.</summary>
public sealed record LeaveRequest(string Id, Person Person, string Type, DateOnly Start, DateOnly End, string StartPart, string EndPart)
{
    /// <summary>As an absence, titled as [types] would title it (a type not in [types]: "{name} leave"), so the text
    /// GJ sees never names the type.</summary>
    public Absence AsAbsence(Config config) =>
        new(Id, Person, Start, End, StartPart, EndPart, config.TypeTitle(Type) ?? "{name} leave");
}

/// <summary>Someone who is, or may be, away from Start to End (inclusive): any absence that isn't rejected, of any
/// type, approved or not. Only who and when; never the type, the state or the reason. A half day is a whole day here,
/// which only makes the cover rule stricter.</summary>
public sealed record Away(string PersonId, DateOnly Start, DateOnly End);

/// <summary>What one read of People First gives with [approvals] on: the calendar's absences as before, plus the
/// requests waiting for approval, who's away when, and the ids of approved absences (any type: to confirm an approval
/// whose answer was lost). All in memory only.</summary>
public sealed record LeaveRead(List<Person> People, List<Absence> Absences, List<LeaveRequest> Requests, List<Away> Away,
    HashSet<string> ApprovedIds);

/// <summary>People First, as the planner reads it: your id, someone's team, someone's absences (annotate=t). Each
/// answer as Python's json.loads would give it.
/// <para>Get and Post are for approvals (an absence's history, its approval template, and posting it back). Post is
/// only ever called through the write door; everything else reads through <see cref="ReadOnlyPeopleFirst"/>.</para></summary>
public interface IPeopleFirstApi
{
    string Me();

    IReadOnlyList<JsonNode?> TeamMembers(string person);

    JsonNode? Events(string person, DateOnly start, DateOnly end);

    /// <summary>A GET under /api/v1, as JSON.</summary>
    JsonNode? Get(string path) => throw new NotSupportedException("This People First reads only team and events.");

    /// <summary>A POST under /api/v1, never resent (see PeopleFirstClient's Api.PostAsync for what each failure means).</summary>
    JsonNode? Post(string path, JsonObject body) => throw new NotSupportedException("This People First can't write.");
}

/// <summary>The fence for People First, as ReadOnlyGraph is for the calendar: reads pass, a write is refused (a bug,
/// never a decision). Only the write door holds the People First underneath it.</summary>
public sealed class ReadOnlyPeopleFirst(IPeopleFirstApi inner) : IPeopleFirstApi
{
    public string Me() => inner.Me();

    public IReadOnlyList<JsonNode?> TeamMembers(string person) => inner.TeamMembers(person);

    public JsonNode? Events(string person, DateOnly start, DateOnly end) => inner.Events(person, start, end);

    public JsonNode? Get(string path) => inner.Get(path);

    public JsonNode? Post(string path, JsonObject body) => throw new WriteRefusedException($"POST people-first:{path}");
}

/// <summary>Leave from People First: who's off, when, and what the calendar entry is called.
///
/// <b>Read only what's needed, and keep less</b> (data minimisation):
/// - the only listing read is /hrm/people/{id}/events (with annotate=t, which names the types). It has no reason or
///   notes field. /hrm/absences/{id}, which has the reason, and the Calendar API, which has free text, are never read;
/// - <b>only the configured types and states are kept</b>, as each listing is read. Everything else (sickness,
///   appointments, pending requests) is dropped in memory, never logged, stored or reported;
/// - <b>the type never reaches the calendar</b>: each type maps to a title template.
///
/// With [approvals] on, the same listings give a little more, <b>kept in memory only</b>: the requests waiting for
/// approval of its types (to decide on), and who's away when (any absence not rejected, as person and dates only: no
/// type, state or reason), for the cover rule. Nothing more is called for, except each request's history
/// (/hrm/absences/{id}/absenceChanges) and its approval template.</summary>
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
    internal static List<(string Key, string Value)> Lookup(JsonNode? meta, string key)
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

    internal static string Part(JsonNode? value, List<(string Key, string Value)> names)
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
        var read = ReadAll(config, api, today, log);
        return (read.People, read.Absences);
    }

    /// <summary>Your reports, to config.depth levels.</summary>
    public static List<Person> Team(Config config, IPeopleFirstApi api)
    {
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
        return people;
    }

    /// <summary>As Read, from the same listings (no more calls), and with [approvals] on, also the requests waiting for
    /// approval of its types, who's away when, and the approved ids. Without [approvals], exactly Read.</summary>
    public static LeaveRead ReadAll(Config config, IPeopleFirstApi api, DateOnly today, Action<string> log)
    {
        var end = config.WindowEnd(today);
        var people = Team(config, api);
        var read = new LeaveRead(people, [], [], [], new HashSet<string>(StringComparer.Ordinal));
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
                if (config.Approvals is { } approvals && MissingNames(approvals.Types, body) is { Count: > 0 } missing)
                    throw new LeaveError($"People First has no absence type called {string.Join(", ", missing.Select(Py.ReprString))}: "
                                         + "check types under [approvals] in the settings.");
                checkedTypes = true;
            }
            read.Absences.AddRange(Absences(config, person, body));
            if (config.Approvals is not null)
                Scan(config, person, body, read);
        }
        log($"people first: {people.Count} people, {read.Absences.Count} absences kept");
        if (config.Approvals is not null)
            log($"people first: {read.Requests.Count} requests waiting for approval");
        return read;
    }

    private static List<string> MissingNames(IEnumerable<string> wanted, JsonNode? body)
    {
        var metaNode = Get(body, "meta");
        var names = Lookup(Py.Truthy(metaNode) ? metaNode : new JsonObject(), "events.eventTypeId").Select(t => t.Value).ToHashSet(StringComparer.Ordinal);
        return wanted.Where(name => names.Count > 0 && !names.Contains(name)).ToList();
    }

    /// <summary>For approvals, from one listing already read by Absences (so its shape is checked): requests waiting for
    /// approval of [approvals] types, everyone's absences that aren't rejected (who and when only), and the approved
    /// ids. A listing it can't read throws LeaveError: cover worked out from a partial answer could approve leave that
    /// leaves nobody in.</summary>
    private static void Scan(Config config, Person person, JsonNode? body, LeaveRead read)
    {
        var meta = Py.Truthy(Get(body, "meta")) ? Get(body, "meta") : new JsonObject();
        var types = Lookup(meta, "events.eventTypeId");
        var starts = Lookup(meta, "events.startTypeId");
        var ends = Lookup(meta, "events.endTypeId");
        var approvalTypes = config.Approvals!.Types.ToHashSet(StringComparer.Ordinal);
        var events = Get(body, "data")!["events"];
        if (!Py.Truthy(events))
            return;
        foreach (var e in (JsonArray)events!)
        {
            var state = PyBits.Key(Get(e, "state"));
            if (state == PyBits.Key(JsonValue.Create("Rejected")))
                continue;   // not away, and dropped unread
            try
            {
                var id = Has(e, "eventId") ? Py.Str(e!["eventId"]) : throw new KeyNotFoundException("eventId");
                var start = Has(e, "startDate") ? PyBits.DateFromIso(PyStr.Head(Py.Str(e!["startDate"]), 10)) : throw new KeyNotFoundException("startDate");
                var end = Has(e, "endDate") ? PyBits.DateFromIso(PyStr.Head(Py.Str(e!["endDate"]), 10)) : throw new KeyNotFoundException("endDate");
                read.Away.Add(new Away(person.Id, start, end));
                if (state == PyBits.Key(JsonValue.Create(Approvals.ApprovedState)))
                    read.ApprovedIds.Add(id);
                if (state != PyBits.Key(JsonValue.Create(Approvals.PendingState)))
                    continue;
                var type = types.FirstOrDefault(t => t.Key == PyBits.Key(Get(e, "eventTypeId"))).Value;
                if (type is null || !approvalTypes.Contains(type))
                    continue;   // another type: away, but not a request the rules look at
                var startPart = Part(Py.Truthy(Get(e, "startTypeId")) ? Get(e, "startTypeId") : JsonValue.Create(Full), starts);
                var endPart = Part(Py.Truthy(Get(e, "endTypeId")) ? Get(e, "endTypeId") : JsonValue.Create(Full), ends);
                read.Requests.Add(new LeaveRequest(id, person, type, start, end, startPart, endPart));
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
    }

    /// <summary>For approving by hand: the absence with this id in your team's listings (today to days_ahead), as a
    /// request of whatever type it is, with its state as People First names it. Null if no one in your team has it.</summary>
    public static (LeaveRequest Request, string State)? Find(Config config, IPeopleFirstApi api, DateOnly today, string absenceId)
    {
        var end = config.WindowEnd(today);
        foreach (var person in Team(config, api))
        {
            var body = api.Events(person.Id, today, end);
            if (Get(body, "data") is not JsonObject data || data["events"] is not JsonArray events)
                throw new LeaveError("People First's events listing has changed shape (no data.events).");
            var e = events.FirstOrDefault(item => Has(item, "eventId") && Py.Str(item!["eventId"]) == absenceId);
            if (e is null)
                continue;
            var meta = Py.Truthy(Get(body, "meta")) ? Get(body, "meta") : new JsonObject();
            try
            {
                var start = PyBits.DateFromIso(PyStr.Head(Py.Str(e["startDate"]), 10));
                var finish = PyBits.DateFromIso(PyStr.Head(Py.Str(e["endDate"]), 10));
                var type = Lookup(meta, "events.eventTypeId").FirstOrDefault(t => t.Key == PyBits.Key(Get(e, "eventTypeId"))).Value ?? "";
                var startPart = Part(Py.Truthy(Get(e, "startTypeId")) ? Get(e, "startTypeId") : JsonValue.Create(Full), Lookup(meta, "events.startTypeId"));
                var endPart = Part(Py.Truthy(Get(e, "endTypeId")) ? Get(e, "endTypeId") : JsonValue.Create(Full), Lookup(meta, "events.endTypeId"));
                var state = Py.Truthy(Get(e, "state")) ? Py.Str(Get(e, "state")) : "";
                return (new LeaveRequest(absenceId, person, type, start, finish, startPart, endPart), state);
            }
            catch (FormatException)
            {
                throw new LeaveError("The absence in People First's listing couldn't be read (ValueError).");
            }
        }
        return null;
    }

    /// <summary>The state People First gives an absence in one listing, or null if the listing doesn't have it.</summary>
    public static string? StateOf(JsonNode? body, string absenceId)
    {
        if (Get(body, "data") is not JsonObject data || data["events"] is not JsonArray events)
            return null;
        var e = events.FirstOrDefault(item => Has(item, "eventId") && Py.Str(item!["eventId"]) == absenceId);
        return e is null ? null : Py.Truthy(Get(e, "state")) ? Py.Str(Get(e, "state")) : "";
    }
}
