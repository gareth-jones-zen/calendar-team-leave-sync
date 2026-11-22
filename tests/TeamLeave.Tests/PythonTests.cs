using System.Text.Json.Nodes;
using PyCompat;
using static TeamLeave.Tests.Fixtures;

namespace TeamLeave.Tests;

// The 37 tests of tests/test_team_leave.py, by name and in the same classes.

public sealed class ConfigTest
{
    [Fact]
    public void test_defaults()
    {
        var c = Config("[people_first]\nurl = 'https://acme.people-first.com'\n");
        Assert.Equal([new KeyValuePair<string, string>("Holiday", "{name} annual leave")], c.Types);
        Assert.Equal("\"Approved\"", Assert.Single(c.States)!.ToJsonString());
        Assert.Equal("acme", c.PeopleFirst.TenantCode);
        Assert.Equal("[\"Planned\"]", PyJson.Dump(new JsonArray(c.Event.Categories.Select(x => x?.DeepClone()).ToArray()), null, false).Replace(" ", ""));
        Assert.Null(c.Event.Reminder);
        Assert.Equal(("normal", "free"), (c.Event.Sensitivity, c.Event.ShowAs));
        Assert.Equal((365, 15, ("07:00", "19:00"), true), (c.DaysAhead, c.EveryMinutes, c.Hours, c.Weekdays));
    }

    [Fact]
    public void test_bad_settings()
    {
        foreach (var text in new[]
                 {
                     "[people_first]\nurl='http://x'\n", Settings + "[titles]\nam = '(AM)'\n",
                     Settings.Replace("\"{name} annual leave\"\n\"My", "\"annual leave\"\n\"My"),
                     Settings + "[event]\nshow_as = 'away'\n", Settings + "[event]\nreminder = true\n",
                 })
            Assert.Throws<PyValueError>(() => Config(text));
    }
}

public sealed class LeaveTest
{
    [Fact]
    public void test_types_and_states()
    {
        var c = Config();
        var body = Listing(Event("A1", "2026-12-24", "2026-12-31"), Event("A2", "2027-03-03", "2027-03-03", "T2"),
            Event("A3", "2026-11-20", "2026-11-20", "T3"), Event("A4", "2026-11-25", "2026-11-25", "T4"),
            Event("A5", "2027-01-05", "2027-01-06", state: "AwaitingApproval"),
            Event("A6", "2027-01-07", "2027-01-07", state: "Rejected"));
        var found = Leave.Absences(c, Jordan, body);
        Assert.Equal(["A1", "A2"], found.Select(a => a.Id));
        // The birthday looks exactly like annual leave: the type never reaches the title.
        Assert.Equal(["Jordan Lee annual leave", "Jordan Lee annual leave"], found.Select(a => a.Title(c)));
    }

    [Fact]
    public void test_holiday_only_by_default()
    {
        var c = Config("[people_first]\nurl = 'https://acme.people-first.com'\n");
        var found = Leave.Absences(c, Jordan, Listing(Event("A1", "2026-12-24", "2026-12-24"), Event("A2", "2027-03-03", "2027-03-03", "T2")));
        Assert.Equal(["A1"], found.Select(a => a.Id));
    }

    [Fact]
    public void test_half_day_titles()
    {
        var c = Config();
        var cases = new[]
        {
            ("2026-12-01", "2026-12-01", "HALF_AM", "HALF_AM", "Jordan Lee annual leave (AM)"),
            ("2026-12-01", "2026-12-01", "HALF_PM", "HALF_PM", "Jordan Lee annual leave (PM)"),
            ("2026-12-01", "2026-12-01", "FULL", "HALF_PM", "Jordan Lee annual leave (PM)"),
            ("2026-12-01", "2026-12-01", "HALF_AM", "HALF_PM", "Jordan Lee annual leave"),
            ("2026-12-01", "2026-12-03", "HALF_PM", "FULL", "Jordan Lee annual leave (from PM)"),
            ("2026-12-01", "2026-12-03", "FULL", "HALF_AM", "Jordan Lee annual leave (to AM)"),
            ("2026-12-01", "2026-12-03", "HALF_PM", "HALF_AM", "Jordan Lee annual leave (from PM) (to AM)"),
            ("2026-12-01", "2026-12-03", "HALF_AM", "HALF_PM", "Jordan Lee annual leave"),
        };
        foreach (var (start, end, sp, ep, title) in cases)
            Assert.Equal(title, Absence(c, "A", Jordan, start, end, sp, ep).Title(c));
    }

    [Fact]
    public void test_parts_by_annotated_name()
    {
        var c = Config();
        var body = Listing(Event("A1", "2026-12-01", "2026-12-01", startPart: "X9", endPart: "X9"));
        body["meta"]!["events.startTypeId"]!["values"]!.AsArray().Add((JsonNode)new JsonObject { ["id"] = "X9", ["value"] = "PM" });
        body["meta"]!["events.endTypeId"]!["values"]!.AsArray().Add((JsonNode)new JsonObject { ["id"] = "X9", ["value"] = "PM" });
        Assert.Equal("Jordan Lee annual leave (PM)", Leave.Absences(c, Jordan, body)[0].Title(c));
    }

    [Fact]
    public void test_names_override()
    {
        var c = Config(Settings + "[names]\n\"Robert Hall\" = \"Robin Hall\"\n");
        Assert.Equal("Robin Hall", Leave.DisplayName(c, new JsonObject { ["firstName"] = "Robert", ["lastName"] = "Hall" }));
        Assert.Equal("Jordan Lee", Leave.DisplayName(c, new JsonObject { ["firstName"] = "Jordan", ["lastName"] = "Lee" }));
    }

    [Fact]
    public void test_changed_shape_raises()
    {
        var c = Config();
        Assert.Throws<LeaveError>(() => Leave.Absences(c, Jordan, new JsonObject { ["data"] = new JsonObject { ["events"] = new JsonArray() }, ["meta"] = new JsonObject() }));
        Assert.Throws<LeaveError>(() => Leave.Absences(c, Jordan, new JsonObject { ["items"] = new JsonArray() }));
    }

    [Fact]
    public void test_unknown_configured_type_raises()
    {
        var c = Config(Settings + "\"Birthday Leave\" = \"{name} annual leave\"\n");
        var api = new FakeApi(person => person == "ME" ? [new JsonObject { ["personId"] = "P1", ["firstName"] = "Jordan", ["lastName"] = "Lee" }] : [],
            _ => Listing());
        Assert.Throws<LeaveError>(() => Leave.Read(c, api, Today, Quiet));
    }
}

public sealed class PlanTest
{
    private readonly Config c = Config();
    private readonly Absence a1;
    private readonly Absence a2;

    public PlanTest()
    {
        a1 = Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31");
        a2 = Absence(c, "A2", Robin, "2026-11-20", "2026-11-20", "HALF_PM", "HALF_PM");
    }

    private Plan MakePlan(IReadOnlyList<Absence> absences, FakeCalendar calendar, bool survey = false) =>
        Sync.MakePlan(c, [Jordan, Robin], absences, calendar, Today, survey: survey);

    [Fact]
    public void test_create_update_delete_and_unchanged()
    {
        var moved = Absence(c, "A1", Jordan, "2026-12-23", "2026-12-31");
        var gone = Owned(c, Absence(c, "A9", Robin, "2026-12-01", "2026-12-01"), "E9");
        var calendar = new FakeCalendar([Owned(c, a1, "E1"), gone]);
        var p = MakePlan([moved, a2], calendar);
        Assert.Equal(["A2"], p.Create.Select(x => x.Absence.Id));
        Assert.Equal([("A1", "E1")], p.Update.Select(x => (x.Absence.Id, x.Entry.Id)));
        Assert.Equal(["E9"], p.Delete.Select(e => e.Id));
        Sync.Apply(p, calendar, Quiet);
        Assert.Equal(["create", "update", "delete"], calendar.Writes.Select(w => w.Op));
        var created = calendar.Writes[0].Payload!;
        Assert.Equal("Robin Hall annual leave (PM)", Py.Str(created["subject"]));
        Assert.Equal(("2026-11-20T00:00:00", "2026-11-21T00:00:00"), (Py.Str(created["start"]!["dateTime"]), Py.Str(created["end"]!["dateTime"])));
        Assert.True(created["isAllDay"]!.GetValue<bool>());
        Assert.Equal(("free", "normal", "[\"Planned\"]", false),
            (Py.Str(created["showAs"]), Py.Str(created["sensitivity"]), created["categories"]!.ToJsonString(), created["isReminderOn"]!.GetValue<bool>()));
        var props = created["singleValueExtendedProperties"]!.AsArray().ToDictionary(x => Py.Str(x!["id"]), x => Py.Str(x!["value"]));
        Assert.Equal("A2", props[Calendar.AbsenceProp]);
        Assert.DoesNotContain("Holiday", created.ToJsonString());
    }

    [Fact]
    public void test_nothing_to_do_writes_nothing_and_reads_no_calendar_range()
    {
        var calendar = new FakeCalendar([Owned(c, a1, "E1"), Owned(c, a2, "E2")]);
        var p = MakePlan([a1, a2], calendar);
        Assert.Equal((0, 2, 0), (p.Writes, p.Unchanged, calendar.BetweenCalls));
    }

    [Fact]
    public void test_hand_edit_is_overwritten()
    {
        var calendar = new FakeCalendar([Owned(c, a1, "E1") with { Subject = "Jordan off" }]);
        Assert.Equal(["E1"], MakePlan([a1], calendar).Update.Select(u => u.Entry.Id));
    }

    [Fact]
    public void test_settings_change_rewrites()
    {
        var calendar = new FakeCalendar([Owned(c, a1, "E1")]);
        c.Event.Categories = [JsonValue.Create("Leave")];
        Assert.Single(MakePlan([a1], calendar).Update);
    }

    [Fact]
    public void test_past_and_unowned_entries_never_touched()
    {
        var past = Owned(c, Absence(c, "A8", Jordan, "2026-11-02", "2026-11-06"), "E8");
        var calendar = new FakeCalendar([past, Hand("H1", "Team offsite", "2026-12-01", "2026-12-01"),
            Hand("H2", "Jordan Lee annual leave", "2026-10-01", "2026-10-02"), Hand("H3", "Robin Hall on leave", "2027-02-01", "2027-02-01")]);
        var p = MakePlan([a1], calendar, survey: true);
        Sync.Apply(p, calendar, Quiet);
        Assert.DoesNotContain(calendar.Writes, w => w.Op != "create");
        Assert.Equal(["create"], calendar.Writes.Select(w => w.Op));
        // H3 looks like Robin's leave but matches no absence: reported, never touched.
        Assert.Equal(["H3"], p.UnmatchedHand.Select(e => e.Id));
    }

    [Fact]
    public void test_ongoing_owned_entry_can_still_be_removed()
    {
        var ongoing = Owned(c, Absence(c, "A7", Jordan, "2026-11-10", "2026-11-13"), "E7");
        Assert.Equal(["E7"], MakePlan([a1], new FakeCalendar([ongoing])).Delete.Select(e => e.Id));
    }

    [Fact]
    public void test_duplicate_owned_entries_tidied()
    {
        var p = MakePlan([a1], new FakeCalendar([Owned(c, a1, "E1"), Owned(c, a1, "E2")]));
        Assert.Equal(["E2"], p.Delete.Select(e => e.Id));
        Assert.Equal(1, p.Unchanged);
    }
}

public sealed class AdoptTest
{
    private readonly Config c = Config();
    private readonly Absence a1;
    private readonly Absence a2;

    public AdoptTest()
    {
        a1 = Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31");
        a2 = Absence(c, "A2", Robin, "2026-11-20", "2026-11-20", "HALF_PM", "HALF_PM");
    }

    private Plan MakePlan(IReadOnlyList<Absence> absences, FakeCalendar calendar, bool adopt = false) =>
        Sync.MakePlan(c, [Jordan, Robin], absences, calendar, Today, adopt: adopt);

    [Fact]
    public void test_exact_match_suppresses_create()
    {
        var calendar = new FakeCalendar([Hand("H1", "Jordan Lee on leave", "2026-12-24", "2026-12-31"),
            Hand("H2", "robin hall annual leave PM", "2026-11-20", "2026-11-20")]);
        var p = MakePlan([a1, a2], calendar);
        Assert.Equal((0, 0), (p.Create.Count, p.Adopt.Count));
        Assert.Equal([["H1"], ["H2"]], p.ByHand.Select(b => b.Entries.Select(e => e.Id).ToArray()));
        Sync.Apply(p, calendar, Quiet);
        Assert.Empty(calendar.Writes);
    }

    [Fact]
    public void test_adopt_takes_over_and_retitles()
    {
        var calendar = new FakeCalendar([Hand("H1", "Jordan Lee on leave", "2026-12-24", "2026-12-31")]);
        var p = MakePlan([a1], calendar, adopt: true);
        Assert.Equal(["H1"], p.Adopt.Select(x => x.Entry.Id));
        Sync.Apply(p, calendar, Quiet);
        var (op, id, payload) = calendar.Writes[0];
        Assert.Equal(("update", "H1", "Jordan Lee annual leave"), (op, id, Py.Str(payload!["subject"])));
        Assert.Contains(Calendar.AbsenceProp, payload["singleValueExtendedProperties"]!.AsArray().Select(x => Py.Str(x!["id"])));
        var r = Sync.Report(c, p, dryRun: false);
        Assert.Equal("Jordan Lee on leave 24/12 to 31/12", Py.Str(r["adopted"]![0]!["was"]));
    }

    [Fact]
    public void test_no_match_on_different_dates_other_name_attendees_or_timed()
    {
        var calendar = new FakeCalendar([
            Hand("H1", "Jordan Lee annual leave", "2026-12-24", "2026-12-30"),
            Hand("H2", "Jordan Leeds annual leave", "2026-12-24", "2026-12-31"),
            Hand("H3", "Jordan Lee annual leave", "2026-12-24", "2026-12-31") with { Attendees = 2 },
            Hand("H4", "Jordan Lee annual leave", "2026-12-24", "2026-12-31", allDay: false),
            Hand("H5", "Jordan Lee annual leave - skiing", "2026-12-24", "2026-12-31"),
            Hand("H6", "Jordan Lee annual leave", "2026-12-24", "2026-12-31") with { Organiser = false }]);
        var p = MakePlan([a1], calendar, adopt: true);
        Assert.Equal(["A1"], p.Create.Select(x => x.Absence.Id));
        Assert.Empty(p.Adopt);
        Assert.Empty(p.ByHand);
    }

    [Fact]
    public void test_ongoing_hand_entry_not_adopted()
    {
        var a = Absence(c, "A3", Jordan, "2026-11-10", "2026-11-13");
        var p = MakePlan([a], new FakeCalendar([Hand("H1", "Jordan Lee annual leave", "2026-11-10", "2026-11-13")]), adopt: true);
        Assert.Empty(p.Adopt);
        Assert.Equal([["H1"]], p.ByHand.Select(b => b.Entries.Select(e => e.Id).ToArray()));
    }

    [Fact]
    public void test_split_by_hand_around_a_weekend()
    {
        var a = Absence(c, "A3", Jordan, "2026-11-18", "2026-11-25");   // Wed to the next Wed
        var pieces = new[] { Hand("H1", "Jordan Lee annual leave", "2026-11-18", "2026-11-20"), Hand("H2", "Jordan Lee annual leave", "2026-11-23", "2026-11-25") };
        var p = MakePlan([a], new FakeCalendar(pieces), adopt: true);
        Assert.Empty(p.Create);
        Assert.Empty(p.Adopt);
        Assert.Equal([["H1", "H2"]], p.ByHand.Select(b => b.Entries.Select(e => e.Id).ToArray()));
        Assert.Equal(2, Py.Int(Sync.Report(c, p, dryRun: true)["by_hand"]![0]!["pieces"]));
        // A weekday not covered (Mon 23 Nov missing): not leave entered by hand, so calendar-team-leave-sync writes its own.
        var gap = new[] { pieces[0], Hand("H2", "Jordan Lee annual leave", "2026-11-24", "2026-11-25") };
        Assert.Equal(["A3"], MakePlan([a], new FakeCalendar(gap)).Create.Select(x => x.Absence.Id));
    }
}

public sealed class SafetyTest
{
    private readonly Config c = Config();

    [Fact]
    public void test_everything_vanished()
    {
        var a = Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31");
        Assert.Throws<SafetyStop>(() => Sync.MakePlan(c, [Jordan], [], new FakeCalendar([Owned(c, a, "E1")]), Today));
        // ...but with only past entries owned, an empty People First is fine.
        var past = Owned(c, Absence(c, "A2", Jordan, "2026-10-01", "2026-10-02"), "E2");
        Assert.Equal(0, Sync.MakePlan(c, [Jordan], [], new FakeCalendar([past]), Today).Writes);
    }

    [Fact]
    public void test_deletion_cap()
    {
        var keep = Absence(c, "K", Jordan, "2027-01-04", "2027-01-04");
        var entries = new List<Entry> { Owned(c, keep, "EK") };
        for (var i = 1; i < 5; i++)
            entries.Add(Owned(c, Absence(c, $"A{i}", Robin, $"2026-12-0{i}", $"2026-12-0{i}"), $"E{i}"));
        Assert.Throws<SafetyStop>(() => Sync.MakePlan(c, [Jordan, Robin], [keep], new FakeCalendar(entries), Today));
        Assert.Equal(4, Sync.MakePlan(c, [Jordan, Robin], [keep], new FakeCalendar(entries), Today, maxDeletes: 4).Delete.Count);
    }

    [Fact]
    public void test_vanished_can_be_overridden_explicitly()
    {
        var a = Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31");
        var p = Sync.MakePlan(c, [Jordan], [], new FakeCalendar([Owned(c, a, "E1")]), Today, maxDeletes: 1);
        Assert.Equal(["E1"], p.Delete.Select(e => e.Id));
    }

    [Fact]
    public void test_failed_read_writes_nothing()
    {
        var graph = new RecordingGraph();
        Assert.Throws<LeaveError>(() => Runner.SyncOnce(c, graph, WriteDoor.Live(graph, "Europe/London"),
            (_, _, _) => throw new LeaveError("People First answered 500 to /hrm/people/P2/events"), Today, Quiet));
        Assert.Empty(graph.Calls);
    }

    [Fact]
    public void test_safety_stop_in_a_run_writes_nothing()
    {
        var a = Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31");
        var graph = new RecordingGraph();
        var item = Recorded.Item(c, Owned(c, a, "E1"));
        graph.Answers[Calendar.OwnedUrl()] = [item];
        Assert.Throws<SafetyStop>(() => Runner.SyncOnce(c, graph, WriteDoor.Live(graph, "Europe/London"), (_, _, _) => ([Jordan], []), Today, Quiet));
        Assert.All(graph.Calls, call => Assert.StartsWith("GET ", call));
    }
}

/// <summary>Hours gating, failure counting and notifications, through Runner.Run with fakes.</summary>
public sealed class RunTest
{
    private readonly Config c = Config();
    private readonly List<string> alerts = [];
    private readonly RecordingGraph graph = new();

    private JsonObject? Go(LeaveReader read, bool scheduled = true, bool dryRun = false, DateTime? now = null) =>
        Runner.Run(c, graph, dryRun ? WriteDoor.Refusing("Europe/London") : WriteDoor.Live(graph, "Europe/London"), read, Quiet,
            dryRun: dryRun, scheduled: scheduled, now: () => now ?? new DateTime(2026, 11, 12, 10, 0, 0), today: Today,
            alert: (title, _) => alerts.Add(title));

    [Fact]
    public void test_hours()
    {
        Assert.True(Runner.InHours(c, new DateTime(2026, 11, 12, 7, 0, 0)));
        Assert.False(Runner.InHours(c, new DateTime(2026, 11, 12, 6, 59, 0)));
        Assert.False(Runner.InHours(c, new DateTime(2026, 11, 12, 19, 0, 0)));
        Assert.False(Runner.InHours(c, new DateTime(2026, 11, 14, 10, 0, 0)));   // a Saturday
        c.Weekdays = false;
        Assert.True(Runner.InHours(c, new DateTime(2026, 11, 14, 10, 0, 0)));
    }

    [Fact]
    public void test_outside_hours_does_nothing()
    {
        var called = new List<int>();
        Assert.Null(Go((_, _, _) =>
        {
            called.Add(1);
            return ([], []);
        }, now: new DateTime(2026, 11, 12, 6, 0, 0)));
        Assert.Empty(called);
    }

    [Fact]
    public void test_failures_notify_on_the_third_and_once()
    {
        for (var i = 0; i < 5; i++)
            Assert.Throws<LeaveError>(() => Go((_, _, _) => throw new LeaveError("People First answered 503 to /hrm/people/P1/events")));
        Assert.Single(alerts);
        Assert.Equal(5, Py.Int(Runner.LoadState(c)["failures"]));
        Go((_, _, _) => ([Jordan], []));   // a success clears it
        Assert.Equal(0, Py.Int(Runner.LoadState(c)["failures"]));
    }

    [Fact]
    public void test_sign_in_notifies_at_once()
    {
        Assert.Throws<PeopleFirstClient.SignInNeededException>(() =>
            Go((_, _, _) => throw new PeopleFirstClient.SignInNeededException("People First's sign-in wants a password.")));
        Assert.Equal(["calendar-team-leave-sync needs a sign-in"], alerts);
    }

    [Fact]
    public void test_state_holds_no_names_or_dates()
    {
        var a = Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31");
        Go((_, _, _) => ([Jordan], [a]), scheduled: false);
        var text = File.ReadAllText(c.StateFile);
        foreach (var secret in new[] { "Jordan", "2026-12-24", "Holiday" })
            Assert.DoesNotContain(secret, text);
        Assert.Single(graph.Calls, call => call.StartsWith("POST ", StringComparison.Ordinal));
        Assert.False(File.Exists(c.LogFile));   // the test's log function writes nowhere; see test_log_is_names_free
    }

    [Fact]
    public void test_log_is_names_free()
    {
        var lines = new List<string>();
        var a = Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31");
        Runner.Run(c, graph, WriteDoor.Live(graph, "Europe/London"), (_, _, _) => ([Jordan], [a]), lines.Add, today: Today, alert: (_, _) => { });
        var joined = string.Join("\n", lines);
        foreach (var secret in new[] { "Jordan", "24/12", "2026-12", "Holiday" })
            Assert.DoesNotContain(secret, joined);
    }

    [Fact]
    public void test_dry_run_writes_nothing()
    {
        var a = Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31");
        var r = Go((_, _, _) => ([Jordan], [a]), scheduled: false, dryRun: true)!;
        Assert.All(graph.Calls, call => Assert.StartsWith("GET ", call));
        Assert.Equal("Jordan Lee annual leave 24/12 to 31/12", Py.Str(r["added"]![0]!["text"]));
        Assert.False(File.Exists(c.StateFile));
    }
}

/// <summary>The Graph side: reading entries back, against a fake Graph.</summary>
public sealed class CalendarTest
{
    [Fact]
    public void test_entry_dates_and_properties()
    {
        var item = new JsonObject
        {
            ["id"] = "X",
            ["subject"] = "Jordan Lee annual leave",
            ["isAllDay"] = true,
            ["isOrganizer"] = true,
            ["start"] = new JsonObject { ["dateTime"] = "2026-12-24T00:00:00.0000000" },
            ["end"] = new JsonObject { ["dateTime"] = "2027-01-01T00:00:00.0000000" },
            ["singleValueExtendedProperties"] = new JsonArray(
                new JsonObject { ["id"] = Calendar.AbsenceProp, ["value"] = "A1" },
                new JsonObject { ["id"] = Calendar.HashProp, ["value"] = "abc" }),
        };
        var e = Calendar.Parse(item);
        Assert.Equal((D("2026-12-24"), D("2026-12-31"), "A1", "abc"), (e.Start, e.End, e.Absence, e.Hash));
    }

    [Fact]
    public void test_owned_query_and_create()
    {
        var c = Config();
        var g = new RecordingGraph();
        var calendar = new Calendar(g, WriteDoor.Live(g, c.Event.TimeZone).Writer, c.Event.TimeZone);
        Assert.Empty(calendar.Owned());
        Assert.Contains("singleValueExtendedProperties/Any", g.Calls[0]);
        Assert.EndsWith("Prefer=outlook.timezone=\"Europe/London\"", g.Calls[0]);
        calendar.Create(new JsonObject { ["subject"] = "x" });
        Assert.Contains("\"attendees\": []", g.Calls[1]);
    }

    [Fact]
    public void test_hand_pattern()
    {
        var p = Calendar.HandPattern("Jordan Lee");
        foreach (var ok in new[] { "Jordan Lee annual leave", "jordan lee on leave", "Jordan Lee annual leave (AM)", "Jordan Lee annual leave PM",
                     "Jordan Lee annual leave (from PM)", "Jordan Lee on leave (to AM)", "Jordan Lee annual leave (from PM) (to AM)" })
            Assert.True(p.IsMatch(ok), ok);
        foreach (var bad in new[] { "Jordan Leeds annual leave", "Jordan Lee annual leave - skiing", "Re: Jordan Lee annual leave", "Jordan Lee sick" })
            Assert.False(p.IsMatch(bad), bad);
    }

    [Fact]
    public void test_reminder_minutes()
    {
        var c = Config(Settings + "[event]\nreminder = 1080\nsensitivity = 'private'\n");
        var b = Calendar.Body(c, Absence(c, "A1", Jordan, "2026-12-24", "2026-12-24"));
        Assert.Equal((true, 1080L, "private"), (b["isReminderOn"]!.GetValue<bool>(), Py.Int(b["reminderMinutesBeforeStart"]), Py.Str(b["sensitivity"])));
    }
}

/// <summary>Graph items for entries, as Graph returns them.</summary>
public static class Recorded
{
    public static JsonObject Item(Config c, Entry e) => new()
    {
        ["id"] = e.Id,
        ["subject"] = e.Subject,
        ["isAllDay"] = e.AllDay,
        ["isOrganizer"] = e.Organiser,
        ["start"] = new JsonObject { ["dateTime"] = $"{PyTime.Iso(e.Start)}T00:00:00.0000000", ["timeZone"] = c.Event.TimeZone },
        ["end"] = new JsonObject { ["dateTime"] = $"{PyTime.Iso(e.End.AddDays(1))}T00:00:00.0000000", ["timeZone"] = c.Event.TimeZone },
        ["singleValueExtendedProperties"] = new JsonArray(
            new JsonObject { ["id"] = Calendar.AbsenceProp, ["value"] = e.Absence },
            new JsonObject { ["id"] = Calendar.HashProp, ["value"] = e.Hash }),
    };
}
