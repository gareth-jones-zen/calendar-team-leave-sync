using PyCompat;
using WinBackground;
using static TeamLeave.Tests.Fixtures;

namespace TeamLeave.Tests;

/// <summary>[schedule]: the library's shared scheduling settings, with the old top-level keys still read.</summary>
public sealed class ScheduleTests
{
    /// <summary>A task that keeps what it was registered with, description (and so the fingerprint) included.</summary>
    private sealed class RecordingTask(string name) : ITask
    {
        public string Name { get; } = name;
        public TaskSchedule? Registered;

        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir) => Enable(exe, args, workingDir, TaskSchedule.Watcher);

        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir, TaskSchedule schedule)
        {
            Registered = schedule;
            return exe + " " + string.Join(" ", args);
        }

        public bool Disable() => Registered is not null && (Registered = null) is null;
        public bool Stop() => true;

        public IReadOnlyDictionary<string, string>? Status() => Registered is null ? null : new Dictionary<string, string>
        {
            ["state"] = "Ready",
            ["command"] = "x",
            ["description"] = Registered.Fingerprint is { } fp ? ScheduleDrift.WithFingerprint("desc", fp) : "desc",
        };
    }

    private static (int Code, string Out, string Err) Autostart(Config c, RecordingTask task, string action)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = TeamLeave.Cli.Run(["--config", c.Path, "autostart", action], new CliHost
        {
            Out = output,
            Err = error,
            Tasks = _ => task,
            Notify = (_, _) => { },
        });
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Without_schedule_settings_the_defaults_are_unchanged()
    {
        var c = Config();
        Assert.Equal((15, ("07:00", "19:00"), true), (c.EveryMinutes, c.Hours, c.Weekdays));
        Assert.False(c.ScheduleFromOldKeys);
        var task = TeamLeave.Autostart.Schedule(c);
        Assert.Equal((true, TimeSpan.FromMinutes(15), (TimeSpan?)null, false, TimeSpan.FromMinutes(20)),
            (task.AtLogon, task.Every, task.AfterUnlock, task.KeepRunning, task.TimeLimit));
        Assert.Equal("v1 job every=15 daily=- logon=yes unlock=0", task.Fingerprint);
    }

    [Fact]
    public void A_schedule_table_is_read()
    {
        var c = Config(Settings + "\n[schedule]\nevery_minutes = 60\nhours = \"00:00-23:59\"\n");
        Assert.Equal((60, ("00:00", "23:59")), (c.EveryMinutes, c.Hours));
        Assert.True(c.Weekdays);   // days not given: the default Mon-Fri
        Assert.False(c.ScheduleFromOldKeys);
        Assert.Equal(TimeSpan.FromMinutes(60), TeamLeave.Autostart.Schedule(c).Every);
        Assert.True(Runner.InHours(c, new DateTime(2026, 11, 12, 6, 0, 0)));
        Assert.False(Runner.InHours(c, new DateTime(2026, 11, 14, 10, 0, 0)));   // a Saturday

        var allWeek = Config(Settings + "\n[schedule]\nevery_minutes = 60\nhours = \"00:00-23:59\"\ndays = \"Mon-Sun\"\n");
        Assert.True(Runner.InHours(allWeek, new DateTime(2026, 11, 14, 10, 0, 0)));
    }

    [Fact]
    public void The_old_top_level_keys_still_work_and_are_noted()
    {
        var c = Config(Settings.Replace("max_deletes = 3", "max_deletes = 3\nevery_minutes = 60\nhours = \"00:00-23:59\"\nweekdays = false"));
        Assert.Equal((60, ("00:00", "23:59"), false), (c.EveryMinutes, c.Hours, c.Weekdays));
        Assert.True(c.ScheduleFromOldKeys);
        Assert.True(Runner.InHours(c, new DateTime(2026, 11, 14, 10, 0, 0)));   // weekdays = false: Saturdays too
        Assert.Contains("[schedule]", Runner.OldScheduleNote);
    }

    [Fact]
    public void Both_forms_at_once_is_an_error()
    {
        var error = Assert.Throws<PyValueError>(() =>
            Config(Settings.Replace("max_deletes = 3", "max_deletes = 3\nevery_minutes = 60") + "\n[schedule]\nevery_minutes = 30\n"));
        Assert.Contains("[schedule]", error.Message);
        Assert.Contains("every_minutes", error.Message);
    }

    [Fact]
    public void A_bad_schedule_is_an_error()
    {
        Assert.Throws<PyValueError>(() => Config(Settings + "\n[schedule]\nevery_minute = 60\n"));
        Assert.Throws<PyValueError>(() => Config(Settings + "\n[schedule]\nevery_minutes = 60\ndaily_at = \"07:00\"\n"));
    }

    [Fact]
    public void End_date_stops_scheduled_runs()
    {
        var c = Config(Settings + "\n[schedule]\nend_date = \"2026-11-13\"\n");
        Assert.True(Runner.InHours(c, new DateTime(2026, 11, 12, 10, 0, 0)));
        Assert.False(Runner.InHours(c, new DateTime(2026, 11, 13, 10, 0, 0)));
    }

    [Fact]
    public void Autostart_records_the_schedule_and_status_spots_a_change()
    {
        var task = new RecordingTask(TeamLeave.Autostart.TaskName);
        var hourly = Config(Settings + "\n[schedule]\nevery_minutes = 60\nhours = \"00:00-23:59\"\n");
        var (code, output, _) = Autostart(hourly, task, "on");
        Assert.Equal(0, code);
        Assert.Contains("runs every 60 minutes and at logon (syncing only 00:00-23:59, Mon-Fri)", output);
        Assert.Equal("v1 job every=60 daily=- logon=yes unlock=0", task.Registered!.Fingerprint);

        Assert.DoesNotContain("settings changed", Autostart(hourly, task, "status").Out);

        File.WriteAllText(hourly.Path, File.ReadAllText(hourly.Path).Replace("every_minutes = 60", "every_minutes = 30"));
        var changed = TeamLeave.Config.Load(hourly.Path);
        Assert.Contains("settings changed since this task was registered: run `calendar-team-leave-sync autostart on`",
            Autostart(changed, task, "status").Out);
    }
}
