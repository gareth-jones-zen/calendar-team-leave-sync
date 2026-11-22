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

    /// <summary>When it runs: at logon and every every_minutes, as a job stopped after 20 minutes, started now.</summary>
    public static TaskSchedule Schedule(int everyMinutes) => new()
    {
        AtLogon = true,
        Every = TimeSpan.FromMinutes(everyMinutes),
        KeepRunning = false,
        TimeLimit = TimeSpan.FromMinutes(20),
    };
}
