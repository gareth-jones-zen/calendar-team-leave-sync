using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PyCompat;

namespace TeamLeave;

/// <summary>A calendar entry as read: dates are the first day and the last day (inclusive).</summary>
public sealed record Entry(string Id, string Subject, DateOnly Start, DateOnly End, bool AllDay)
{
    /// <summary>The People First absence id, if team-leave owns it.</summary>
    public string Absence { get; init; } = "";

    public string Hash { get; init; } = "";

    /// <summary>isOrganizer as Python read it (missing: true; null: false).</summary>
    public bool Organiser { get; init; } = true;

    public int Attendees { get; init; }
    public bool Cancelled { get; init; }
    public string Kind { get; init; } = "singleInstance";
}

/// <summary>What the planner reads: the entries team-leave owns, and everything in a range.</summary>
public interface ICalendarReads
{
    /// <summary>Every entry carrying team-leave's property, past ones included (the caller leaves those alone).</summary>
    IReadOnlyList<Entry> Owned();

    /// <summary>Everything in the calendar from first to last (inclusive), with team-leave's property where set.</summary>
    IReadOnlyList<Entry> Between(DateOnly first, DateOnly last);
}

/// <summary>Your Outlook calendar, through Microsoft Graph: the entries team-leave owns, and hand-made ones.
///
/// <b>An entry is team-leave's only if it carries its extended property</b> (a hidden, single-value MAPI property
/// holding the People First absence id), plus a second holding a hash of what was written. <b>The calendar is the
/// source of truth</b>: there's no local list of entries, so a lost state file is harmless, and an entry deleted by
/// hand is simply written again. Entries without the property are never changed or deleted, unless `sync --adopt`
/// takes one over.
///
/// Reads go through a read-only Graph (GETs only); writes go through the write door's writer, and nothing
/// else.</summary>
public sealed class Calendar(IGraph reads, ICalendarWriter writes, string timeZone) : ICalendarReads, ICalendarWriter
{
    public static readonly string[] Scopes = ["Calendars.ReadWrite"];
    public const string PropertySet = "{bd639353-3a69-4233-90ad-38c8c468272a}";   // team-leave's own; never change it
    public const string AbsenceProp = $"String {PropertySet} Name team-leave.absence";
    public const string HashProp = $"String {PropertySet} Name team-leave.hash";
    public const string Select = "subject,start,end,isAllDay,showAs,categories,sensitivity,isReminderOn,isOrganizer,attendees,isCancelled,type";

    private readonly IGraph reads = new ReadOnlyGraph(reads);
    private readonly Dictionary<string, string> headers = Headers(timeZone);

    /// <summary>Prefer: outlook.timezone="...", on every call.</summary>
    public static Dictionary<string, string> Headers(string timeZone) =>
        new(StringComparer.Ordinal) { ["Prefer"] = $"outlook.timezone=\"{timeZone}\"" };

    public static string OwnedUrl()
    {
        var filter = PyBits.Quote($"singleValueExtendedProperties/Any(ep: ep/id eq '{AbsenceProp}' and ep/value ne null)");
        var expand = PyBits.Quote($"singleValueExtendedProperties($filter=id eq '{AbsenceProp}' or id eq '{HashProp}')");
        return $"/me/events?$filter={filter}&$expand={expand}&$select={Select}&$top=100";
    }

    public static string BetweenUrl(DateOnly first, DateOnly last)
    {
        var expand = PyBits.Quote($"singleValueExtendedProperties($filter=id eq '{AbsenceProp}')");
        return $"/me/calendarView?startDateTime={PyTime.Iso(first)}T00:00:00&endDateTime={PyTime.Iso(last.AddDays(1))}T00:00:00"
               + $"&$expand={expand}&$select={Select}&$top=500";
    }

    public IReadOnlyList<Entry> Owned() => reads.Pages(OwnedUrl(), Scopes, headers).Select(Parse).ToList();

    public IReadOnlyList<Entry> Between(DateOnly first, DateOnly last) =>
        reads.Pages(BetweenUrl(first, last), Scopes, headers).Select(Parse).ToList();

    public string Create(JsonObject payload) => writes.Create(payload);

    public void Update(string eventId, JsonObject payload) => writes.Update(eventId, payload);

    public void Delete(string eventId) => writes.Delete(eventId);

    private static DateTime When(JsonNode? value)
    {
        var text = value is JsonObject o && o.TryGetPropertyValue("dateTime", out var dt) ? Py.Str(dt) : "";
        return PyTime.FromIso(PyStr.Head(text, 19));
    }

    /// <summary>entry(): one Graph event as an Entry.</summary>
    public static Entry Parse(JsonObject item)
    {
        var start = When(item.TryGetPropertyValue("start", out var s) ? s : throw new KeyNotFoundException("start"));
        var end = When(item.TryGetPropertyValue("end", out var e) ? e : throw new KeyNotFoundException("end"));
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        if (item["singleValueExtendedProperties"] is JsonArray list)
            foreach (var p in list)
            {
                var id = p is JsonObject po && po.TryGetPropertyValue("id", out var pid) ? Py.Str(pid) : "";
                var value = p is JsonObject pv && pv.TryGetPropertyValue("value", out var val) ? val is null ? "" : Py.Str(val) : "";
                props[PyStr.Lower(id)] = value;
            }
        // All-day entries end at midnight the day after; a timed one ends on the day its end falls in.
        var last = end > start ? DateOnly.FromDateTime(end.AddMinutes(-1)) : DateOnly.FromDateTime(start);
        var subject = item["subject"];
        return new Entry(item.TryGetPropertyValue("id", out var eid) ? Py.Str(eid) : throw new KeyNotFoundException("id"),
            Py.Truthy(subject) ? Py.Str(subject) : "", DateOnly.FromDateTime(start), last, Py.Truthy(item["isAllDay"]))
        {
            Absence = props.GetValueOrDefault(PyStr.Lower(AbsenceProp), ""),
            Hash = props.GetValueOrDefault(PyStr.Lower(HashProp), ""),
            Organiser = !item.TryGetPropertyValue("isOrganizer", out var organiser) || Py.Truthy(organiser),
            Attendees = item["attendees"] is JsonArray attendees ? attendees.Count : 0,
            Cancelled = Py.Truthy(item["isCancelled"]),
            Kind = Py.Truthy(item["type"]) ? Py.Str(item["type"]) : "singleInstance",
        };
    }

    /// <summary>body(): what the entry should be. No attendees (added on create only), and never the type or a reason.
    /// The keys in Python's order.</summary>
    public static JsonObject Body(Config config, Absence absence)
    {
        var ev = config.Event;
        var output = new JsonObject
        {
            ["subject"] = absence.Title(config),
            ["start"] = new JsonObject { ["dateTime"] = $"{PyTime.Iso(absence.Start)}T00:00:00", ["timeZone"] = ev.TimeZone },
            ["end"] = new JsonObject { ["dateTime"] = $"{PyTime.Iso(absence.End.AddDays(1))}T00:00:00", ["timeZone"] = ev.TimeZone },
            ["isAllDay"] = true,
            ["showAs"] = ev.ShowAs,
            ["sensitivity"] = ev.Sensitivity,
            ["categories"] = new JsonArray(ev.Categories.Select(c => c?.DeepClone()).ToArray()),
            ["isReminderOn"] = ev.Reminder is not null,
            ["body"] = new JsonObject { ["contentType"] = "text", ["content"] = ev.Body },
        };
        if (ev.Reminder is { } minutes)
            output["reminderMinutesBeforeStart"] = PyJson.Int(minutes);
        return output;
    }

    /// <summary>digest(): sha256 of json.dumps(payload, sort_keys=True), 16 hex characters. Byte for byte Python's, or
    /// the first .NET run would rewrite every entry.</summary>
    public static string Digest(JsonObject payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PyJson.Dump(payload, sortKeys: true))))[..16];

    /// <summary>stamped(): the payload with team-leave's properties: what a create, update or adoption writes.</summary>
    public static JsonObject Stamped(JsonObject payload, string absenceId)
    {
        var output = (JsonObject)payload.DeepClone();
        output["singleValueExtendedProperties"] = new JsonArray(
            new JsonObject { ["id"] = AbsenceProp, ["value"] = absenceId },
            new JsonObject { ["id"] = HashProp, ["value"] = Digest(payload) });
        return output;
    }

    /// <summary>differs(): whether an owned entry needs rewriting: what was written has changed, or someone edited the
    /// title or dates by hand (the body says edits are overwritten).</summary>
    public static bool Differs(Entry current, JsonObject payload)
    {
        var wantStart = PyBits.DateFromIso(PyStr.Head(Py.Str(payload["start"]!["dateTime"]), 10));
        var wantEnd = PyBits.DateFromIso(PyStr.Head(Py.Str(payload["end"]!["dateTime"]), 10)).AddDays(-1);
        return current.Hash != Digest(payload) || current.Subject != Py.Str(payload["subject"]) || !current.AllDay
               || current.Start != wantStart || current.End != wantEnd;
    }

    /// <summary>hand_pattern(): a hand-made leave entry for this person: "&lt;Name&gt; annual leave" or "&lt;Name&gt; on
    /// leave", optionally with the half-day marks the titles use, with or without brackets. Python's
    /// <c>^\s*NAME\s+(annual leave|on leave)(\s*\(?\s*((from|to)\s+)?(am|pm)\s*\)?)*\s*$</c> with re.IGNORECASE,
    /// spelt out so .NET matches exactly what Python's re matches: Python's case-insensitive matching (CaseFold), its
    /// \s (which adds U+001C to U+001F), and its $ (the end, or before a final newline, as .NET's). Run without
    /// backtracking, so no title can make it slow (and built once per name: building one takes a few milliseconds).</summary>
    public static Regex HandPattern(string name) => HandPatterns.GetOrAdd(name, Build);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> HandPatterns = new(StringComparer.Ordinal);

    private static Regex Build(string name)
    {
        const string s = @"[\s\x1C-\x1F]";
        const string open = @"\(", close = @"\)";
        string F(string text) => CaseFold.Pattern(text);
        var pattern = $"^{s}*{F(name)}{s}+({F("annual leave")}|{F("on leave")})"
                      + $"({s}*{open}?{s}*(({F("from")}|{F("to")}){s}+)?({F("am")}|{F("pm")}){s}*{close}?)*{s}*$";
        return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}
