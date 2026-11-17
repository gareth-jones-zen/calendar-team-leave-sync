using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PyCompat;

namespace TeamLeave;

/// <summary>The plan looked wrong, so nothing was written.</summary>
public sealed class SafetyStop(string message) : Exception(message);

/// <summary>What one reconcile would do.</summary>
public sealed class Plan
{
    public List<(Absence Absence, JsonObject Payload)> Create { get; } = [];
    public List<(Absence Absence, JsonObject Payload, Entry Entry)> Update { get; } = [];
    public List<(Absence Absence, JsonObject Payload, Entry Entry)> Adopt { get; } = [];
    public List<Entry> Delete { get; } = [];

    /// <summary>Already entered by hand, left alone.</summary>
    public List<(Absence Absence, List<Entry> Entries)> ByHand { get; } = [];

    /// <summary>Looks like a report's leave, matches no absence.</summary>
    public List<Entry> UnmatchedHand { get; set; } = [];

    public int Unchanged { get; set; }

    public int Writes => Create.Count + Update.Count + Adopt.Count + Delete.Count;
}

/// <summary>One reconcile: People First's leave against the entries team-leave owns in your calendar.
///
/// For each kept absence (Plan):
/// - <b>no owned entry:</b> create one, <b>unless you already made one by hand</b>: an entry you organise, with no
///   attendees, all-day, titled "&lt;Name&gt; annual leave" or "&lt;Name&gt; on leave" (half-day marks allowed), on exactly
///   the same first and last day, <b>or several such entries that between them cover every weekday of it</b>. That
///   suppresses the create. <b>With --adopt it's taken over instead</b>: stamped as team-leave's and rewritten. Only a
///   single exact match starting today or later is adopted;
/// - <b>an owned entry that differs</b>: rewritten;
/// - <b>an owned entry with no absence</b> (cancelled, rejected, or the person left your team): deleted.
///
/// <b>What it never touches:</b> entries without its property (unless adopted), and <b>owned entries that ended
/// before today</b>. Owned entries starting after the window are left too.
///
/// <b>Safety stops, which write nothing at all:</b> a failed People First read (Leave.Read throws); "everything
/// vanished" (no leave at all while team-leave owns future entries); more than max_deletes removals.</summary>
public static class Sync
{
    internal static string Fmt(DateOnly first, DateOnly last) =>
        first == last ? PyTime.Strftime(first, "%d/%m") : $"{PyTime.Strftime(first, "%d/%m")} to {PyTime.Strftime(last, "%d/%m")}";

    /// <summary>_pieces(): hand-made entries that between them cover every weekday of the absence, each lying wholly
    /// inside it. Empty if they don't.</summary>
    private static List<Entry> Pieces(Absence absence, List<Entry> candidates)
    {
        var inside = candidates.Where(e => absence.Start <= e.Start && e.End <= absence.End).ToList();
        if (inside.Count == 0)
            return [];
        for (var day = absence.Start; day <= absence.End; day = day.AddDays(1))
            if (PyTime.Weekday(day) < 5 && !inside.Any(e => e.Start <= day && day <= e.End))
                return [];
        return inside.OrderBy(e => e.Start).ToList();
    }

    /// <summary>plan(): survey reads the whole window for hand-made entries, to report ones that match no leave (a dry
    /// run does). Otherwise hand-made entries are read only across the span of absences still to create.</summary>
    public static Plan MakePlan(Config config, IReadOnlyList<Person> people, IReadOnlyList<Absence> absences, ICalendarReads calendar,
        DateOnly today, bool adopt = false, long? maxDeletes = null, bool survey = false)
    {
        var windowEnd = today.AddDays(config.DaysAhead);
        var output = new Plan();
        var owned = new List<KeyValuePair<string, Entry>>();
        foreach (var item in calendar.Owned())
        {
            if (item.End < today || item.Start > windowEnd)
                continue;   // the past, and anything beyond the window, are never touched
            if (owned.Any(o => o.Key == item.Absence))
                output.Delete.Add(item);   // a second entry for one absence (a run cut short mid-write): tidy it
            else
                owned.Add(new(item.Absence, item));
        }
        var wanted = new HashSet<string>(absences.Select(a => a.Id), StringComparer.Ordinal);

        var pending = new List<(Absence Absence, JsonObject Payload)>();
        foreach (var absence in absences)
        {
            var payload = Calendar.Body(config, absence);
            var current = owned.FirstOrDefault(o => o.Key == absence.Id).Value;
            if (current is null)
                pending.Add((absence, payload));
            else if (Calendar.Differs(current, payload))
                output.Update.Add((absence, payload, current));
            else
                output.Unchanged++;
        }
        output.Delete.AddRange(owned.Where(o => !wanted.Contains(o.Key)).Select(o => o.Value));

        // Hand-made entries: only read when there's something to create, or to report on a dry run.
        if (pending.Count > 0 || survey)
        {
            var first = pending.Select(p => p.Absence.Start).Append(today).Min();
            var last = survey ? windowEnd : pending.Max(p => p.Absence.End);
            var hand = calendar.Between(first, last)
                .Where(e => e.Absence.Length == 0 && e.Organiser && e.Attendees == 0 && !e.Cancelled && e.AllDay).ToList();
            var patterns = new Dictionary<string, Regex>(StringComparer.Ordinal);
            foreach (var p in people)
                patterns[p.Name] = Calendar.HandPattern(p.Name);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (absence, payload) in pending)
            {
                var pattern = patterns.GetValueOrDefault(absence.Person.Name) ?? Calendar.HandPattern(absence.Person.Name);
                var match = hand.FirstOrDefault(e => !used.Contains(e.Id) && pattern.IsMatch(e.Subject)
                                                     && e.Start == absence.Start && e.End == absence.End);
                if (match is null)
                {
                    var pieces = Pieces(absence, hand.Where(e => !used.Contains(e.Id) && pattern.IsMatch(e.Subject)).ToList());
                    if (pieces.Count > 0)   // split by hand (around a weekend, say): left alone, never adopted
                    {
                        foreach (var piece in pieces)
                            used.Add(piece.Id);
                        output.ByHand.Add((absence, pieces));
                    }
                    else
                        output.Create.Add((absence, payload));
                    continue;
                }
                used.Add(match.Id);
                if (adopt && match.Start >= today)
                    output.Adopt.Add((absence, payload, match));
                else
                    output.ByHand.Add((absence, [match]));
            }
            output.UnmatchedHand = hand.Where(e => !used.Contains(e.Id) && e.End >= today
                                                   && patterns.Values.Any(p => p.IsMatch(e.Subject))).ToList();
        }

        // Safety stops: nothing is written if either trips.
        var cap = maxDeletes ?? config.MaxDeletes;
        if (absences.Count == 0 && owned.Count > 0 && (maxDeletes is null || maxDeletes < owned.Count))
            throw new SafetyStop($"People First gave no leave at all, but team-leave has {owned.Count} future entries. "
                                 + "Nothing was changed. If everyone's leave really was cancelled, run sync by hand with "
                                 + $"--max-deletes {owned.Count}.");
        if (output.Delete.Count > cap)
            throw new SafetyStop($"{output.Delete.Count} entries would be removed, more than max_deletes ({cap}). Nothing was "
                                 + $"changed. Check with sync --dry-run; if it's right, run sync with --max-deletes {output.Delete.Count}.");
        return output;
    }

    /// <summary>apply(): creates, then updates and adoptions, then deletes, each through the write door's writer, and
    /// each logged by id.</summary>
    public static void Apply(Plan plan, ICalendarWriter writer, Action<string> log)
    {
        foreach (var (absence, payload) in plan.Create)
        {
            writer.Create(Calendar.Stamped(payload, absence.Id));
            log($"created entry for absence {absence.Id}");
        }
        foreach (var (verb, items) in new[] { ("updated", plan.Update), ("adopted", plan.Adopt) })
            foreach (var (absence, payload, current) in items)
            {
                writer.Update(current.Id, Calendar.Stamped(payload, absence.Id));
                log($"{verb} entry for absence {absence.Id}");
            }
        foreach (var item in plan.Delete)
        {
            writer.Delete(item.Id);
            log($"deleted entry for absence {item.Absence}");
        }
    }

    /// <summary>report(): what changed, for the console and the after-run hook: names and dates, never the type. The
    /// keys in Python's order.</summary>
    public static JsonObject Report(Config config, Plan plan, bool dryRun)
    {
        JsonObject Line(Absence absence)
        {
            var title = absence.Title(config);
            return new JsonObject
            {
                ["name"] = absence.Person.Name,
                ["title"] = title,
                ["start"] = PyTime.Iso(absence.Start),
                ["end"] = PyTime.Iso(absence.End),
                ["text"] = $"{title} {Fmt(absence.Start, absence.End)}",
            };
        }
        JsonObject Gone(Entry item) => new()
        {
            ["title"] = item.Subject,
            ["start"] = PyTime.Iso(item.Start),
            ["end"] = PyTime.Iso(item.End),
            ["text"] = $"{item.Subject} {Fmt(item.Start, item.End)}",
        };
        JsonObject Was(Absence a, Entry e)
        {
            var line = Line(a);
            line["was"] = $"{e.Subject} {Fmt(e.Start, e.End)}";
            return line;
        }
        JsonObject ByHand(Absence a, List<Entry> entries)
        {
            var line = Line(a);
            line["entry"] = string.Join(" + ", entries.Select(e => $"{e.Subject} {Fmt(e.Start, e.End)}"));
            line["pieces"] = entries.Count;
            return line;
        }
        static JsonArray Array(IEnumerable<JsonObject> items) => new(items.Cast<JsonNode?>().ToArray());

        var output = new JsonObject
        {
            ["config"] = config.Path,
            ["dry_run"] = dryRun,
            ["added"] = Array(plan.Create.Select(c => Line(c.Absence))),
            ["changed"] = Array(plan.Update.Select(u => Was(u.Absence, u.Entry))),
            ["adopted"] = Array(plan.Adopt.Select(u => Was(u.Absence, u.Entry))),
            ["removed"] = Array(plan.Delete.Select(Gone)),
            ["by_hand"] = Array(plan.ByHand.Select(b => ByHand(b.Absence, b.Entries))),
            ["unmatched_hand"] = Array(plan.UnmatchedHand.Select(Gone)),
            ["unchanged"] = plan.Unchanged,
        };
        output["summary"] = Summary(output);
        return output;
    }

    public static string Summary(JsonObject report)
    {
        int Count(string key) => report[key]!.AsArray().Count;
        var text = $"{Count("added")} added, {Count("changed")} changed, {Count("removed")} removed, {Py.Str(report["unchanged"])} unchanged";
        if (Count("adopted") > 0)
            text += $", {Count("adopted")} adopted";
        if (Count("by_hand") > 0)
            text += $", {Count("by_hand")} already in by hand";
        return text;
    }
}
