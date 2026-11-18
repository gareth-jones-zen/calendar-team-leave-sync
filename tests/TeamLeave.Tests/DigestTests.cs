using System.Text.Json.Nodes;
using static TeamLeave.Tests.Fixtures;

namespace TeamLeave.Tests;

/// <summary>digest (1.4.0): People First's notifications email against what team-leave has seen. Names are made up.</summary>
public sealed class DigestTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 9, 0, 0);

    private static string Email(params string[] lines) =>
        new JsonObject { ["subject"] = "Today's notifications", ["body"] = string.Join("\n", lines) }.ToJsonString();

    private static JsonObject Seen(params (Person Person, string Start, string End, string Kind)[] read)
    {
        var state = new JsonObject();
        Digest.Note(state, read.Select(r => (r.Person, D(r.Start), D(r.End), r.Kind)), Now);
        return state;
    }

    [Fact]
    public void A_digest_of_leave_requests_team_leave_has_seen_can_go()
    {
        var state = Seen((Jordan, "2026-12-21", "2026-12-24", "waiting"), (Robin, "2026-12-07", "2026-12-07", "approved"));
        var (code, note) = Digest.Check(Config(), state, Email(
            "2 unread notifications",
            "Jordan Lee (Holiday) has requested leave for 21 Dec 2026 - 24 Dec 2026 [https://example.people-first.com/x]",
            "Robin  Hall has requested leave for 7 December 2026"));
        Assert.Equal((0, "2 leave request(s), all seen by team-leave (approved, waiting)"), (code, note));
    }

    [Fact]
    public void Graphs_text_links_are_taken_off_the_names()
    {
        var state = Seen((Jordan, "2026-12-29", "2026-12-31", "approved"));
        var (code, _) = Digest.Check(Config(), state, Email(
            "<https://example.people-first.com/notifications?id=1&x=2>Jordan Lee has requested leave for 29 Dec 2026 - 31 Dec 2026"));
        Assert.Equal(0, code);
        Assert.Equal(0, Digest.Check(Config(), state, Email("JL Jordan Lee has requested leave for 29 Dec 2026 - 31 Dec 2026")).Code);
    }

    [Fact]
    public void Matching_is_by_person_as_well_as_dates()
    {
        var state = Seen((Jordan, "2026-12-21", "2026-12-24", "waiting"));
        var (code, note) = Digest.Check(Config(), state, Email("Robin Hall has requested leave for 21 Dec 2026 - 24 Dec 2026"));
        Assert.Equal((Digest.TryAgain, "1 of 1 leave request(s) not yet seen by team-leave: will check again"), (code, note));
    }

    [Fact]
    public void Peoples_formal_names_are_matched_through_names()
    {
        var config = Config(Settings + "[names]\n\"Jordan Alexander Lee\" = \"Jordan Lee\"\n");
        var state = Seen((Jordan, "2026-12-21", "2026-12-21", "approved"));
        Assert.Equal(0, Digest.Check(config, state, Email("Jordan Alexander Lee has requested leave for 21 Dec 2026")).Code);
    }

    [Theory]
    [InlineData("Your leave for 1 Dec 2026 has been approved")]                                       // not a request
    [InlineData("3 unread notifications", "Jordan Lee has requested leave for 21 Dec 2026")]          // more than the requests
    public void Anything_else_is_raised(params string[] lines) =>
        Assert.Equal(1, Digest.Check(Config(), Seen((Jordan, "2026-12-21", "2026-12-21", "waiting")), Email(lines)).Code);

    [Fact]
    public void Seen_entries_go_after_thirty_days_unread()
    {
        var state = Seen((Jordan, "2026-12-21", "2026-12-21", "waiting"));
        Digest.Note(state, [(Robin, D("2027-01-04"), D("2027-01-04"), "waiting")], Now.AddDays(29));
        Assert.Equal(2, state["seen"]!.AsObject().Count);
        Digest.Note(state, [(Robin, D("2027-01-04"), D("2027-01-04"), "approved")], Now.AddDays(31));
        var left = Assert.Single(state["seen"]!.AsObject());
        Assert.Equal((Digest.Fingerprint("Robin Hall", D("2027-01-04"), D("2027-01-04")), "approved"), (left.Key, (string?)left.Value!["kind"]));
        foreach (var secret in new[] { "Robin", "Hall", "2027-01-04" })
            Assert.DoesNotContain(secret, state.ToJsonString());   // only fingerprints, as the state file never holds names or dates
    }

    [Fact]
    public void The_command_reads_stdin_and_the_state_file()
    {
        var folder = new TempFolder();
        var config = Config($"state_file = '{folder.File("state.json")}'\n" + Settings);
        config.StateFile = folder.File("state.json");   // where the command line will read it (the fixture moves it)
        Runner.SaveState(config, Seen((Jordan, "2026-12-21", "2026-12-24", "waiting")));
        var output = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", config.Path, "digest"], new CliHost
        {
            Out = output, Err = output, Env = _ => null,
            In = new StringReader(Email("Jordan Lee has requested leave for 21 Dec 2026 - 24 Dec 2026")),
        });
        Assert.Equal(0, code);
        Assert.StartsWith("1 leave request(s), all seen by team-leave", output.ToString());
    }
}
