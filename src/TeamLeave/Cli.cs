using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using PyCompat;
using WinBackground;

namespace TeamLeave;

/// <summary>Everything the command line touches outside itself, so tests can give it fakes.</summary>
public sealed class CliHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;

    /// <summary>Where a sync's log lines are echoed (the console exe: stdout; the background exe: nowhere).</summary>
    public TextWriter? LogEcho { get; init; }

    public Func<string, string?> Env { get; init; } = Config.Environment;

    /// <summary>Standard input: digest's email (as UTF-8 JSON).</summary>
    public TextReader In { get; init; } = Console.In;

    /// <summary>client(config, interactive): the unfenced Graph for these settings. Reads go through a read-only fence
    /// over it; only a Live write door writes through it.</summary>
    public Func<Config, bool?, IGraph> Graph { get; init; } = (config, interactive) => M365GraphClient.For(config, interactive);

    public LeaveReader ReadPeopleFirst { get; init; } = Runner.ReadPeopleFirst;

    /// <summary>People First signed in for a run: with [approvals] (sync, approvals) and for approve.</summary>
    public PeopleFirstOpener OpenPeopleFirst { get; init; } = Runner.OpenPeopleFirst;

    public Action<Config>? PeopleFirstSignIn { get; init; }
    public Action<string, IEnumerable<string>> Notify { get; init; } = Toast.Notify;
    public HookRunner? HookRunner { get; init; }
    public Func<string, ITask> Tasks { get; init; } = Autostart.Create;
    public string ProgramDir { get; init; } = AppContext.BaseDirectory;
    public Func<DateTime>? Now { get; init; }

    /// <summary>GOV.UK's bank holiday list as text, for leave-left (a failure throws; the rules stand in).</summary>
    public Func<string> BankHolidayFeed { get; init; } = BankHolidays.Fetch;
}

/// <summary>calendar-team-leave-sync: your reports' approved leave, copied from People First into your Outlook calendar. The same
/// commands, options and exit codes as the Python version: 0; 1 for a scheduled sync that failed (and anything
/// unexpected); 2 for a failed sync by hand, a sign-in needed, no settings file, settings that can't be used, a lock
/// timeout, or a usage error.
/// <para>Two options the Python hasn't, for checking the port: <c>--writes live|journal|refuse</c> (default live) and
/// <c>--journal FILE</c> (where journal mode records what it would have written; default the log). A dry run always
/// refuses writes.</para>
/// <para>Two commands the Python hasn't, for [approvals]: <c>approvals</c> (what the rules make of each waiting request;
/// writes nothing) and <c>approve ID</c> (a preview that writes nothing; with <c>--yes</c>, approve one by hand, through
/// the write door).</para></summary>
public static class Cli
{
    public const string Prog = "calendar-team-leave-sync";

    private const string Usage = "usage: calendar-team-leave-sync [-h] [--version] [--config CONFIG]\n"
        + "                  {config,sign-in,sync,approvals,approve,leave-left,digest,status,autostart} ...";

    private const string Help = """
        calendar-team-leave-sync: your reports' approved leave, copied from People First into your Outlook calendar.

        calendar-team-leave-sync config                  create the settings file (if missing) and say where it is
        calendar-team-leave-sync sign-in                 sign in to Microsoft, then to People First in a window
        calendar-team-leave-sync sync --dry-run          what it would add, change, adopt and remove (writes nothing)
        calendar-team-leave-sync sync                    bring your calendar into step with People First
        calendar-team-leave-sync sync --adopt            ...taking over hand-made entries that match exactly
        calendar-team-leave-sync sync --hook             ...then run the settings' after_run command, if anything changed
        calendar-team-leave-sync sync --max-deletes N    allow more removals than max_deletes, this once
        calendar-team-leave-sync approvals               leave requests waiting for you: what the [approvals] rules
                                           say about each, and who's in (writes nothing)
        calendar-team-leave-sync approve ID              what approving one request yourself would do, and what the
                                           rules would say (writes nothing)
        calendar-team-leave-sync approve ID --yes        approve it, whatever the rules say
        calendar-team-leave-sync leave-left              each report's annual leave this year: allowance, taken,
                                           booked, waiting and left to book (reads only)
        calendar-team-leave-sync digest                  People First's daily notifications email (as JSON on stdin): exit 0
                                           if it's only leave requests calendar-team-leave-sync has seen, 75 if one isn't
                                           seen yet, 1 for anything else (a mail-triage handler)
        calendar-team-leave-sync status                  the last run, and the scheduled task
        calendar-team-leave-sync autostart on            sync every every_minutes and at logon, and now (off, status)

        positional arguments:
          {config,sign-in,sync,approvals,approve,leave-left,digest,status,autostart}
            config              create the settings file if missing, and say where it
                                is
            sign-in             sign in to Microsoft and People First (opens windows)
            sync                bring the calendar into step with People First
            approvals           the [approvals] rules on each waiting request (read
                                only)
            approve             approve one leave request by its People First id
                                (--yes; without it, a preview)
            leave-left          each report's annual leave left to book (read only)
            digest              check a People First digest email against what
                                calendar-team-leave-sync has seen (stdin: the email as JSON)
            status              the last run, and the scheduled task
            autostart           sync every every_minutes and at logon (Task Scheduler)

        options:
          -h, --help            show this help message and exit
          --version             show program's version number and exit
          --config CONFIG       settings file (default %APPDATA%\team-
                                leave\config.toml)
        """;

    private const string SyncUsage = "usage: calendar-team-leave-sync sync [-h] [--dry-run] [--adopt] [--hook]\n"
        + "                       [--max-deletes MAX_DELETES] [--json] [--scheduled]";

    private const string SyncHelp = """

        options:
          -h, --help            show this help message and exit
          --dry-run             say what it would do; write nothing
          --adopt               take over hand-made entries that match exactly
          --hook                run the after_run command if anything changed
          --max-deletes MAX_DELETES
                                allow this many removals, this once
          --json                print the report as JSON
          --scheduled           as the task runs it: only within hours, no browser,
                                and notify problems
        """;

    private static readonly string[] Commands = ["config", "sign-in", "sync", "approvals", "approve", "leave-left", "digest", "status", "autostart"];

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private sealed class UsageError(string message, string usage = Usage, string prog = Prog) : Exception(message)
    {
        public string UsageText { get; } = usage;
        public string ProgText { get; } = prog;
    }

    internal sealed record Args(string? Config, string Command, bool DryRun, bool Adopt, bool Hook, bool Json, bool Scheduled,
        long? MaxDeletes, string? Action, WriteMode Writes, string? Journal, bool Yes = false, int? Year = null);

    public static int Run(string[] argv, CliHost host)
    {
        Args args;
        try
        {
            var parsed = Parse(argv, host);
            if (parsed is null)
                return 0;   // --help or --version
            args = parsed;
        }
        catch (UsageError error)
        {
            Print(host.Err, $"{error.UsageText}\n{error.ProgText}: error: {error.Message}");
            return 2;
        }

        if (args.Command == "config")
        {
            Print(host.Out, !string.IsNullOrEmpty(args.Config) ? args.Config : TeamLeave.Config.CreateDefault(host.Env));
            return 0;
        }
        try
        {
            var config = TeamLeave.Config.Load(args.Config, host.Env);
            switch (args.Command)
            {
                case "autostart":
                    return AutostartCommand(config, args.Action!, host);
                case "status":
                    return Status(config, host);
                case "approvals":
                    return ApprovalsCommand(config, args, host);
                case "approve":
                    return ApproveCommand(config, args, host);
                case "leave-left":
                    return LeaveLeftCommand(config, args, host);
                case "digest":
                    var (code, note) = Digest.Check(config, Runner.LoadState(config), host.In.ReadToEnd());
                    Print(host.Out, note);
                    return code;
                case "sign-in":
                    host.Graph(config, true).Token();
                    Print(host.Out, "Signed in to Microsoft. A window opens for People First: sign in there; it closes by itself.");
                    (host.PeopleFirstSignIn ?? (c => PeopleFirstClient.SignInWindow.RunAsync(c.PeopleFirst).GetAwaiter().GetResult()))(config);
                    Print(host.Out, "Signed in to People First.");
                    return 0;
            }
            var log = Logger.Create(config.LogFile, !args.Json && !args.Scheduled ? host.LogEcho ?? host.Out : null, stampEcho: false, clock: host.Now);
            JsonObject? report;
            try
            {
                var graph = host.Graph(config, args.Scheduled ? false : null);
                var door = Door(args, graph, config, log);
                report = Runner.Run(config, graph, door, host.ReadPeopleFirst, log, args.DryRun, args.Adopt, args.Hook, args.Scheduled,
                    args.MaxDeletes, host.Now, alert: host.Notify, runner: host.HookRunner, open: host.OpenPeopleFirst);
            }
            catch (Exception error)
            {
                if (args.Scheduled)
                    return 1;
                Print(host.Err, PyBits.Text(error));
                return 2;
            }
            if (report is null)
                return 0;
            if (args.Json)
                Print(host.Out, PyJson.Dump(report, indent: 1));
            else if (!args.Scheduled)
                PrintReport(host.Out, report);
            return 0;
        }
        catch (Exception error) when (error is M365Graph.SignInNeededException or PeopleFirstClient.SignInNeededException
                                          or FileNotFoundException or PyValueError or TimeoutException)
        {
            Print(host.Err, error.Message);
            return 2;
        }
        catch (Exception error)
        {
            Print(host.Err, $"{error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    /// <summary>The write door for this command line: a dry run refuses every write; journal mode records each intended
    /// write in --journal (or the log) and sends nothing.</summary>
    private static WriteDoor Door(Args args, IGraph graph, Config config, Action<string> log)
    {
        var mode = args.DryRun ? WriteMode.Refuse : args.Writes;
        if (mode != WriteMode.Journal)
            return WriteDoor.For(mode, graph, config.Event.TimeZone);
        var journal = args.Journal is null ? log : Logger.Create(args.Journal);
        return WriteDoor.Journaling(config.Event.TimeZone, call => journal($"journal (not sent): {call.Text}"));
    }

    /// <summary>_print(): what it did, or would do, a line each.</summary>
    private static void PrintReport(TextWriter output, JsonObject report)
    {
        var verb = report["dry_run"]!.GetValue<bool>() ? "Would" : "Did";
        foreach (var (word, key, was) in new[] { ("add", "added", (string?)null), ("change", "changed", "was"), ("adopt", "adopted", "was"), ("remove", "removed", null) })
            foreach (var item in report[key]!.AsArray())
                Print(output, $"  {verb} {word}: {Py.Str(item!["text"])}" + (was is not null ? $"  (was: {Py.Str(item[was])})" : ""));
        foreach (var item in report["by_hand"]!.AsArray())
        {
            var hint = Py.Int(item!["pieces"]) > 1 ? "  (in pieces: never adopted; delete them for one entry)" : "  (sync --adopt takes it over)";
            Print(output, $"  Already in by hand, left alone: {Py.Str(item["entry"])}{hint}");
        }
        foreach (var item in report["unmatched_hand"]!.AsArray())
            Print(output, $"  Hand-made, matches no approved leave in People First: {Py.Str(item!["text"])}");
        if (report["approvals"] is JsonObject approvals)
            PrintApprovals(output, approvals, detail: false);
    }

    /// <summary>The approvals part of a report: a line per request, with the reasons for anything that needs you, and
    /// (in detail) its id and who's in each working day.</summary>
    private static void PrintApprovals(TextWriter output, JsonObject approvals, bool detail)
    {
        var dry = !approvals["act"]!.GetValue<bool>();
        foreach (var (label, key) in new[]
                 {
                     ("Approved", "approved"), (dry ? "Would approve (act = false)" : "Would approve", "would_approve"),
                     ("Over max_per_run, for the next run", "deferred"), ("Needs you", "asked"), ("Sent, not yet confirmed", "unknown"),
                 })
        {
            foreach (var item in approvals[key]!.AsArray())
            {
                var mark = key == "asked" && !item!["new"]!.GetValue<bool>() ? " (raised before)" : "";
                Print(output, $"  {label}: {Py.Str(item!["text"])}{mark}" + (detail ? $"  [{Py.Str(item["id"])}]" : ""));
                if (key is "asked" or "unknown" or "deferred" || detail)
                    foreach (var reason in item["reasons"]!.AsArray())
                        Print(output, $"      - {Py.Str(reason)}");
                if (detail || key == "asked")
                    foreach (var day in item["cover"]!.AsArray())
                    {
                        var names = day!["in"]!.AsArray().Select(n => Py.Str(n)).ToList();
                        var date = PyBits.DateFromIso(Py.Str(day["date"]));
                        Print(output, $"      {date.ToString("ddd dd/MM", CultureInfo.InvariantCulture)} in: {(names.Count == 0 ? "nobody" : string.Join(", ", names))}");
                    }
            }
        }
        Print(output, $"  Approvals: {Py.Str(approvals["summary"])}");
    }

    /// <summary>approvals: read People First, and say what the rules make of each waiting request. Never writes: no
    /// People First write, no state, no log, no notification.</summary>
    private static int ApprovalsCommand(Config config, Args args, CliHost host)
    {
        if (config.Approvals is null)
        {
            Print(host.Err, $"There's no [approvals] section in {config.Path}: add one (see the README) to see what its rules would do.");
            return 2;
        }
        var now = (host.Now ?? (() => DateTime.Now))();
        var today = DateOnly.FromDateTime(now);
        JsonObject report;
        var api = host.OpenPeopleFirst(config, _ => { });
        try
        {
            var fenced = new ReadOnlyPeopleFirst(api);
            var read = Leave.ReadAll(config, fenced, today, _ => { });
            var door = WriteDoor.Refusing(config.Event.TimeZone);
            report = Approvals.Process(config, read, fenced, door.PeopleFirst(api), door.Mode, dryRun: true, Runner.LoadState(config), today, now,
                _ => { }).Report;
        }
        catch (Exception error) when (error is PeopleFirstClient.PeopleFirstException or LeaveError)
        {
            Print(host.Err, error.Message);
            return 2;
        }
        finally
        {
            (api as IDisposable)?.Dispose();
        }
        if (args.Json)
            Print(host.Out, PyJson.Dump(report, indent: 1));
        else
            PrintApprovals(host.Out, report, detail: true);
        return 0;
    }

    /// <summary>leave-left: each report's allowance, taken, booked, waiting and left to book this leave year. Reads only,
    /// every call through the read-only fence; the figures go to the console and nowhere else (the log gets counts).
    /// Exit 2 if anyone couldn't be read (the rest are still shown).</summary>
    private static int LeaveLeftCommand(Config config, Args args, CliHost host)
    {
        var now = (host.Now ?? (() => DateTime.Now))();
        var today = DateOnly.FromDateTime(now);
        LeaveLeftReport report;
        var api = host.OpenPeopleFirst(config, _ => { });
        try
        {
            report = LeaveLeft.Read(config, new ReadOnlyPeopleFirst(api), today, args.Year,
                years => BankHolidays.For(years, config.LeaveLeft.Region, host.BankHolidayFeed));
        }
        catch (Exception error) when (error is LeaveError or PeopleFirstClient.PeopleFirstException
                                          and not PeopleFirstClient.SignInNeededException)
        {
            Print(host.Err, error.Message);
            return 2;
        }
        finally
        {
            (api as IDisposable)?.Dispose();
        }
        var failed = report.Rows.Count(r => r.Error is not null);
        Logger.Create(config.LogFile, clock: host.Now)(
            $"leave-left: {report.Rows.Count} people, {report.Rows.Count(r => r.Error is null && r.Reconciles)} reconciled, "
            + $"{report.Rows.Count(r => r.Flagged)} flagged, {failed} couldn't be read");
        Print(host.Out, args.Json ? PyJson.Dump(LeaveLeft.Json(report), indent: 1) : LeaveLeft.Text(report));
        return failed > 0 ? 2 : 0;
    }

    /// <summary>approve ID: approve one waiting request yourself (your decision, whatever the rules say). Without --yes,
    /// a preview: what would be approved and what the [approvals] rules make of it, and nothing written, whatever
    /// --writes says. With --yes, through the write door, then read back. Either way it refuses an id that isn't waiting
    /// for approval in your team.</summary>
    private static int ApproveCommand(Config config, Args args, CliHost host)
    {
        var id = args.Action!;
        if (!Approvals.SafeId().IsMatch(id))
        {
            Print(host.Err, $"{Py.ReprString(id)} isn't a People First absence id.");
            return 2;
        }
        if (!args.Yes)
            return ApprovePreview(config, id, host);
        var clock = host.Now ?? (() => DateTime.Now);
        var log = Logger.Create(config.LogFile, host.LogEcho ?? host.Out, stampEcho: false, clock: host.Now);
        using var held = new NamedLock(Runner.RunLockName(config));
        if (!held.Acquire(Runner.LockWaitSeconds))
            throw new TimeoutException($"Another calendar-team-leave-sync run for {config.Path} didn't finish within {Runner.LockWaitSeconds / 60:0} minutes.");
        var mode = args.Writes;
        var journal = mode != WriteMode.Journal ? null : args.Journal is null ? log : Logger.Create(args.Journal);
        var door = WriteDoor.PeopleFirstOnly(mode, config.Event.TimeZone, call => journal?.Invoke($"journal (not sent): {call.Text}"));
        var today = DateOnly.FromDateTime(clock());
        ApprovalResult result;
        LeaveRequest request;
        var api = host.OpenPeopleFirst(config, log);
        try
        {
            var fenced = new ReadOnlyPeopleFirst(api);
            var found = Leave.Find(config, fenced, today, id);
            if (found is null)
            {
                Print(host.Err, $"No absence {id} in your team's leave from today on.");
                return 2;
            }
            request = found.Value.Request;
            if (found.Value.State != Approvals.PendingState)
            {
                Print(host.Err, $"{Text(config, request)} isn't waiting for approval (People First says {Py.ReprString(found.Value.State)}).");
                return 2;
            }
            result = Approvals.Approve(config, request, fenced, door.PeopleFirst(api), door.Mode, today, log);
        }
        catch (Exception error) when (error is PeopleFirstClient.PeopleFirstException or LeaveError or WriteRefusedException)
        {
            Print(host.Err, error.Message);
            return 2;
        }
        finally
        {
            (api as IDisposable)?.Dispose();
        }
        if (door.Mode == WriteMode.Live && result.Outcome != ApprovalOutcome.Journaled)
        {
            var state = Runner.LoadState(config);
            var records = Approvals.Records(state);
            Approvals.Record(records, request, result.Outcome switch
            {
                ApprovalOutcome.Approved => Approvals.Approved,
                ApprovalOutcome.Unknown => Approvals.Unknown,
                _ => Approvals.Failed,
            }, clock(), byHand: true);
            state["approvals"] = records;
            Runner.SaveState(config, state);
        }
        var line = result.Outcome switch
        {
            ApprovalOutcome.Approved => $"Approved: {Text(config, request)}. The next sync puts it in your calendar.",
            ApprovalOutcome.Journaled => $"Not sent (journal mode): {Text(config, request)}.",
            ApprovalOutcome.Unknown => $"Not confirmed: {Text(config, request)}. {Capital(result.Message)}. Check People First before trying again.",
            _ => $"Not approved: {Text(config, request)}. {Capital(result.Message)}.",
        };
        Print(result.Outcome is ApprovalOutcome.Approved or ApprovalOutcome.Journaled ? host.Out : host.Err, line);
        return result.Outcome is ApprovalOutcome.Approved or ApprovalOutcome.Journaled ? 0 : 2;
    }

    /// <summary>approve ID without --yes: the same checks as approving (in your team, waiting for approval), then who,
    /// when, the type, and what the [approvals] rules would say and why, so it's plain when approving overrides them.
    /// Reads only, every call through the read-only fence, and never Approvals.Approve: no People First write, no
    /// state, no log, no notification, whatever --writes says.</summary>
    private static int ApprovePreview(Config config, string id, CliHost host)
    {
        var now = (host.Now ?? (() => DateTime.Now))();
        var today = DateOnly.FromDateTime(now);
        LeaveRequest request;
        Decision? decision = null;
        var api = host.OpenPeopleFirst(config, _ => { });
        try
        {
            var fenced = new ReadOnlyPeopleFirst(api);
            var found = Leave.Find(config, fenced, today, id);
            if (found is null)
            {
                Print(host.Err, $"No absence {id} in your team's leave from today on.");
                return 2;
            }
            request = found.Value.Request;
            if (found.Value.State != Approvals.PendingState)
            {
                Print(host.Err, $"{Text(config, request)} isn't waiting for approval (People First says {Py.ReprString(found.Value.State)}).");
                return 2;
            }
            if (config.Approvals is { } settings)
            {
                var read = Leave.ReadAll(config, fenced, today, _ => { });
                decision = Approvals.Decide(request, read.People, read.Away, Approvals.History(fenced, id, _ => { }), today,
                    config.WindowEnd(today), settings);
            }
        }
        catch (Exception error) when (error is PeopleFirstClient.PeopleFirstException or LeaveError or WriteRefusedException)
        {
            Print(host.Err, error.Message);
            return 2;
        }
        finally
        {
            (api as IDisposable)?.Dispose();
        }

        string Day(DateOnly day) => day.ToString("ddd dd/MM", CultureInfo.InvariantCulture);
        var workingDays = Approvals.WorkingDays(request.Start, request.End);
        var lines = new List<string>
        {
            $"Would approve: {Text(config, request)}  [{id}]",
            $"  Person:   {request.Person.Name}",
            $"  Dates:    {(request.Start == request.End ? Day(request.Start) : $"{Day(request.Start)} to {Day(request.End)}")}, "
            + $"{workingDays} working day{(workingDays == 1 ? "" : "s")}"
            + HalfDays(request.AsAbsence(config).Halves),
            $"  Type:     {(request.Type.Length > 0 ? request.Type : "(People First gives none)")}",
        };
        if (decision is null)
            lines.Add("  Rules:    none to compare with (no [approvals] section)");
        else
        {
            lines.Add(decision.Approve
                ? "  Rules:    they'd approve it too"
                : "  Rules:    they wouldn't approve it, so approving it overrides them:");
            lines.AddRange(decision.Reasons.Select(reason => $"      - {reason}"));
            lines.AddRange(decision.Cover.Select(day =>
                $"      {Day(day.Day)} in: {(day.In.Count == 0 ? "nobody" : string.Join(", ", day.In))}"));
        }
        if (Approvals.Records(Runner.LoadState(config))[id] is JsonObject record && Approvals.Outcome(record) == Approvals.Unknown
            && Approvals.SameDates(record, request))
            lines.Add($"  Earlier:  an approval was sent {Approvals.When(record)} and never confirmed, and People First still shows it "
                      + "waiting: check it there before approving it again");
        lines.Add($"  People First records the approval as yours, and {request.Person.Name} sees it.");
        lines.Add("Nothing approved. Run again with --yes to approve it.");
        Print(host.Out, string.Join("\n", lines));
        return 0;
    }

    /// <summary>The half days, in words, as the title marks them ("" for whole days).</summary>
    private static string HalfDays((string? Single, string? First, string? Last) halves)
    {
        var (single, first, last) = halves;
        var words = new List<string>();
        if (single is not null)
            words.Add(single == CalendarDays.ReportLeaveTitles.Am ? "the morning only" : "the afternoon only");
        if (first is not null)
            words.Add("from the afternoon of the first day");
        if (last is not null)
            words.Add("to midday on the last day");
        return words.Count == 0 ? "" : $" ({string.Join(", ", words)})";
    }

    private static string Text(Config config, LeaveRequest request) =>
        $"{request.AsAbsence(config).Title(config)} {Sync.Fmt(request.Start, request.End)}";

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static int Status(Config config, CliHost host)
    {
        var state = Runner.LoadState(config);
        Print(host.Out, $"Last run:    {(state.TryGetPropertyValue("last_run", out var last) ? Py.Str(last) : "never")}");
        if (Py.Truthy(state["last_result"]))
            Print(host.Out, $"Result:      {Py.Str(state["last_result"])}");
        if (Py.Truthy(state["failures"]))
            Print(host.Out, $"Failing:     {Py.Str(state["failures"])} run(s) in a row; last: {(state.TryGetPropertyValue("last_error", out var e) ? Py.Str(e) : "")}");
        if (config.Approvals is not null)
        {
            var outcomes = Approvals.Records(state).Select(r => r.Value?["outcome"] is JsonValue v && v.TryGetValue<string>(out var o) ? o : "").ToList();
            int Count(params string[] kinds) => outcomes.Count(kinds.Contains);
            Print(host.Out, $"Approvals:   {(config.Approvals.Act ? "on" : "preview (act = false)")}; {Count(Approvals.Asked, Approvals.Failed)} with you, "
                            + $"{Count(Approvals.Unknown)} sent but not confirmed, {Count(Approvals.WouldApprove)} the rules would approve");
        }
        return AutostartCommand(config, "status", host);
    }

    private static int AutostartCommand(Config config, string action, CliHost host)
    {
        var task = host.Tasks(Autostart.TaskName);
        try
        {
            if (action == "on")
            {
                var (exe, taskArgs) = Autostart.Command(host.ProgramDir, config.Path);
                var command = task.Enable(exe, taskArgs, Path.GetDirectoryName(config.Path), Autostart.Schedule(config));
                Print(host.Out, $"Autostart is on: '{Autostart.TaskName}' {Autostart.Describe(config)}, and has started "
                                + $"now.\n  {command}\n  log: {config.LogFile}");
            }
            else if (action == "off")
                Print(host.Out, task.Disable() ? "Autostart is off." : "Autostart was already off.");
            else
            {
                var info = task.Status();
                if (info is null)
                    Print(host.Out, "Autostart is off.");
                else
                {
                    Print(host.Out, $"Autostart is on ('{Autostart.TaskName}').");
                    foreach (var key in new[] { "state", "last_run", "last_result", "next_run", "command" })
                        Print(host.Out, $"  {key.Replace('_', ' '),-12} {info.GetValueOrDefault(key, "")}");
                    if (ScheduleDrift.Advice(ScheduleDrift.Check(info, config.Schedule.Fingerprint(TaskKind.Job)), "calendar-team-leave-sync") is { } advice)
                        Print(host.Out, $"  {"schedule",-12} {advice}");
                }
            }
            return 0;
        }
        catch (Exception error) when (error is InvalidOperationException or PyValueError or Win32Exception or ArgumentException)
        {
            Print(host.Err, error.Message);
            return 2;
        }
    }

    /// <summary>print(): a line, with "\n" written as "\r\n" as Python's stdout does on Windows.</summary>
    private static void Print(TextWriter writer, string text)
    {
        try
        {
            writer.Write(PyStr.WindowsNewlines(text.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n"));
            writer.Flush();
        }
        catch (IOException)
        {
            // a closed console mustn't stop the tool
        }
    }

    // ---- argparse, as far as these commands use it ------------------------------------------------------------

    private static string SubUsage(string command) => command switch
    {
        "sync" => SyncUsage,
        "autostart" => "usage: calendar-team-leave-sync autostart [-h] {on,off,status}",
        "approvals" => "usage: calendar-team-leave-sync approvals [-h] [--json]",
        "approve" => ApproveUsage,
        "leave-left" => LeaveLeftUsage,
        _ => $"usage: calendar-team-leave-sync {command} [-h]",
    };

    private static string SubHelp(string command) => command switch
    {
        "sync" => SyncUsage + "\n" + SyncHelp.TrimEnd('\n', '\r'),
        "autostart" => "usage: calendar-team-leave-sync autostart [-h] {on,off,status}\n\npositional arguments:\n  {on,off,status}\n\noptions:\n"
                       + "  -h, --help       show this help message and exit",
        "approvals" => "usage: calendar-team-leave-sync approvals [-h] [--json]\n\noptions:\n  -h, --help  show this help message and exit\n"
                       + "  --json      print the report as JSON",
        "approve" => ApproveUsage + "\n\n" + """
            Without --yes, shows what would be approved (who, the dates and working days, the type) and what the
            [approvals] rules would say and why, and approves nothing, whatever --writes says. With --yes, approves it in
            People First (through --writes) and reads it back.

            positional arguments:
              absence     the People First absence id (from approvals)

            options:
              -h, --help  show this help message and exit
              --yes       approve it; without this, a preview that writes nothing
            """.TrimEnd('\n', '\r'),
        "leave-left" => LeaveLeftUsage + "\n\n" + """
            Each report's annual leave: the allowance, days taken to today and booked after it (bank holidays count as
            used), days waiting for approval (not in the balance), and People First's own figure left to book. Reads
            People First only; the figures are printed and never logged or stored.

            options:
              -h, --help   show this help message and exit
              --year YEAR  the leave year People First has on 1 January of YEAR (default: today's)
              --json       print it as JSON
            """.TrimEnd('\n', '\r'),
        _ => $"usage: calendar-team-leave-sync {command} [-h]\n\noptions:\n  -h, --help  show this help message and exit",
    };

    private const string LeaveLeftUsage = "usage: calendar-team-leave-sync leave-left [-h] [--year YEAR] [--json]";

    private const string ApproveUsage = "usage: calendar-team-leave-sync approve [-h] [--yes] absence";

    private static string Quoted(IEnumerable<string> choices) => string.Join(", ", choices.Select(c => $"'{c}'"));

    internal static Args? Parse(string[] argv, CliHost host)
    {
        string? config = null;
        string? journal = null;
        var writes = WriteMode.Live;
        var i = 0;
        string[] mainOptions = ["--help", "--version", "--config", "--writes", "--journal"];
        while (i < argv.Length && IsOption(argv[i]))
        {
            var (name, value) = Option(argv[i], mainOptions);
            string Value() => value ?? (i + 1 < argv.Length && !IsOption(argv[i + 1]) ? argv[++i] : throw new UsageError($"argument {name}: expected one argument"));
            switch (name)
            {
                case "--help":
                    Print(host.Out, $"{Usage}\n\n{Help.TrimEnd('\n', '\r')}");
                    return null;
                case "--version":
                    Print(host.Out, Version);
                    return null;
                case "--config":
                    config = Value();
                    break;
                case "--journal":
                    journal = Value();
                    break;
                case "--writes":
                    var mode = Value();
                    writes = mode switch
                    {
                        "live" => WriteMode.Live,
                        "journal" => WriteMode.Journal,
                        "refuse" => WriteMode.Refuse,
                        _ => throw new UsageError($"argument --writes: invalid choice: {Py.ReprString(mode)} (choose from 'live', 'journal', 'refuse')"),
                    };
                    break;
            }
            i++;
        }
        if (i >= argv.Length)
            throw new UsageError("the following arguments are required: command");
        var command = argv[i++];
        if (!Commands.Contains(command))
            throw new UsageError($"argument command: invalid choice: '{command}' (choose from {Quoted(Commands)})");

        string[] options = command switch
        {
            "sync" => ["--help", "--dry-run", "--adopt", "--hook", "--max-deletes", "--json", "--scheduled"],
            "approvals" => ["--help", "--json"],
            "approve" => ["--help", "--yes"],
            "leave-left" => ["--help", "--json", "--year"],
            _ => ["--help"],
        };
        bool dryRun = false, adopt = false, hook = false, json = false, scheduled = false, yes = false;
        long? maxDeletes = null;
        int? year = null;
        string? action = null;
        var unrecognised = new List<string>();
        var onlyPositional = false;
        var sub = $"{Prog} {command}";
        for (; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (!onlyPositional && arg == "--")
            {
                onlyPositional = true;
                continue;
            }
            if (!onlyPositional && IsOption(arg))
            {
                string name;
                string? value;
                try
                {
                    (name, value) = Option(arg, options);
                }
                catch (UsageError)
                {
                    unrecognised.Add(arg);
                    continue;
                }
                switch (name)
                {
                    case "--help":
                        Print(host.Out, SubHelp(command));
                        return null;
                    case "--dry-run": dryRun = Flag(value, name, command); break;
                    case "--adopt": adopt = Flag(value, name, command); break;
                    case "--hook": hook = Flag(value, name, command); break;
                    case "--json": json = Flag(value, name, command); break;
                    case "--scheduled": scheduled = Flag(value, name, command); break;
                    case "--yes": yes = Flag(value, name, command); break;
                    case "--year":
                        var given = value ?? (i + 1 < argv.Length && !IsOption(argv[i + 1]) ? argv[++i]
                            : throw new UsageError("argument --year: expected one argument", SubUsage(command), sub));
                        year = int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 2000 and <= 2100 ? y
                            : throw new UsageError($"argument --year: invalid year: {Py.ReprString(given)}", SubUsage(command), sub);
                        break;
                    case "--max-deletes":
                        var text = value ?? (i + 1 < argv.Length && !IsOption(argv[i + 1]) ? argv[++i]
                            : throw new UsageError("argument --max-deletes: expected one argument", SubUsage(command), sub));
                        try
                        {
                            maxDeletes = PyStr.Int(text);
                        }
                        catch (Exception error) when (error is FormatException or OverflowException)
                        {
                            throw new UsageError($"argument --max-deletes: invalid int value: {Py.ReprString(text)}", SubUsage(command), sub);
                        }
                        break;
                }
                continue;
            }
            if (command is "autostart" or "approve" && action is null)
                action = arg;
            else
                unrecognised.Add(arg);
        }
        if (command == "approve" && action is null)
            throw new UsageError("the following arguments are required: absence", SubUsage(command), sub);
        if (command == "autostart")
        {
            string[] choices = ["on", "off", "status"];
            if (action is null)
                throw new UsageError("the following arguments are required: action", SubUsage(command), sub);
            if (!choices.Contains(action))
                throw new UsageError($"argument action: invalid choice: '{action}' (choose from {Quoted(choices)})", SubUsage(command), sub);
        }
        if (unrecognised.Count > 0)
            throw new UsageError($"unrecognized arguments: {string.Join(' ', unrecognised)}");
        return new Args(config, command, dryRun, adopt, hook, json, scheduled, maxDeletes, action, writes, journal, yes, year);
    }

    // argparse treats "-5" as a value, not an option, when no option looks like a number
    private static bool IsOption(string arg) => arg.Length > 1 && arg[0] == '-' && !(char.IsAsciiDigit(arg[1]) || (arg[1] == '.' && arg.Length > 2 && char.IsAsciiDigit(arg[2])));

    private static bool Flag(string? value, string name, string command) =>
        value is null ? true : throw new UsageError($"argument {name}: ignored explicit argument '{value}'", SubUsage(command), $"{Prog} {command}");

    /// <summary>An option and its "=value", with argparse's unambiguous prefixes ("--dry" for "--dry-run").</summary>
    private static (string Name, string? Value) Option(string arg, string[] known)
    {
        if (arg == "-h")
            return ("--help", null);
        var eq = arg.IndexOf('=');
        var name = eq >= 0 ? arg[..eq] : arg;
        var value = eq >= 0 ? arg[(eq + 1)..] : null;
        if (known.Contains(name))
            return (name, value);
        if (name.StartsWith("--", StringComparison.Ordinal))
        {
            var matches = known.Where(k => k.StartsWith(name, StringComparison.Ordinal)).ToList();
            if (matches.Count == 1)
                return (matches[0], value);
            if (matches.Count > 1)
                throw new UsageError($"ambiguous option: {name} could match {string.Join(", ", matches)}");
        }
        throw new UsageError($"unrecognized arguments: {arg}");
    }
}
