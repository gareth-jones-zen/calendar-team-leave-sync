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

    /// <summary>client(config, interactive): the unfenced Graph for these settings. Reads go through a read-only fence
    /// over it; only a Live write door writes through it.</summary>
    public Func<Config, bool?, IGraph> Graph { get; init; } = (config, interactive) => M365GraphClient.For(config, interactive);

    public LeaveReader ReadPeopleFirst { get; init; } = Runner.ReadPeopleFirst;
    public Action<Config>? PeopleFirstSignIn { get; init; }
    public Action<string, IEnumerable<string>> Notify { get; init; } = Toast.Notify;
    public HookRunner? HookRunner { get; init; }
    public Func<string, ITask> Tasks { get; init; } = Autostart.Create;
    public string ProgramDir { get; init; } = AppContext.BaseDirectory;
    public Func<DateTime>? Now { get; init; }
}

/// <summary>team-leave: your reports' approved leave, copied from People First into your Outlook calendar. The same
/// commands, options and exit codes as the Python version: 0; 1 for a scheduled sync that failed (and anything
/// unexpected); 2 for a failed sync by hand, a sign-in needed, no settings file, settings that can't be used, a lock
/// timeout, or a usage error.
/// <para>Two options the Python hasn't, for checking the port: <c>--writes live|journal|refuse</c> (default live) and
/// <c>--journal FILE</c> (where journal mode records what it would have written; default the log). A dry run always
/// refuses writes.</para></summary>
public static class Cli
{
    public const string Prog = "team-leave";

    private const string Usage = "usage: team-leave [-h] [--version] [--config CONFIG]\n"
        + "                  {config,sign-in,sync,status,autostart} ...";

    private const string Help = """
        team-leave: your reports' approved leave, copied from People First into your Outlook calendar.

        team-leave config                  create the settings file (if missing) and say where it is
        team-leave sign-in                 sign in to Microsoft, then to People First in a window
        team-leave sync --dry-run          what it would add, change, adopt and remove (writes nothing)
        team-leave sync                    bring your calendar into step with People First
        team-leave sync --adopt            ...taking over hand-made entries that match exactly
        team-leave sync --hook             ...then run the settings' after_run command, if anything changed
        team-leave sync --max-deletes N    allow more removals than max_deletes, this once
        team-leave status                  the last run, and the scheduled task
        team-leave autostart on            sync every every_minutes and at logon, and now (off, status)

        positional arguments:
          {config,sign-in,sync,status,autostart}
            config              create the settings file if missing, and say where it
                                is
            sign-in             sign in to Microsoft and People First (opens windows)
            sync                bring the calendar into step with People First
            status              the last run, and the scheduled task
            autostart           sync every every_minutes and at logon (Task Scheduler)

        options:
          -h, --help            show this help message and exit
          --version             show program's version number and exit
          --config CONFIG       settings file (default %APPDATA%\team-
                                leave\config.toml)
        """;

    private const string SyncUsage = "usage: team-leave sync [-h] [--dry-run] [--adopt] [--hook]\n"
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

    private static readonly string[] Commands = ["config", "sign-in", "sync", "status", "autostart"];

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private sealed class UsageError(string message, string usage = Usage, string prog = Prog) : Exception(message)
    {
        public string UsageText { get; } = usage;
        public string ProgText { get; } = prog;
    }

    internal sealed record Args(string? Config, string Command, bool DryRun, bool Adopt, bool Hook, bool Json, bool Scheduled,
        long? MaxDeletes, string? Action, WriteMode Writes, string? Journal);

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
                    args.MaxDeletes, host.Now, alert: host.Notify, runner: host.HookRunner);
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
    }

    private static int Status(Config config, CliHost host)
    {
        var state = Runner.LoadState(config);
        Print(host.Out, $"Last run:    {(state.TryGetPropertyValue("last_run", out var last) ? Py.Str(last) : "never")}");
        if (Py.Truthy(state["last_result"]))
            Print(host.Out, $"Result:      {Py.Str(state["last_result"])}");
        if (Py.Truthy(state["failures"]))
            Print(host.Out, $"Failing:     {Py.Str(state["failures"])} run(s) in a row; last: {(state.TryGetPropertyValue("last_error", out var e) ? Py.Str(e) : "")}");
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
                var command = task.Enable(exe, taskArgs, Path.GetDirectoryName(config.Path), Autostart.Schedule(config.EveryMinutes));
                Print(host.Out, $"Autostart is on: '{Autostart.TaskName}' runs every {config.EveryMinutes} minutes and at logon "
                                + $"(syncing only {config.Hours.From}-{config.Hours.To}{(config.Weekdays ? ", Mon-Fri" : "")}), and has started "
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
        "autostart" => "usage: team-leave autostart [-h] {on,off,status}",
        _ => $"usage: team-leave {command} [-h]",
    };

    private static string SubHelp(string command) => command switch
    {
        "sync" => SyncUsage + "\n" + SyncHelp.TrimEnd('\n', '\r'),
        "autostart" => "usage: team-leave autostart [-h] {on,off,status}\n\npositional arguments:\n  {on,off,status}\n\noptions:\n"
                       + "  -h, --help       show this help message and exit",
        _ => $"usage: team-leave {command} [-h]\n\noptions:\n  -h, --help  show this help message and exit",
    };

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

        string[] options = command == "sync" ? ["--help", "--dry-run", "--adopt", "--hook", "--max-deletes", "--json", "--scheduled"] : ["--help"];
        bool dryRun = false, adopt = false, hook = false, json = false, scheduled = false;
        long? maxDeletes = null;
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
            if (command == "autostart" && action is null)
                action = arg;
            else
                unrecognised.Add(arg);
        }
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
        return new Args(config, command, dryRun, adopt, hook, json, scheduled, maxDeletes, action, writes, journal);
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
