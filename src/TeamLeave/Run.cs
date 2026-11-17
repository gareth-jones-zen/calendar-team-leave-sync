using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyCompat;
using ReviewQueue;
using WinBackground;

namespace TeamLeave;

/// <summary>What reading People First gives: your reports, and their kept absences.</summary>
public delegate (List<Person> People, List<Absence> Absences) LeaveReader(Config config, DateOnly today, Action<string> log);

/// <summary>People First opened for a run (signed in), for [approvals]: read through a fence, written to only through
/// the write door. The caller disposes it if it's IDisposable.</summary>
public delegate IPeopleFirstApi PeopleFirstOpener(Config config, Action<string> log);

/// <summary>Runs the after-run hook: (command, stdin, working folder, timeout).</summary>
public delegate Completed HookRunner(IReadOnlyList<string> command, string input, string folder, TimeSpan timeout);

/// <summary>One locked run: read People First, plan, write (unless a dry run), run the hook, and notify problems.
///
/// <b>Notifications are for problems only</b>, once per problem, never every 15 minutes: a sign-in needed (Microsoft or
/// People First), at once; People First or Graph failing three runs in a row; a safety stop, at once. The next success
/// clears the problem, so a recurrence is notified again.
///
/// <b>The state file holds no names, dates or types</b>: the failure count, the problem last notified, and the last
/// run's time and counts. The calendar is the record of what was written. <b>With [approvals]</b>, it also holds each
/// leave request still waiting that the rules have decided on (its id, the person's People First id, its dates and the
/// outcome; see Approvals), and nothing about it once it's no longer waiting.</summary>
public static class Runner
{
    public const int FailuresBeforeNotice = 3;
    public const double LockWaitSeconds = 600;
    public static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(600);

    /// <summary>The run lock, named as the Python version named it (win_background's key(), with Python's lower-casing),
    /// so a .NET run and a Python run of the same settings exclude each other.</summary>
    public static string RunLockName(Config config) => $"team-leave-run-{LockKey(config.Path)}";

    internal static string LockKey(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PyStr.Lower(text))))[..16];

    /// <summary>load_state(): empty if it's missing or isn't JSON. Text that isn't UTF-8 is a PyValueError, as Python's
    /// UnicodeDecodeError was; JSON that isn't an object is refused too (Python failed on it later).</summary>
    public static JsonObject LoadState(Config config)
    {
        string text;
        try
        {
            text = PyJson.ReadStrict(config.StateFile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        catch (DecoderFallbackException error)
        {
            throw new PyValueError($"{config.StateFile} isn't UTF-8: {error.Message}");
        }
        JsonNode? parsed;
        try
        {
            parsed = PyJson.Parse(PyStr.UniversalNewlines(text));
        }
        catch (JsonException)
        {
            return [];
        }
        return parsed as JsonObject ?? throw new PyValueError($"{config.StateFile} doesn't hold a JSON object.");
    }

    /// <summary>save_state(): json.dumps(state, indent=1), written through a .tmp beside it and moved into place.</summary>
    public static void SaveState(Config config, JsonObject state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(config.StateFile)!);
        var temp = PyBits.WithSuffix(config.StateFile, ".tmp");
        PyStr.WriteText(temp, PyJson.Dump(state, indent: 1));
        File.Move(temp, config.StateFile, overwrite: true);
    }

    /// <summary>in_hours(): a scheduled run outside hours (and at weekends, with weekdays) does nothing.</summary>
    public static bool InHours(Config config, DateTime now)
    {
        if (config.Weekdays && PyTime.Weekday(now) >= 5)
            return false;
        var clock = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        return string.CompareOrdinal(config.Hours.From, clock) <= 0 && string.CompareOrdinal(clock, config.Hours.To) < 0;
    }

    /// <summary>People First, read for real: a hidden Edge on the settings' profile for the token (the profile's lock
    /// held while Edge is open), then the API.</summary>
    public static (List<Person> People, List<Absence> Absences) ReadPeopleFirst(Config config, DateOnly today, Action<string> log)
    {
        using var api = (PeopleFirstApi)OpenPeopleFirst(config, log);
        return Leave.Read(config, api, today, log);
    }

    /// <summary>People First signed in for a run, as ReadPeopleFirst signs in: the token from a hidden Edge, then the API
    /// (dispose it when done).</summary>
    public static IPeopleFirstApi OpenPeopleFirst(Config config, Action<string> log) =>
        new PeopleFirstApi(PeopleFirstClient.Session.OpenAsync(config.PeopleFirst,
            new PeopleFirstClient.SessionOptions { Log = log, LockWaitSeconds = LockWaitSeconds }).GetAwaiter().GetResult());

    /// <summary>sync_once(): read, plan and (unless dry run) write through the door. Throws on any failure or safety
    /// stop, having written nothing for a failed read or a stop.
    /// <para><b>With [approvals]</b>, People First is opened through <paramref name="open"/> instead of
    /// <paramref name="read"/>: read (the same listings, through a fence), then the waiting requests decided and, if
    /// act, approved through the door, <b>before</b> the calendar plan, so an absence approved now gets its entry in
    /// this run. Outcomes are saved to <paramref name="state"/> as they happen (not on a dry run), so a failure later
    /// in the run can't lose one.</para></summary>
    public static JsonObject SyncOnce(Config config, IGraph graph, WriteDoor door, LeaveReader read, DateOnly today, Action<string> log,
        bool dryRun = false, bool adopt = false, long? maxDeletes = null, PeopleFirstOpener? open = null, JsonObject? state = null,
        DateTime? now = null, Action<string, IEnumerable<string>>? alert = null)
    {
        List<Person> people;
        List<Absence> absences;
        JsonObject? approvals = null;
        if (config.Approvals is null)
            (people, absences) = read(config, today, log);
        else
        {
            if (open is null)
                throw new InvalidOperationException("[approvals] needs People First opened for the run.");
            var api = open(config, log);
            try
            {
                var fenced = new ReadOnlyPeopleFirst(api);
                var found = Leave.ReadAll(config, fenced, today, log);
                var done = Approvals.Process(config, found, fenced, door.PeopleFirst(api), door.Mode, dryRun, state ?? [], today,
                    now ?? DateTime.Now, log, dryRun ? null : s => SaveState(config, s), alert);
                people = found.People;
                absences = [.. found.Absences.Where(a => !done.Approved.Any(n => n.Id == a.Id)), .. done.Approved];
                approvals = done.Report;
            }
            finally
            {
                (api as IDisposable)?.Dispose();
            }
        }
        var calendar = new Calendar(graph, door.Writer, config.Event.TimeZone);
        var plan = Sync.MakePlan(config, people, absences, calendar, today, adopt, maxDeletes, survey: dryRun);
        log($"plan: {plan.Create.Count} to create, {plan.Update.Count} to update, {plan.Adopt.Count} to adopt, "
            + $"{plan.Delete.Count} to delete, {plan.Unchanged} unchanged, {plan.ByHand.Count} already in by hand");
        if (!dryRun)
            Sync.Apply(plan, calendar, log);
        var report = Sync.Report(config, plan, dryRun);
        if (approvals is not null)
            report["approvals"] = approvals;
        return report;
    }

    public static string Problem(Exception error) => error switch
    {
        M365Graph.SignInNeededException or PeopleFirstClient.SignInNeededException => "sign-in",
        SafetyStop => "safety",
        _ => "failure",
    };

    /// <summary>run(): a locked sync with the state kept and problems notified (when scheduled). Returns the report, or
    /// null if a scheduled run fell outside working hours. Re-throws the error after recording it.
    /// <para>The door decides what a run may write; a dry run is always given a refusing one by the command line. With
    /// a journal door the hook is never run, because nothing really changed.</para></summary>
    public static JsonObject? Run(Config config, IGraph graph, WriteDoor door, LeaveReader read, Action<string> log,
        bool dryRun = false, bool adopt = false, bool hook = false, bool scheduled = false, long? maxDeletes = null,
        Func<DateTime>? now = null, DateOnly? today = null, Action<string, IEnumerable<string>>? alert = null, HookRunner? runner = null,
        PeopleFirstOpener? open = null)
    {
        now ??= () => DateTime.Now;
        alert ??= Toast.Notify;
        if (scheduled && !InHours(config, now()))
            return null;
        JsonObject report;
        using (var held = new NamedLock(RunLockName(config)))
        {
            if (!held.Acquire(LockWaitSeconds))
                throw new TimeoutException($"Another team-leave sync for {config.Path} didn't finish within {LockWaitSeconds / 60:0} minutes.");
            var state = LoadState(config);
            try
            {
                report = SyncOnce(config, graph, door, read, today ?? DateOnly.FromDateTime(now()), log, dryRun, adopt, maxDeletes,
                    open, state, now(), alert);
            }
            catch (Exception error)
            {
                var kind = Problem(error);
                var message = PyStr.Head(PyBits.Text(error), 300);
                state["failures"] = Increment(state["failures"]);
                state["last_error"] = $"{PyBits.TypeName(error)}: {message}";
                state["last_run"] = PyTime.Iso(Seconds(now()), "seconds");
                log($"sync failed ({kind}): {message}");
                var failures = state["failures"];
                var due = kind is "sign-in" or "safety" || AtLeast(failures, FailuresBeforeNotice);
                var notified = state.TryGetPropertyValue("notified", out var n) ? n : null;
                if (scheduled && due && !(notified is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == kind))
                {
                    var title = kind switch
                    {
                        "sign-in" => "team-leave needs a sign-in",
                        "safety" => "team-leave stopped: nothing was changed",
                        _ => $"team-leave has failed {Py.Str(failures)} times in a row",
                    };
                    alert(title, [PyStr.Head(PyBits.Text(error), 400)]);
                    state["notified"] = kind;
                }
                if (!dryRun)
                    SaveState(config, state);
                throw;
            }
            log(Py.Str(report["summary"]) + (dryRun ? " (dry run: nothing written)" : ""));
            if (!dryRun)
            {
                var fresh = new JsonObject
                {
                    ["last_run"] = PyTime.Iso(Seconds(now()), "seconds"),
                    ["failures"] = 0,
                    ["last_result"] = Py.Str(report["summary"]),
                };
                if (config.Approvals is not null && state["approvals"] is JsonObject records)
                    fresh["approvals"] = records.DeepClone();   // kept while [approvals] is on; dropped with it
                SaveState(config, fresh);
            }
        }
        var changed = new[] { "added", "changed", "removed", "adopted" }.Any(key => report[key]!.AsArray().Count > 0)
                      || Approvals.HasNews(report["approvals"] as JsonObject);
        if (hook && !dryRun && changed)
        {
            if (config.AfterRun.Count > 0)
            {
                if (door.Mode == WriteMode.Journal)
                    log("after-run hook not run: journal mode, nothing was written");
                else
                    AfterRun(config, report, log, runner);
            }
            if (config.Review is not null)
            {
                if (door.Mode == WriteMode.Journal)
                    log("review queue not written: journal mode, nothing was written");
                else
                    Review(config, report, log);
            }
        }
        return report;
    }

    private static DateTime Seconds(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);

    /// <summary>state.get("failures", 0) + 1, for the number types json gives (bool counts as 0 or 1, as in Python).</summary>
    private static JsonNode Increment(JsonNode? value)
    {
        if (value is null)
            return 1;
        if (value is JsonValue v)
            switch (v.GetValueKind())
            {
                case JsonValueKind.True:
                    return 2;
                case JsonValueKind.False:
                    return 1;
                case JsonValueKind.Number:
                    var text = Py.Str(v);
                    if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                        return PyJson.Int(whole + 1);
                    return PyJson.Float(Py.Float(v) + 1);
            }
        throw new InvalidOperationException("The state file's failure count isn't a number.");
    }

    private static bool AtLeast(JsonNode? value, int limit) => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && Py.Float(v) >= limit;

    /// <summary>[review]'s items for a run's report, in order: one per calendar change ("added", "changed",
    /// "adopted", "removed": its text and what happened, with start and end), then one per approval reported for the
    /// first time ("approval" by rule, "approval-preview", "approval-needed" with the reasons, "approval-unconfirmed"),
    /// with the absence's id and who's in on each working day. Deferred approvals wait for the next run.</summary>
    public static IReadOnlyList<ReviewItem> ReviewItems(JsonObject report)
    {
        var output = new List<ReviewItem>();
        static JsonArray List(JsonObject from, string key) => from[key] as JsonArray ?? [];
        void Change(string key, string type, Func<JsonNode, string> title)
        {
            foreach (var item in List(report, key))
                output.Add(new ReviewItem(type, new JsonObject
                {
                    ["title"] = title(item!), ["start"] = item!["start"]?.DeepClone(), ["end"] = item["end"]?.DeepClone(),
                }));
        }
        Change("added", "added", i => $"{Py.Str(i["text"])} added");
        Change("changed", "changed", i => $"{Py.Str(i["text"])} changed (was {Py.Str(i["was"])})");
        Change("adopted", "adopted", i => $"{Py.Str(i["text"])} taken over from the hand-made entry");
        Change("removed", "removed", i => $"{Py.Str(i["text"])} removed (no longer approved in People First)");
        var approvals = Py.Truthy(report["approvals"]) ? report["approvals"]!.AsObject() : [];
        var shapes = new (string Key, string Type, Func<JsonNode, string> Title)[]
        {
            ("approved", "approval", i => $"{Py.Str(i["text"])}: approved by rule"),
            ("would_approve", "approval-preview", i => $"{Py.Str(i["text"])}: would be approved (preview)"),
            ("asked", "approval-needed", i => $"{Py.Str(i["text"])}: needs your decision "
                                              + $"({string.Join("; ", (i["reasons"] as JsonArray ?? []).Select(r => Py.Str(r)))})"),
            ("unknown", "approval-unconfirmed", i => $"{Py.Str(i["text"])}: approval sent, not confirmed yet"),
        };
        foreach (var (key, type, title) in shapes)
        {
            foreach (var item in List(approvals, key))
            {
                if (!Py.Truthy(item!["new"]))
                    continue;
                var cover = string.Join("; ", (item["cover"] as JsonArray ?? []).Select(c =>
                {
                    var names = string.Join(", ", c!["in"]!.AsArray().Select(n => Py.Str(n)));
                    return $"{Py.Str(c["date"])}: {(names.Length > 0 ? names : "nobody")}";
                }));
                output.Add(new ReviewItem(type, new JsonObject
                {
                    ["title"] = title(item), ["start"] = item["start"]?.DeepClone(), ["end"] = item["end"]?.DeepClone(),
                    ["absence"] = item["id"]?.DeepClone(), ["cover"] = cover,
                }));
            }
        }
        return output;
    }

    /// <summary>[review]: each of the run's items added under the settings' source (the same item twice is kept once).
    /// Logged with a count only, as the log holds no names or dates; nothing it does stops the run.</summary>
    public static void Review(Config config, JsonObject report, Action<string> log)
    {
        if (config.Review is not { } review)
            return;
        try
        {
            var items = ReviewItems(report);
            var queue = review.Open();
            foreach (var item in items)
                queue.Add(review.Source, item.Type, item.Fields);
            log($"review: queued {items.Count} leave item(s) under {review.Source}");
        }
        catch (Exception error)
        {
            log($"review queue failed: {error.Message}");
        }
    }

    /// <summary>after_run(): the report as JSON on stdin, from the settings' folder, with no window. Its output is
    /// logged a line at a time; a failure to run it is logged, never raised.</summary>
    public static void AfterRun(Config config, JsonObject report, Action<string> log, HookRunner? runner = null)
    {
        runner ??= (command, input, folder, timeout) => Shell.Run(command[0], command.Skip(1), timeout, input, folder, utf8: true);
        log($"after-run hook: {string.Join(' ', config.AfterRun)}");
        try
        {
            var done = runner(config.AfterRun, PyJson.Dump(report), Path.GetDirectoryName(config.Path)!, HookTimeout);
            foreach (var line in PyStr.SplitLines(done.StandardOutput + done.StandardError))
            {
                var text = PyStr.Strip(line);
                if (text.Length > 0)
                    log($"  hook: {PyStr.Head(text, 200)}");
            }
            if (done.ExitCode != 0)
                log($"after-run hook exited {done.ExitCode}");
        }
        catch (Win32Exception error)
        {
            log($"after-run hook failed: {Shell.WinErrorText(error)}");
        }
        catch (ShellTimeoutException error)
        {
            log($"after-run hook failed: {error.PythonMessage}");
        }
        catch (Exception error)
        {
            log($"after-run hook failed: {error.Message}");
        }
    }
}

/// <summary>PeopleFirstClient's Api, as the planner reads it: each answer re-read as Python's json.loads would read it.
/// Post is PeopleFirstClient's PostAsync: never resent.</summary>
public sealed class PeopleFirstApi(PeopleFirstClient.Api api) : IPeopleFirstApi, IDisposable
{
    public string Me() => api.MeAsync().GetAwaiter().GetResult();

    public IReadOnlyList<JsonNode?> TeamMembers(string person) =>
        api.TeamMembersAsync(person).GetAwaiter().GetResult().Select(m => PyJson.Parse(m.GetRawText())).ToList();

    public JsonNode? Events(string person, DateOnly start, DateOnly end) =>
        PyJson.Parse(api.EventsAsync(person, start, end).GetAwaiter().GetResult().GetRawText());

    public JsonNode? Get(string path) => PyJson.Parse(api.GetAsync(path).GetAwaiter().GetResult().GetRawText());

    /// <summary>The body re-read as plain JSON nodes first (PyJson's numbers included), so it goes out as it reads.</summary>
    public JsonNode? Post(string path, JsonObject body) =>
        PyJson.Parse(api.PostAsync(path, JsonNode.Parse(PyJson.Dump(body))!).GetAwaiter().GetResult().GetRawText());

    public void Dispose() => api.Dispose();
}
