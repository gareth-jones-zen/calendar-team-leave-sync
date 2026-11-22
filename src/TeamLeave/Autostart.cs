using PyCompat;
using WinBackground;

namespace TeamLeave;

/// <summary>A repeating sync through Windows Task Scheduler (WinBackground).
///
/// The task runs `sync --hook --scheduled` every every_minutes, and at logon, and catches up a run missed while the
/// laptop was asleep. A scheduled run outside `hours` (and at weekends, with `weekdays`) exits at once, writing nothing
/// and logging nothing. It runs as you, only while you're logged on, with no admin rights needed, and runs
/// calendar-team-leave-sync-background.exe directly, so there's no window. (Named "team-leave sync" until the rename to
/// calendar-team-leave-sync: an install moved from team-leave turns the old task off first.)</summary>
public static class Autostart
{
    public const string TaskName = "calendar-team-leave-sync sync";
    public const string ConsoleExe = "calendar-team-leave-sync.exe";
    public const string BackgroundExe = "calendar-team-leave-sync-background.exe";

    public static ITask Create(string name) =>
        new ScheduledTask(name, "Copies your reports' approved leave from People First into your calendar (calendar-team-leave-sync sync).");

    /// <summary>The program the task runs (the windowless one beside the running program, if it's there) and its
    /// arguments.</summary>
    public static (string Exe, List<string> Args) Command(string programDir, string configPath)
    {
        var background = Path.Combine(programDir, BackgroundExe);
        var exe = File.Exists(background) ? background : Path.Combine(programDir, ConsoleExe);
        return (exe, ["--config", PyPath.Resolve(configPath), "sync", "--hook", "--scheduled"]);
    }

    /// <summary>A job stopped after 20 minutes.</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(20);

    /// <summary>When it runs, from [schedule] (the library builds the triggers and records their fingerprint in the
    /// task, so `autostart status` can tell when the settings have changed since): by default at logon and every
    /// every_minutes, as a job stopped after 20 minutes, started now.</summary>
    public static TaskSchedule Schedule(Config config) => config.Schedule.ToTaskSchedule(TaskKind.Job, TimeLimit);

    /// <summary>When it runs: at logon and every every_minutes (the old form, kept for callers that pass a number).</summary>
    public static TaskSchedule Schedule(int everyMinutes) =>
        (Config.DefaultSchedule with { EveryMinutes = everyMinutes }).ToTaskSchedule(TaskKind.Job, TimeLimit);

    /// <summary>"runs every 15 minutes and at logon (syncing only 07:00-19:00, Mon-Fri)": the schedule in words.</summary>
    public static string Describe(Config config)
    {
        var s = config.Schedule;
        var when = new List<string>();
        if (s.EveryMinutes is > 0 and var m)
            when.Add($"every {m} minutes");
        else if (s.DailyAt is { } at)
            when.Add($"daily at {at}");
        if (s.AtLogon)
            when.Add("at logon");
        if (s.AfterUnlockMinutes > 0)
            when.Add($"{s.AfterUnlockMinutes} minute(s) after each unlock");
        var runs = when.Count switch
        {
            0 => "never",
            1 => when[0],
            _ => string.Join(", ", when.Take(when.Count - 1)) + " and " + when[^1],
        };
        var gate = new List<string> { $"{config.Hours.From}-{config.Hours.To}" };
        if (config.Weekdays)
            gate.Add("Mon-Fri");
        else if (s.Days is { Count: < 7 } days)
            gate.Add(string.Join(",", days.OrderBy(d => ((int)d + 6) % 7).Select(d => d.ToString()[..3])));
        if (s.EndDate is { } end)
            gate.Add($"until {end:yyyy-MM-dd}");
        return $"runs {runs} (syncing only {string.Join(", ", gate)})";
    }
}
