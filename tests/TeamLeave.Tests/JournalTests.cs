using ActivityJournal;
using static TeamLeave.Tests.Fixtures;

namespace TeamLeave.Tests;

/// <summary>1.14.0: each calendar entry written and each approval sent is a line in the shared activity journal
/// (background-tools-lib's ActivityJournal), restricted, through a live door only; never a dry run or journal mode, and
/// writing it never fails the run. Every person here is made up.</summary>
public sealed class JournalTests
{
    private const string Rules = "[approvals]\nact = true\nmax_per_run = 5\ntypes = [\"Holiday\", \"My Birthday\"]\n";
    private static readonly DateTime Now = new(2026, 11, 12, 10, 0, 0);

    /// <summary>The fixtures' settings with a journal beside them (and the state and log, for the command line).</summary>
    private static Config Settings(string extra = "", string journal = "journal_file = \"journal.jsonl\"\n")
    {
        var c = Config(Fixtures.Settings.Replace("max_deletes = 3", "max_deletes = 3\nstate_file = \"state.json\"\nlog_file = \"log.txt\"\n" + journal) + extra);
        return TeamLeave.Config.Load(c.Path);
    }

    private static IReadOnlyList<JournalEntry> Lines(Config c) =>
        File.Exists(c.JournalFile) ? new Journal(c.JournalFile!).Read() : [];

    private static void Sync(Config c, RecordingGraph graph, LeaveReader read, WriteDoor? door = null, bool dryRun = false) =>
        Runner.Run(c, graph, door ?? (dryRun ? WriteDoor.Refusing("Europe/London") : WriteDoor.Live(graph, "Europe/London")), read, Quiet,
            dryRun: dryRun, now: () => Now, today: Today, alert: (_, _) => { });

    [Fact]
    public void A_sync_journals_each_entry_it_wrote_restricted_and_a_dry_run_or_journal_mode_nothing()
    {
        var c = Settings();
        var a = Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31");
        var graph = new RecordingGraph();
        Sync(c, graph, (_, _, _) => ([Jordan], [a]), dryRun: true);
        Sync(c, graph, (_, _, _) => ([Jordan], [a]), door: WriteDoor.Journaling("Europe/London"));
        Assert.Empty(Lines(c));

        Sync(c, graph, (_, _, _) => ([Jordan], [a]));
        var line = Assert.Single(Lines(c));
        Assert.Equal("calendar-team-leave-sync", line.Tool);
        Assert.Equal("add", line.What);
        Assert.Equal(By.Auto, line.By);
        Assert.Equal("Jordan Lee annual leave 24/12 to 31/12", line.Subject);
        Assert.Equal("sync", line.Rule);
        Assert.Equal("approved leave in People First", line.Why);
        Assert.Equal("new", line.Item);
        Assert.Equal(Sensitivity.Restricted, line.Sensitivity);
        Assert.Null(line.Undo);
    }

    [Fact]
    public void Changed_and_cancelled_leave_are_update_and_remove_lines()
    {
        var c = Settings();
        var a1 = Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31");
        var gone = Absence(c, "A9", Robin, "2026-12-01", "2026-12-01");
        var graph = new RecordingGraph();
        graph.Answers[Calendar.OwnedUrl()] = [Recorded.Item(c, Owned(c, a1 with { End = D("2026-12-30") }, "E1")), Recorded.Item(c, Owned(c, gone, "E9"))];

        Sync(c, graph, (_, _, _) => ([Jordan, Robin], [a1]));

        var lines = Lines(c);
        Assert.Equal(2, lines.Count);
        var updated = Assert.Single(lines, l => l.What == "update");
        Assert.Equal("Jordan Lee annual leave 24/12 to 31/12", updated.Subject);
        Assert.Equal("changed in People First, or edited by hand (was: Jordan Lee annual leave 24/12 to 30/12)", updated.Why);
        Assert.Equal("E1", updated.Item);
        var removed = Assert.Single(lines, l => l.What == "remove");
        Assert.Equal("Robin Hall annual leave 01/12", removed.Subject);
        Assert.Equal("no longer approved leave in People First", removed.Why);
        Assert.All(lines, l => Assert.Equal(Sensitivity.Restricted, l.Sensitivity));
    }

    [Fact]
    public void An_approval_by_the_rules_is_auto_and_by_hand_is_hand_neither_with_an_undo()
    {
        var c = Settings(Rules);
        var pf = new FakePeopleFirst().Add(Jordan, Event("A1", "2026-12-01", "2026-12-02", state: "AwaitingApproval"));
        var graph = new RecordingGraph();
        Runner.Run(c, graph, WriteDoor.Live(graph, "Europe/London"), (_, _, _) => throw new InvalidOperationException("opened, not read"),
            Quiet, now: () => Now, today: Today, alert: (_, _) => { }, open: (_, _) => pf);

        var approved = Assert.Single(Lines(c), l => l.What == "approve");
        Assert.Equal(By.Auto, approved.By);
        Assert.Equal("[approvals]", approved.Rule);
        Assert.Equal("Jordan Lee annual leave 01/12 to 02/12", approved.Subject);
        Assert.Equal("every [approvals] rule held; People First shows it approved", approved.Why);
        Assert.Equal("A1", approved.Item);
        Assert.Equal(Sensitivity.Restricted, approved.Sensitivity);
        Assert.Null(approved.Undo);
        Assert.Single(Lines(c), l => l.What == "add");   // and its entry, in the same run

        var c2 = Settings();
        var pf2 = new FakePeopleFirst().Add(Robin, Event("B1", "2026-12-08", "2026-12-08", state: "AwaitingApproval"));
        Assert.Equal(0, Cli(c2, pf2, "approve", "B1"));   // a preview: nothing sent, nothing journalled
        Assert.Empty(Lines(c2));
        Assert.Equal(0, Cli(c2, pf2, "approve", "B1", "--yes"));
        var byHand = Assert.Single(Lines(c2));
        Assert.Equal(("approve", By.Hand, "by hand"), (byHand.What, byHand.By, byHand.Rule));
        Assert.Equal("approved by hand; People First shows it approved", byHand.Why);
    }

    [Fact]
    public void An_approval_whose_answer_is_lost_is_unconfirmed()
    {
        var c = Settings();
        var pf = new FakePeopleFirst().Add(Robin, Event("B1", "2026-12-08", "2026-12-08", state: "AwaitingApproval"));
        pf.OnPost = (_, _) => throw new TimeoutException("timed out");
        Assert.Equal(2, Cli(c, pf, "approve", "B1", "--yes"));
        var line = Assert.Single(Lines(c));
        Assert.Equal(JournalEntry.Unconfirmed, line.Outcome);
    }

    [Fact]
    public void Without_journal_file_nothing_is_written_and_a_bad_journal_never_fails_the_run()
    {
        var c = Settings(journal: "");
        Assert.Null(c.JournalFile);
        var graph = new RecordingGraph();
        Sync(c, graph, (_, _, _) => ([Jordan], [Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31")]));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(c.Path)!, "*.jsonl"));

        var c2 = Settings();
        Directory.CreateDirectory(c2.JournalFile!);   // a folder where the file should be
        var graph2 = new RecordingGraph();
        var lines = new List<string>();
        Runner.Run(c2, graph2, WriteDoor.Live(graph2, "Europe/London"), (_, _, _) => ([Jordan], [Absence(c2, "A1", Jordan, "2026-12-24", "2026-12-31")]),
            lines.Add, now: () => Now, today: Today, alert: (_, _) => { });
        Assert.Single(graph2.Calls, call => call.StartsWith("POST ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("journal: couldn't write", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("Jordan", StringComparison.Ordinal));   // the log stays names-free
    }

    private static int Cli(Config c, FakePeopleFirst pf, params string[] argv) =>
        TeamLeave.Cli.Run(["--config", c.Path, .. argv], new CliHost
        {
            Out = new StringWriter(),
            Err = new StringWriter(),
            Graph = (_, _) => new RecordingGraph(),
            ReadPeopleFirst = (_, _, _) => throw new InvalidOperationException("opened, not read"),
            OpenPeopleFirst = (_, _) => pf,
            Notify = (_, _) => { },
            Now = () => Now,
        });
}
