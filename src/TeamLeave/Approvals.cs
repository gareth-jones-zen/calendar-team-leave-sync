using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PeopleFirstClient;
using PyCompat;

namespace TeamLeave;

/// <summary>Who's in on one working day a request covers (the requester never is).</summary>
public sealed record CoverDay(DateOnly Day, List<string> In);

/// <summary>What the rules say about one request: approve, or ask you, with every reason that applies, and the cover.</summary>
public sealed record Decision(LeaveRequest Request, bool Approve, List<string> Reasons, List<CoverDay> Cover, int WorkingDays);

/// <summary>How an approval went. Approved is confirmed by reading it back; Unknown may or may not have happened, and is
/// never sent again; Failed certainly didn't happen; Journaled was recorded in journal mode and not sent.</summary>
public enum ApprovalOutcome
{
    Approved,
    Unknown,
    Failed,
    Journaled,
}

/// <summary>One approval's outcome, a line for you on what happened, and (when approved and of a [types] type) the
/// absence as the calendar will have it.</summary>
public sealed record ApprovalResult(ApprovalOutcome Outcome, string Message, Absence? Absence);

/// <summary>What a run's approvals step did: its report, and the absences it approved, which the same run puts in the
/// calendar.</summary>
public sealed record ApprovalRun(JsonObject Report, List<Absence> Approved);

/// <summary>Leave approvals by rules ([approvals]).
///
/// <b>A request is approved only if every rule holds</b>; otherwise it's asked of you, once, with every reason that
/// applies and who's in on each working day. <b>Nothing is ever rejected</b>, and <b>never more than max_per_run are
/// approved in a run</b>. The rules:
/// - its type is one of [approvals] types;
/// - it doesn't change leave already approved (its history has no approval, auto-approval or modification);
/// - on every working day it covers, at least min_in of the team are in. Away means any absence that isn't rejected,
///   approved or still waiting (so two requests that together leave nobody in are both asked), and a half day counts as
///   a whole one. Working days are Monday to Friday: bank holidays aren't left out, which only makes this stricter;
/// - it's no more than max_working_days working days;
/// - it's at least notice_days' notice (calendar days from today to its first day), or short_notice_ok_days working
///   days or fewer (GJ: "if it's just 1-2 days and is less than a week's notice that's fine");
/// - it doesn't overlap a protected range;
/// - and its cover can be checked: it doesn't start before today or run past days_ahead.
///
/// <b>Approving</b> is People First's own resource template: GET /hrm/absences/{id}/approved/resourcetemplate, set
/// data.absences.id, POST it to the template's meta.links.create.href (through the write door), then read that person's
/// listing back and confirm the absence is Approved. <b>A POST that may have reached People First is never sent
/// again</b>: it's recorded as unknown, and the next run reads it back. Still waiting then, it's asked of you ("the
/// approval may not have gone through"), never posted again.
///
/// <b>Recorded</b> in the state file, only while a request is waiting (or unconfirmed): its id, the person's People
/// First id, its dates and day parts, the outcome and when. Never the name, the type or the reason. Only a real, live
/// run records outcomes and notifies; a dry run and journal mode record nothing.</summary>
public static partial class Approvals
{
    public const string PendingState = "AwaitingApproval";
    public const string ApprovedState = "Approved";

    /// <summary>The path a template's create link must have (its query is People First's, used as given).</summary>
    public const string ApprovePath = "/hrm/absences/approved";

    // What the state file records for each request.
    public const string Approved = "approved";
    public const string Asked = "asked";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
    public const string WouldApprove = "would-approve";

    private static readonly string[] ChangesKinds = ["approval", "creationAndAutoApproval", "modification"];

    /// <summary>An id that can go in a path as it is: letters, digits, '-' and '_' (People First's ids are GUIDs).</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,100}\z", RegexOptions.CultureInvariant)]
    public static partial Regex SafeId();

    public static bool IsWorkingDay(DateOnly day) => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>Monday to Friday from start to end, inclusive (a half day counts as one).</summary>
    public static int WorkingDays(DateOnly start, DateOnly end)
    {
        var count = 0;
        for (var day = start; day <= end; day = day.AddDays(1))
            if (IsWorkingDay(day))
                count++;
        return count;
    }

    /// <summary>Whether an absence's history (/hrm/absences/{id}/absenceChanges) shows it changes leave already approved:
    /// any approval, auto-approval or modification. Null if the history names no changes at all (an unknown shape):
    /// the caller asks rather than guess.</summary>
    public static bool? ChangesApproved(JsonNode? history)
    {
        var kinds = new List<string>();
        Collect(history, kinds);
        return kinds.Count == 0 ? null : kinds.Any(k => ChangesKinds.Contains(k, StringComparer.OrdinalIgnoreCase));
    }

    private static void Collect(JsonNode? node, List<string> kinds)
    {
        if (node is JsonObject o)
        {
            foreach (var (key, value) in o)
            {
                if (key == "changeType" && value is JsonValue v && v.TryGetValue<string>(out var kind))
                    kinds.Add(kind);
                else
                    Collect(value, kinds);
            }
        }
        else if (node is JsonArray a)
            foreach (var item in a)
                Collect(item, kinds);
    }

    /// <summary>The rules, for one request: pure. <paramref name="changesApproved"/> null means its history couldn't be
    /// read. The cover lists each working day the request covers from today to <paramref name="windowEnd"/>.</summary>
    public static Decision Decide(LeaveRequest request, IReadOnlyList<Person> team, IReadOnlyList<Away> away, bool? changesApproved,
        DateOnly today, DateOnly windowEnd, ApprovalSettings settings)
    {
        var reasons = new List<string>();
        if (!settings.Types.Contains(request.Type, StringComparer.Ordinal))
            reasons.Add("it isn't a type the rules approve");
        if (changesApproved is null)
            reasons.Add("its history couldn't be read, so it may change leave already approved");
        else if (changesApproved.Value)
            reasons.Add("it changes leave that was already approved");

        var cover = new List<CoverDay>();
        var thin = new List<DateOnly>();
        for (var day = request.Start; day <= request.End; day = day.AddDays(1))
        {
            if (!IsWorkingDay(day) || day < today || day > windowEnd)
                continue;
            var present = team.Where(p => p.Id != request.Person.Id && !away.Any(a => a.PersonId == p.Id && a.Start <= day && day <= a.End))
                .Select(p => p.Name).ToList();
            cover.Add(new CoverDay(day, present));
            if (present.Count < settings.MinIn)
                thin.Add(day);
        }
        if (thin.Count > 0)
            reasons.Add(settings.MinIn == 1 ? $"it leaves nobody in on {Days(thin)}" : $"it leaves fewer than {settings.MinIn} in on {Days(thin)}");
        if (request.Start < today)
            reasons.Add("it started before today, so its cover can't all be checked");
        if (request.End > windowEnd)
            reasons.Add("it runs past days_ahead, so its cover can't all be checked");

        var workingDays = WorkingDays(request.Start, request.End);
        if (workingDays > settings.MaxWorkingDays)
            reasons.Add($"it's {workingDays} working days, more than {settings.MaxWorkingDays}");
        var notice = request.Start.DayNumber - today.DayNumber;
        if (notice < settings.NoticeDays && workingDays > settings.ShortNoticeOkDays)
            reasons.Add($"it's {Notice(notice)} for {workingDays} working days (less than {settings.NoticeDays} days' notice is fine "
                        + $"only for {settings.ShortNoticeOkDays} working days or fewer)");
        foreach (var range in settings.Protected)
            if (request.Start <= range.End && range.Start <= request.End)
                reasons.Add($"it overlaps {(range.Why.Length > 0 ? range.Why : "protected dates")} ({Sync.Fmt(range.Start, range.End)})");
        return new Decision(request, reasons.Count == 0, reasons, cover, workingDays);
    }

    private static string Notice(int days) => days switch
    {
        <= 0 => "no notice",
        1 => "1 day's notice",
        _ => $"{days} days' notice",
    };

    private static string Days(List<DateOnly> days) => string.Join(", ", days.Select(d => PyTime.Strftime(d, "%d/%m")));

    /// <summary>Approve one request: its template, the POST through the writer, and the read-back. Never throws for
    /// People First's answers (they become the outcome), except a WriteRefusedException, which is a bug.</summary>
    public static ApprovalResult Approve(Config config, LeaveRequest request, IPeopleFirstApi reads, IPeopleFirstWriter writer, WriteMode mode,
        DateOnly today, Action<string> log)
    {
        if (!SafeId().IsMatch(request.Id))
            return new(ApprovalOutcome.Failed, "its id isn't one team-leave can use, so nothing was sent", null);
        JsonNode? template;
        try
        {
            template = reads.Get($"/hrm/absences/{request.Id}/approved/resourcetemplate");
        }
        catch (Exception error) when (error is not (WriteRefusedException or NotSupportedException))
        {
            log($"approval of absence {request.Id}: its template couldn't be read{StatusOf(error)}");
            return new(ApprovalOutcome.Failed, "its approval form couldn't be read from People First, so nothing was sent", null);
        }
        var href = At(template, "meta", "links", "create", "href") is JsonValue h && h.TryGetValue<string>(out var link) ? link : null;
        if (href is null || href.Split('?')[0] != ApprovePath || At(template, "data", "absences") is not JsonObject)
        {
            log($"approval of absence {request.Id}: the template has changed shape; nothing sent");
            return new(ApprovalOutcome.Failed, "People First's approval form has changed shape, so nothing was sent", null);
        }
        var body = (JsonObject)template!.DeepClone();
        body["data"]!["absences"]!["id"] = request.Id;
        try
        {
            writer.Post(href, body);
        }
        catch (PeopleFirstException error) when (error is not PeopleFirstDeliveryUnknownException
                                                 && (error.NeverSent || error.Status is >= 400 and <= 499))
        {
            log($"approval of absence {request.Id} failed{StatusOf(error)}");
            return new(ApprovalOutcome.Failed, error.NeverSent
                ? "People First couldn't be reached, so nothing was sent"
                : $"People First refused it ({error.Status})", null);
        }
        catch (Exception error) when (error is not (WriteRefusedException or NotSupportedException))
        {
            log($"approval of absence {request.Id} may not have gone through{StatusOf(error)}; not sent again");
            return new(ApprovalOutcome.Unknown, "the approval was sent, but its answer was lost: the next run checks it, and never sends it again", null);
        }
        if (mode == WriteMode.Journal)
            return new(ApprovalOutcome.Journaled, "journal mode: recorded, not sent", null);
        JsonNode? listing;
        try
        {
            listing = reads.Events(request.Person.Id, today, today.AddDays(config.DaysAhead));
        }
        catch (Exception error) when (error is not (WriteRefusedException or NotSupportedException))
        {
            log($"approval of absence {request.Id} sent, but couldn't be read back{StatusOf(error)}");
            return new(ApprovalOutcome.Unknown, "the approval was sent, but People First couldn't be read back to confirm it: the next run checks it", null);
        }
        if (Leave.StateOf(listing, request.Id) != ApprovedState)
        {
            log($"approval of absence {request.Id} sent, but not yet shown as approved");
            return new(ApprovalOutcome.Unknown, "the approval was sent, but People First doesn't show it approved yet: the next run checks it", null);
        }
        Absence? absence = null;
        try
        {
            absence = Leave.Absences(config, request.Person, listing).FirstOrDefault(a => a.Id == request.Id);
        }
        catch (LeaveError)
        {
            // approved; the next sync reads the listing itself
        }
        log($"approved absence {request.Id}");
        return new(ApprovalOutcome.Approved, "approved", absence);
    }

    private static string StatusOf(Exception error) => error is PeopleFirstException { Status: { } status } ? $" ({status})" : "";

    private static JsonNode? At(JsonNode? node, params string[] keys)
    {
        foreach (var key in keys)
            node = node is JsonObject o && o.TryGetPropertyValue(key, out var next) ? next : null;
        return node;
    }

    /// <summary>The run's approvals step: decide each waiting request, approve (if act, through the door, up to
    /// max_per_run), record, notify new asks, and report. <paramref name="reads"/> is fenced; <paramref name="writer"/>
    /// is the door's. A dry run, or a door that isn't Live, records nothing and notifies nothing; a dry run approves
    /// nothing either.</summary>
    public static ApprovalRun Process(Config config, LeaveRead read, IPeopleFirstApi reads, IPeopleFirstWriter writer, WriteMode mode,
        bool dryRun, JsonObject state, DateOnly today, DateTime now, Action<string> log, Action<JsonObject>? save = null,
        Action<string, IEnumerable<string>>? alert = null)
    {
        var settings = config.Approvals ?? throw new InvalidOperationException("No [approvals] in the settings.");
        var recording = !dryRun && mode == WriteMode.Live;
        var act = settings.Act && !dryRun;
        var windowEnd = today.AddDays(config.DaysAhead);
        var records = (JsonObject)(state["approvals"] as JsonObject ?? []).DeepClone();
        List<JsonObject> approved = [], would = [], deferred = [], asked = [], unknown = [];
        var newlyApproved = new List<Absence>();

        void Persist()
        {
            if (!recording)
                return;
            state["approvals"] = records.DeepClone();
            save?.Invoke(state);
        }

        // Records for requests no longer waiting: an unconfirmed approval now approved is confirmed; all are dropped.
        var waiting = read.Requests.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, node) in records.ToList())
        {
            if (waiting.Contains(id))
                continue;
            if (Outcome(node) == Unknown && read.ApprovedIds.Contains(id))
            {
                approved.Add(Confirmed(config, read, id, node, now));
                log($"approval of absence {id} confirmed");
            }
            records.Remove(id);
        }

        var attempts = 0;
        foreach (var request in read.Requests.OrderBy(r => r.Start).ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            var record = records[request.Id] as JsonObject;
            var outcome = record is not null && SameDates(record, request) ? Outcome(record) : null;
            var decision = Decide(request, read.People, read.Away, History(reads, request.Id, log), today, windowEnd, settings);
            switch (outcome)
            {
                case Asked or Failed:   // already with you: not raised again, and not approved by the rules
                {
                    List<string> reasons = decision.Approve ? ["the rules would approve it now, but it's already with you"] : decision.Reasons;
                    if (outcome == Failed)
                        reasons = ["approving it failed earlier", .. reasons];
                    asked.Add(Item(config, decision, false, reasons));
                    continue;
                }
                case Unknown:
                    asked.Add(Item(config, decision, true,
                        [$"an approval was sent {When(record!)}, but People First still shows it waiting: check it there before approving it again",
                         .. decision.Reasons]));
                    Record(records, request, Asked, now);
                    continue;
                case Approved:
                    asked.Add(Item(config, decision, true, ["it was approved, and is waiting for approval again", .. decision.Reasons]));
                    Record(records, request, Asked, now);
                    continue;
            }
            if (!decision.Approve)
            {
                asked.Add(Item(config, decision, true, decision.Reasons));
                Record(records, request, Asked, now);
                continue;
            }
            if (!act)
            {
                would.Add(Item(config, decision, outcome != WouldApprove, []));
                Record(records, request, WouldApprove, now);
                continue;
            }
            if (attempts >= settings.MaxPerRun)
            {
                deferred.Add(Item(config, decision, false, [$"more than max_per_run ({settings.MaxPerRun}) this run: the next run approves it"]));
                continue;
            }
            attempts++;
            var result = Approve(config, request, reads, writer, mode, today, log);
            switch (result.Outcome)
            {
                case ApprovalOutcome.Approved:
                    approved.Add(Item(config, decision, true, []));
                    Record(records, request, Approved, now);
                    if (result.Absence is not null)
                        newlyApproved.Add(result.Absence);
                    break;
                case ApprovalOutcome.Unknown:
                    unknown.Add(Item(config, decision, true, [result.Message]));
                    Record(records, request, Unknown, now);
                    break;
                case ApprovalOutcome.Failed:
                    asked.Add(Item(config, decision, true, [$"approving it failed: {result.Message}"]));
                    Record(records, request, Failed, now);
                    break;
                default:
                    var item = Item(config, decision, true, [result.Message]);
                    item["journal"] = true;
                    would.Add(item);
                    break;
            }
            Persist();
        }
        Persist();

        var fresh = asked.Where(i => i["new"]!.GetValue<bool>()).ToList();
        if (recording && settings.Notify && alert is not null && fresh.Count > 0)
            alert(fresh.Count == 1 ? "A leave request needs you" : $"{fresh.Count} leave requests need you",
                fresh.Select(i => $"{Py.Str(i["text"])}: {i["reasons"]!.AsArray().Select(r => Py.Str(r)).FirstOrDefault()}"));
        log($"approvals: {read.Requests.Count} waiting, {approved.Count} approved, {would.Count} would be approved, {deferred.Count} left "
            + $"for the next run, {asked.Count} with you ({fresh.Count} new), {unknown.Count} unconfirmed");

        var report = new JsonObject
        {
            ["act"] = settings.Act,
            ["approved"] = Array(approved),
            ["would_approve"] = Array(would),
            ["deferred"] = Array(deferred),
            ["asked"] = Array(asked),
            ["unknown"] = Array(unknown),
        };
        report["summary"] = Summary(report);
        return new ApprovalRun(report, newlyApproved);
    }

    private static JsonArray Array(IEnumerable<JsonObject> items) => new(items.Cast<JsonNode?>().ToArray());

    /// <summary>Whether a run's approvals report has anything new for you (for the after-run hook).</summary>
    public static bool HasNews(JsonObject? report) =>
        report is not null && new[] { "approved", "would_approve", "asked", "unknown" }
            .Any(key => report[key] is JsonArray items && items.Any(i => i?["new"]?.GetValue<bool>() == true));

    public static string Summary(JsonObject report)
    {
        int Count(string key) => report[key]!.AsArray().Count;
        var parts = new List<string>();
        if (Count("approved") > 0)
            parts.Add($"{Count("approved")} approved");
        if (Count("would_approve") > 0)
            parts.Add($"{Count("would_approve")} would be approved");
        if (Count("deferred") > 0)
            parts.Add($"{Count("deferred")} left for the next run");
        if (Count("asked") > 0)
            parts.Add($"{Count("asked")} with you ({report["asked"]!.AsArray().Count(i => i!["new"]!.GetValue<bool>())} new)");
        if (Count("unknown") > 0)
            parts.Add($"{Count("unknown")} sent but not confirmed");
        return parts.Count == 0 ? "no leave requests waiting" : string.Join(", ", parts);
    }

    /// <summary>Record an outcome for a request: its id, the person's People First id, its dates and parts, the outcome
    /// and when. Nothing else.</summary>
    public static void Record(JsonObject records, LeaveRequest request, string outcome, DateTime now, bool byHand = false)
    {
        var record = new JsonObject
        {
            ["person"] = request.Person.Id,
            ["start"] = PyTime.Iso(request.Start),
            ["end"] = PyTime.Iso(request.End),
            ["start_part"] = request.StartPart,
            ["end_part"] = request.EndPart,
            ["outcome"] = outcome,
            ["at"] = now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        };
        if (byHand)
            record["by"] = "hand";
        records[request.Id] = record;
    }

    /// <summary>The approvals records in a state file (a copy), empty if there are none.</summary>
    public static JsonObject Records(JsonObject state) => (JsonObject)(state["approvals"] as JsonObject ?? []).DeepClone();

    private static string? Outcome(JsonNode? record) => record?["outcome"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    private static bool SameDates(JsonObject record, LeaveRequest request) =>
        Text(record["start"]) == PyTime.Iso(request.Start) && Text(record["end"]) == PyTime.Iso(request.End)
        && Text(record["start_part"]) == request.StartPart && Text(record["end_part"]) == request.EndPart;

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    private static string When(JsonObject record) =>
        DateTime.TryParseExact(Text(record["at"]), "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? $"on {at:dd/MM} at {at:HH:mm}"
            : "earlier";

    private static bool? History(IPeopleFirstApi reads, string id, Action<string> log)
    {
        if (!SafeId().IsMatch(id))
            return null;
        try
        {
            return ChangesApproved(reads.Get($"/hrm/absences/{id}/absenceChanges"));
        }
        catch (Exception error) when (error is not (WriteRefusedException or NotSupportedException))
        {
            log($"history of absence {id} couldn't be read{StatusOf(error)}");
            return null;
        }
    }

    /// <summary>A report line for one request: who, when, what the rules found, and who's in each working day. Names and
    /// dates, never the type.</summary>
    public static JsonObject Item(Config config, Decision decision, bool isNew, IEnumerable<string> reasons)
    {
        var request = decision.Request;
        return new JsonObject
        {
            ["id"] = request.Id,
            ["name"] = request.Person.Name,
            ["start"] = PyTime.Iso(request.Start),
            ["end"] = PyTime.Iso(request.End),
            ["text"] = $"{request.AsAbsence(config).Title(config)} {Sync.Fmt(request.Start, request.End)}",
            ["working_days"] = decision.WorkingDays,
            ["new"] = isNew,
            ["reasons"] = new JsonArray(reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            ["cover"] = new JsonArray(decision.Cover.Select(day => (JsonNode?)new JsonObject
            {
                ["date"] = PyTime.Iso(day.Day),
                ["in"] = new JsonArray(day.In.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
            }).ToArray()),
        };
    }

    /// <summary>A line for an approval sent earlier and confirmed now: from the calendar's absence if it's one, else
    /// from the record.</summary>
    private static JsonObject Confirmed(Config config, LeaveRead read, string id, JsonNode? record, DateTime now)
    {
        var person = read.People.FirstOrDefault(p => p.Id == Text(record?["person"])) ?? new Person(Text(record?["person"]) ?? "", "");
        LeaveRequest request;
        if (read.Absences.FirstOrDefault(a => a.Id == id) is { } absence)
            request = new LeaveRequest(id, absence.Person, config.Types.First(t => t.Value == absence.Template).Key, absence.Start, absence.End,
                absence.StartPart, absence.EndPart);
        else
        {
            var start = PyBits.DateFromIso(Text(record?["start"]) ?? PyTime.Iso(DateOnly.FromDateTime(now)));
            var end = PyBits.DateFromIso(Text(record?["end"]) ?? PyTime.Iso(start));
            request = new LeaveRequest(id, person, "", start, end, Text(record?["start_part"]) ?? Leave.Full, Text(record?["end_part"]) ?? Leave.Full);
        }
        var item = Item(config, new Decision(request, true, [], [], WorkingDays(request.Start, request.End)), true,
            ["the approval sent earlier went through"]);
        item["confirmed_late"] = true;
        return item;
    }
}
