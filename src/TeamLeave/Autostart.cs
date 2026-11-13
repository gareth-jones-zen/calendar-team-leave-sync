using PyCompat;
using WinBackground;

namespace TeamLeave;

/// <summary>A repeating sync through Windows Task Scheduler (WinBackground).
///
/// The task runs `sync --hook --scheduled` every every_minutes, and at logon, and catches up a run missed while the
/// laptop was asleep. A scheduled run outside `hours` (and at weekends, with `weekdays`) exits at once, writing nothing
/// and logging nothing. It runs as you, only while you're logged on, with no admin rights needed, and runs
/// team-leave-background.exe directly, so there's no window. The same task name as the Python version, so switching
/// replaces the Python task rather than adding a second.</summary>
public static class Autostart
{
    public const string TaskName = "team-leave sync";
    public const string ConsoleExe = "team-leave.exe";
    public const string BackgroundExe = "team-leave-background.exe";

    public static ITask Create(string name) =>
        new ScheduledTask(name, "Copies your reports' approved leave from People First into your calendar (team-leave sync).");

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
