using System.Text.Json.Nodes;
using PyCompat;
using static TeamLeave.Tests.Fixtures;

namespace TeamLeave.Tests;

/// <summary>The write door and the read-only fence: the safety design, tested before anything else. Every calendar
/// write goes through the door's writer; the planner reads only through the fence.</summary>
public sealed class WriteDoorTests
{
    private const string Prefer = "Prefer=outlook.timezone=\"Europe/London\"";

    // json.dumps escapes the e-diaeresis (ensure_ascii): a backslash, then u00eb.
    private static readonly string Zoe = "Zo" + (char)92 + "u00eb Example annual leave";

    private static JsonObject Payload() => new() { ["subject"] = "Zoë Example annual leave", ["isAllDay"] = true };

    private static void EveryWrite(ICalendarWriter writer)
    {
        writer.Create(Payload());
        writer.Update("e1", Payload());
        writer.Delete("e2");
    }

    [Fact]
    public void live_sends_exactly_the_python_calls()
    {
        var graph = new RecordingGraph();
        EveryWrite(WriteDoor.Live(graph, "Europe/London").Writer);
        Assert.Equal(
        [
            $"POST /me/events {{\"subject\": \"{Zoe}\", \"isAllDay\": true, \"attendees\": []}} Calendars.ReadWrite {Prefer}",
            $"PATCH /me/events/e1 {{\"subject\": \"{Zoe}\", \"isAllDay\": true}} Calendars.ReadWrite {Prefer}",
            $"DELETE /me/events/e2 - Calendars.ReadWrite {Prefer}",
        ], graph.Calls);
    }

    [Fact]
    public void live_create_returns_the_new_id_and_leaves_the_payload_alone()
    {
        var payload = Payload();
        Assert.Equal("new", WriteDoor.Live(new RecordingGraph(), "Europe/London").Writer.Create(payload));
        Assert.False(payload.ContainsKey("attendees"));
    }

    [Fact]
    public void journal_records_the_same_calls_and_sends_nothing()
    {
        var seen = new List<string>();
        var door = WriteDoor.Journaling("Europe/London", call => seen.Add(call.Text));
        EveryWrite(door.Writer);
        Assert.Equal(WriteMode.Journal, door.Mode);
        Assert.Equal(3, door.Journal.Count);
        Assert.Equal($"POST /me/events {{\"subject\": \"{Zoe}\", \"isAllDay\": true, \"attendees\": []}}", seen[0]);
        Assert.Equal("DELETE /me/events/e2", door.Journal[2].Text);
    }

    [Fact]
    public void refuse_throws_on_every_write_and_sends_nothing()
    {
        var writer = WriteDoor.Refusing("Europe/London").Writer;
        Assert.Throws<WriteRefusedException>(() => writer.Create(Payload()));
        Assert.Throws<WriteRefusedException>(() => writer.Update("e1", Payload()));
        Assert.Throws<WriteRefusedException>(() => writer.Delete("e1"));
    }

    [Theory]
    [InlineData("POST", "/me/events")]
    [InlineData("PATCH", "/me/events/e1")]
    [InlineData("DELETE", "/me/events/e1")]
    [InlineData("PUT", "/me/events/e1")]
    public void the_fence_refuses_every_write(string method, string url)
    {
        var inner = new RecordingGraph();
        var fence = new ReadOnlyGraph(inner);
        Assert.Throws<WriteRefusedException>(() => fence.Send(method, url, [], Calendar.Scopes, Calendar.Headers("UTC")));
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public void the_fence_passes_reads()
    {
        var inner = new RecordingGraph();
        _ = new ReadOnlyGraph(inner).Pages("/me/events", Calendar.Scopes, Calendar.Headers("UTC")).ToList();
        Assert.Single(inner.Calls);
    }

    [Fact]
    public void the_calendar_reads_through_the_fence_even_when_given_the_raw_graph()
    {
        var graph = new RecordingGraph();
        var calendar = new Calendar(graph, WriteDoor.Refusing("Europe/London").Writer, "Europe/London");
        calendar.Owned();
        calendar.Between(Today, Today);
        Assert.All(graph.Calls, call => Assert.StartsWith("GET ", call));
        Assert.Throws<WriteRefusedException>(() => calendar.Create(Payload()));
        Assert.Equal(2, graph.Calls.Count);
    }

    private static (int Code, RecordingGraph Graph, string Out, List<string> Hooks) Cli(Config c, Absence a, params string[] extra)
    {
        var graph = new RecordingGraph();
        var output = new StringWriter();
        var hooks = new List<string>();
        var code = TeamLeave.Cli.Run(["--config", c.Path, .. extra], new CliHost
        {
            Out = output,
            Err = output,
            Graph = (_, _) => graph,
            ReadPeopleFirst = (_, _, _) => ([Jordan], [a]),
            Notify = (_, _) => { },
            HookRunner = (_, input, _, _) =>
            {
                hooks.Add(input);
                return new WinBackground.Completed(0, "", "");
            },
            Now = () => new DateTime(2026, 11, 12, 10, 0, 0),
        });
        return (code, graph, output.ToString(), hooks);
    }

    private static Config WithHook()
    {
        var c = Config(Settings.Replace("max_deletes = 3", "max_deletes = 3\nafter_run = [\"python\", \"hook.py\"]\nstate_file = \"state.json\"\nlog_file = \"log.txt\""));
        return TeamLeave.Config.Load(c.Path);
    }

    [Fact]
    public void a_dry_run_sends_only_reads()
    {
        var c = WithHook();
        var (code, graph, output, hooks) = Cli(c, Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31"), "sync", "--dry-run", "--hook");
        Assert.Equal(0, code);
        Assert.All(graph.Calls, call => Assert.StartsWith("GET ", call));
        Assert.Contains("Would add: Jordan Lee annual leave 24/12 to 31/12", output);
        Assert.Empty(hooks);
        Assert.False(File.Exists(c.StateFile));
    }

    [Fact]
    public void a_dry_run_refuses_writes_even_when_live_is_asked_for()
    {
        var c = WithHook();
        var (code, graph, _, _) = Cli(c, Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31"), "--writes", "live", "sync", "--dry-run");
        Assert.Equal(0, code);
        Assert.All(graph.Calls, call => Assert.StartsWith("GET ", call));
    }

    [Fact]
    public void journal_mode_records_the_writes_sends_only_reads_and_runs_no_hook()
    {
        var c = WithHook();
        var journal = Path.Combine(Path.GetDirectoryName(c.Path)!, "journal.log");
        var (code, graph, _, hooks) = Cli(c, Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31"), "--writes", "journal", "--journal", journal, "sync", "--hook");
        Assert.Equal(0, code);
        Assert.All(graph.Calls, call => Assert.StartsWith("GET ", call));
        Assert.Empty(hooks);
        var lines = File.ReadAllLines(journal);
        Assert.Single(lines);
        Assert.Contains("journal (not sent): POST /me/events {\"subject\": \"Jordan Lee annual leave\"", lines[0]);
    }

    [Fact]
    public void refuse_mode_fails_a_sync_that_would_write()
    {
        var c = WithHook();
        var (code, graph, output, hooks) = Cli(c, Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31"), "--writes", "refuse", "sync", "--hook");
        Assert.Equal(2, code);
        Assert.Contains("Refused a write (POST /me/events)", output);
        Assert.All(graph.Calls, call => Assert.StartsWith("GET ", call));
        Assert.Empty(hooks);
    }

    [Fact]
    public void live_mode_writes_through_the_door_and_runs_the_hook()
    {
        var c = WithHook();
        var (code, graph, _, hooks) = Cli(c, Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31"), "sync", "--hook");
        Assert.Equal(0, code);
        Assert.Single(graph.Calls, call => call.StartsWith("POST /me/events ", StringComparison.Ordinal));
        Assert.Single(hooks);
        Assert.Equal("1 added, 0 changed, 0 removed, 0 unchanged", Py.Str(PyJson.Parse(hooks[0])!["summary"]));
    }
}
