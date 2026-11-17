using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PyCompat;
using static TeamLeave.Tests.Fixtures;

namespace TeamLeave.Tests;

/// <summary>People First for the approvals tests: the team, each person's listing (which an approval changes), each
/// absence's history, the approval template, and every call recorded. A POST approves the absence unless OnPost says
/// otherwise.</summary>
public sealed partial class FakePeopleFirst : IPeopleFirstApi, IDisposable
{
    public const string CreateHref = "/hrm/absences/approved?absence=2&absenceapproval=1";

    public readonly List<Person> People = [Jordan, Robin];
    public readonly Dictionary<string, List<JsonObject>> Listings = new(StringComparer.Ordinal);
    public readonly Dictionary<string, JsonNode?> Histories = new(StringComparer.Ordinal);
    public readonly List<string> Gets = [];
    public readonly List<string> EventReads = [];
    public readonly List<(string Path, JsonObject Body)> Posts = [];
    public Func<string, JsonObject, JsonNode?>? OnPost;
    public JsonObject Template = TemplateBody();
    public bool Disposed;

    public static JsonObject TemplateBody() => new()
    {
        ["meta"] = new JsonObject
        {
            ["links"] = new JsonObject
            {
                ["self"] = new JsonObject { ["href"] = "/hrm/absences/x/approved/resourcetemplate" },
                ["create"] = new JsonObject { ["href"] = CreateHref },
                ["attachments"] = new JsonObject(),
            },
        },
        ["data"] = new JsonObject { ["absences"] = new JsonObject { ["_links"] = new JsonObject(), ["id"] = "00000000-0000-0000-0000-000000000000" } },
    };

    public static JsonObject History(params string[] kinds) => new()
    {
        ["data"] = new JsonObject
        {
            ["absenceChanges"] = new JsonArray(kinds.Select(k => (JsonNode?)new JsonObject { ["changeType"] = k, ["dateTime"] = "2026-11-01T09:00:00" }).ToArray()),
        },
    };

    public FakePeopleFirst Add(Person person, JsonObject e)
    {
        if (!Listings.TryGetValue(person.Id, out var list))
            Listings[person.Id] = list = [];
        list.Add(e);
        return this;
    }

    public JsonObject Find(string id) => Listings.Values.SelectMany(l => l).First(e => Py.Str(e["eventId"]) == id);

    public string Me() => "ME";

    public IReadOnlyList<JsonNode?> TeamMembers(string person) => person != "ME" ? [] :
        People.Select(p => (JsonNode?)new JsonObject { ["personId"] = p.Id, ["firstName"] = p.Name.Split(' ')[0], ["lastName"] = p.Name.Split(' ')[1] }).ToList();

    public JsonNode? Events(string person, DateOnly start, DateOnly end)
    {
        EventReads.Add(person);
        return Listing(Listings.GetValueOrDefault(person, []).Select(e => (JsonObject)e.DeepClone()).ToArray());
    }

    [GeneratedRegex(@"\A/hrm/absences/([^/]+)/(absenceChanges|approved/resourcetemplate)\z")]
    private static partial Regex Paths();

    public JsonNode? Get(string path)
    {
        Gets.Add(path);
        var match = Paths().Match(path);
        if (!match.Success)
            throw new PeopleFirstClient.PeopleFirstException($"People First answered 404 to {path}") { Status = 404 };
        if (match.Groups[2].Value == "absenceChanges")
            return Histories.TryGetValue(match.Groups[1].Value, out var history) ? history?.DeepClone() : History("creation");
        return Template.DeepClone();
    }

    public JsonNode? Post(string path, JsonObject body)
    {
        Posts.Add((path, (JsonObject)body.DeepClone()));
        return OnPost is not null ? OnPost(path, body) : Approve(body);
    }

    /// <summary>What People First does with a good approval: the absence becomes Approved.</summary>
    public JsonNode? Approve(JsonObject body)
    {
        var id = Py.Str(body["absences"]!["id"]);
        Find(id)["state"] = "Approved";
        return new JsonObject { ["data"] = new JsonObject { ["absences"] = new JsonObject { ["id"] = id } } };
    }

    public void Dispose() => Disposed = true;
}

/// <summary>[approvals] settings: off without the section, defaults, and strict checks.</summary>
public sealed class ApprovalSettingsTests
{
    [Fact]
    public void no_section_is_off() => Assert.Null(Config().Approvals);

    [Fact]
    public void defaults()
    {
        var a = Config(Settings + "[approvals]\n").Approvals!;
        Assert.Equal((false, 5, 1, 10, 7, 2, true), (a.Act, a.MaxPerRun, a.MinIn, a.MaxWorkingDays, a.NoticeDays, a.ShortNoticeOkDays, a.Notify));
        Assert.Equal(["Holiday"], a.Types);
        Assert.Empty(a.Protected);
    }

    [Fact]
    public void every_setting()
    {
        var a = Config(Settings + """
            [approvals]
            act = true
            max_per_run = 3
            types = ["Holiday", "My Birthday"]
            min_in = 2
            max_working_days = 5
            notice_days = 14
            short_notice_ok_days = 1
            protected = [ { start = "2027-02-08", end = "2027-02-09", why = ".UK cutover" }, { start = 2027-03-01, end = 2027-03-01 } ]
            notify = false

            """).Approvals!;
        Assert.Equal((true, 3, 2, 5, 14, 1, false), (a.Act, a.MaxPerRun, a.MinIn, a.MaxWorkingDays, a.NoticeDays, a.ShortNoticeOkDays, a.Notify));
        Assert.Equal(["Holiday", "My Birthday"], a.Types);
        Assert.Equal([new ProtectedDates(D("2027-02-08"), D("2027-02-09"), ".UK cutover"), new ProtectedDates(D("2027-03-01"), D("2027-03-01"), "")],
            a.Protected);
    }

    [Fact]
    public void protected_as_a_table_array()
    {
        var a = Config(Settings + "[approvals]\n[[approvals.protected]]\nstart = \"2027-02-08\"\nend = \"2027-02-09\"\nwhy = \"cutover\"\n").Approvals!;
        Assert.Equal([new ProtectedDates(D("2027-02-08"), D("2027-02-09"), "cutover")], a.Protected);
    }

    [Theory]
    [InlineData("act = \"false\"", "act")]
    [InlineData("act = 0", "act")]
    [InlineData("notify = \"yes\"", "notify")]
    [InlineData("max_per_run = 0", "max_per_run")]
    [InlineData("max_per_run = 1.5", "max_per_run")]
    [InlineData("max_per_run = \"5\"", "max_per_run")]
    [InlineData("min_in = -1", "min_in")]
    [InlineData("max_working_days = 0", "max_working_days")]
    [InlineData("notice_days = -1", "notice_days")]
    [InlineData("types = []", "types")]
    [InlineData("types = \"Holiday\"", "types")]
    [InlineData("types = [\"\"]", "types")]
    [InlineData("max_per_runs = 5", "max_per_runs")]
    [InlineData("protected = \"2027-02-08\"", "protected")]
    [InlineData("protected = [ { start = \"2027-02-09\", end = \"2027-02-08\" } ]", "ends before it starts")]
    [InlineData("protected = [ { start = \"8 Feb\", end = \"2027-02-08\" } ]", "start")]
    [InlineData("protected = [ { start = \"2027-02-08\" } ]", "end")]
    [InlineData("protected = [ { start = \"2027-02-08\", end = \"2027-02-08\", reason = \"x\" } ]", "reason")]
    public void bad_settings_are_errors_that_say_which(string line, string named)
    {
        var error = Assert.Throws<PyValueError>(() => Config(Settings + "[approvals]\n" + line + "\n"));
        Assert.Contains(named, error.Message);
    }

    [Fact]
    public void approvals_must_be_a_table()
    {
        var error = Assert.Throws<PyValueError>(() => Config("approvals = 1\n" + Settings));
        Assert.Contains("[approvals]", error.Message);
    }
}

/// <summary>The rules, as a pure function. Today is Thu 12 Nov 2026; Jordan asks, Robin is the rest of the team.</summary>
public sealed class DecideTests
{
    private static readonly ApprovalSettings Rules = new() { Types = ["Holiday", "My Birthday"] };
    private static readonly DateOnly WindowEnd = Today.AddDays(365);

    private static LeaveRequest Request(string start, string end, string sp = "FULL", string ep = "FULL", string type = "Holiday", Person? person = null) =>
        new("A1", person ?? Jordan, type, D(start), D(end), sp, ep);

    private static Decision Decide(LeaveRequest request, IReadOnlyList<Away>? away = null, bool? changes = false, ApprovalSettings? rules = null,
        IReadOnlyList<Person>? team = null) =>
        Approvals.Decide(request, team ?? [Jordan, Robin], away ?? [new Away(Jordan.Id, request.Start, request.End)], changes, Today, WindowEnd, rules ?? Rules);

    [Fact]
    public void a_routine_request_is_approved_with_its_cover()
    {
        var d = Decide(Request("2026-12-01", "2026-12-02"));   // Tue and Wed, 19 days' notice
        Assert.True(d.Approve, string.Join("; ", d.Reasons));
        Assert.Empty(d.Reasons);
        Assert.Equal(2, d.WorkingDays);
        Assert.Equal([(D("2026-12-01"), "Robin Hall"), (D("2026-12-02"), "Robin Hall")], d.Cover.Select(c => (c.Day, string.Join(",", c.In))));
    }

    [Fact]
    public void a_type_the_rules_dont_approve_is_asked_without_naming_it()
    {
        var d = Decide(Request("2026-12-01", "2026-12-01", type: "Compassionate"));
        Assert.False(d.Approve);
        Assert.Equal(["it isn't a type the rules approve"], d.Reasons);
    }

    [Fact]
    public void a_change_to_approved_leave_is_asked()
    {
        Assert.Equal(["it changes leave that was already approved"], Decide(Request("2026-12-01", "2026-12-01"), changes: true).Reasons);
        Assert.Equal(["its history couldn't be read, so it may change leave already approved"], Decide(Request("2026-12-01", "2026-12-01"), changes: null).Reasons);
    }

    [Fact]
    public void leaving_nobody_in_is_asked_with_the_days()
    {
        var r = Request("2026-12-01", "2026-12-03");
        var d = Decide(r, [new Away(Jordan.Id, r.Start, r.End), new Away(Robin.Id, D("2026-12-02"), D("2026-12-03"))]);
        Assert.False(d.Approve);
        Assert.Equal(["it leaves nobody in on 02/12, 03/12"], d.Reasons);
        Assert.Equal(["Robin Hall", "", ""], d.Cover.Select(c => string.Join(",", c.In)));
    }

    [Fact]
    public void the_requester_is_never_counted_in()
    {
        var d = Decide(Request("2026-12-01", "2026-12-01"), away: [], team: [Jordan]);
        Assert.Equal(["it leaves nobody in on 01/12"], d.Reasons);
    }

    [Fact]
    public void weekends_need_no_cover_and_arent_working_days()
    {
        var r = Request("2026-12-04", "2026-12-07");   // Fri to Mon
        var d = Decide(r, [new Away(Jordan.Id, r.Start, r.End), new Away(Robin.Id, D("2026-12-05"), D("2026-12-06"))]);
        Assert.True(d.Approve);
        Assert.Equal(2, d.WorkingDays);
        Assert.Equal([D("2026-12-04"), D("2026-12-07")], d.Cover.Select(c => c.Day));
    }

    [Fact]
    public void min_in_counts_people()
    {
        var sam = new Person("P3", "Sam Ray");
        var r = Request("2026-12-01", "2026-12-01");
        var rules = new ApprovalSettings { Types = ["Holiday"], MinIn = 2 };
        Assert.True(Decide(r, rules: rules, team: [Jordan, Robin, sam]).Approve);
        var d = Decide(r, [new Away(Jordan.Id, r.Start, r.End), new Away(sam.Id, r.Start, r.End)], rules: rules, team: [Jordan, Robin, sam]);
        Assert.Equal(["it leaves fewer than 2 in on 01/12"], d.Reasons);
        Assert.True(Decide(r, away: [new Away(Robin.Id, r.Start, r.End)], rules: new ApprovalSettings { Types = ["Holiday"], MinIn = 0 }).Approve);
    }

    [Theory]
    [InlineData("2026-11-30", "2026-12-11", true)]    // 10 working days
    [InlineData("2026-11-30", "2026-12-14", false)]   // 11
    public void max_working_days(string start, string end, bool approve)
    {
        var d = Decide(Request(start, end));
        Assert.Equal(approve, d.Approve);
        if (!approve)
            Assert.Equal(["it's 11 working days, more than 10"], d.Reasons);
    }

    [Theory]
    [InlineData("2026-11-19", "2026-11-23", true)]    // 7 days' notice for 3 working days: enough
    [InlineData("2026-11-18", "2026-11-20", false)]   // 6 days' notice for 3 working days
    [InlineData("2026-11-16", "2026-11-17", true)]    // 4 days' notice, but only 2 working days (GJ: "1-2 days ... fine")
    [InlineData("2026-11-13", "2026-11-13", true)]    // tomorrow, 1 day
    [InlineData("2026-11-12", "2026-11-12", true)]    // today, 1 day
    [InlineData("2026-11-13", "2026-11-16", true)]    // Fri and Mon: 2 working days over a weekend
    [InlineData("2026-11-13", "2026-11-17", false)]   // 3 working days, 1 day's notice
    public void notice_and_the_short_notice_exception(string start, string end, bool approve)
    {
        var d = Decide(Request(start, end));
        Assert.True(approve == d.Approve, string.Join("; ", d.Reasons));
        if (!approve)
            Assert.Contains(d.Reasons, r => r.Contains("notice", StringComparison.Ordinal));
    }

    [Fact]
    public void the_notice_reason_reads_as_gj_would_say_it()
    {
        Assert.Equal(["it's 1 day's notice for 3 working days (less than 7 days' notice is fine only for 2 working days or fewer)"],
            Decide(Request("2026-11-13", "2026-11-17")).Reasons);
    }

    [Fact]
    public void protected_dates_are_asked_with_why()
    {
        var rules = new ApprovalSettings { Types = ["Holiday"], Protected = [new ProtectedDates(D("2026-12-08"), D("2026-12-09"), ".UK cutover")] };
        Assert.Equal(["it overlaps .UK cutover (08/12 to 09/12)"], Decide(Request("2026-12-09", "2026-12-10"), rules: rules).Reasons);
        Assert.True(Decide(Request("2026-12-10", "2026-12-11"), rules: rules).Approve);   // the day after: fine
        var unnamed = new ApprovalSettings { Types = ["Holiday"], Protected = [new ProtectedDates(D("2026-12-08"), D("2026-12-08"), "")] };
        Assert.Equal(["it overlaps protected dates (08/12)"], Decide(Request("2026-12-07", "2026-12-08"), rules: unnamed).Reasons);
    }

    [Fact]
    public void cover_that_cant_be_checked_is_asked()
    {
        Assert.Contains("it started before today, so its cover can't all be checked", Decide(Request("2026-11-11", "2026-11-12")).Reasons);
        var late = Today.AddDays(365);
        Assert.Contains("it runs past days_ahead, so its cover can't all be checked",
            Decide(Request(PyTime.Iso(late), PyTime.Iso(late.AddDays(1)))).Reasons);
    }

    [Fact]
    public void every_reason_that_applies_is_given()
    {
        var rules = new ApprovalSettings { Types = ["Holiday"], Protected = [new ProtectedDates(D("2026-11-16"), D("2026-11-16"), "cutover")] };
        var r = Request("2026-11-16", "2026-11-30", type: "Other");
        var d = Decide(r, [new Away(Jordan.Id, r.Start, r.End), new Away(Robin.Id, D("2026-11-16"), D("2026-11-16"))], changes: true, rules: rules);
        Assert.Equal(6, d.Reasons.Count);
    }

    [Theory]
    [InlineData(new[] { "creation" }, false)]
    [InlineData(new[] { "creation", "approval" }, true)]
    [InlineData(new[] { "creationAndAutoApproval" }, true)]
    [InlineData(new[] { "creation", "modification" }, true)]
    [InlineData(new string[0], null)]
    public void history(string[] kinds, bool? changes) => Assert.Equal(changes, Approvals.ChangesApproved(FakePeopleFirst.History(kinds)));

    [Fact]
    public void history_of_another_shape_is_unknown()
    {
        Assert.Null(Approvals.ChangesApproved(null));
        Assert.Null(Approvals.ChangesApproved(new JsonObject { ["data"] = new JsonObject { ["items"] = new JsonArray() } }));
        Assert.True(Approvals.ChangesApproved(new JsonArray(new JsonObject { ["changeType"] = "Approval" })));
    }
}

/// <summary>Reading for approvals: the same listings, a little more kept in memory, nothing more called for.</summary>
public sealed class ApprovalReadTests
{
    private const string Rules = "[approvals]\nact = true\ntypes = [\"Holiday\", \"My Birthday\"]\n";

    [Fact]
    public void without_approvals_read_all_is_read()
    {
        var c = Config();
        var pf = new FakePeopleFirst().Add(Jordan, Event("A1", "2026-12-01", "2026-12-01", state: "AwaitingApproval"))
            .Add(Robin, Event("B1", "2026-12-01", "2026-12-01", "T3", "NotApplicable"));
        var read = Leave.ReadAll(c, pf, Today, Quiet);
        Assert.Empty(read.Requests);
        Assert.Empty(read.Away);
        Assert.Empty(read.ApprovedIds);
        Assert.Equal(["P1", "P2"], pf.EventReads);
        Assert.Empty(pf.Gets);
    }

    [Fact]
    public void requests_away_and_approved_ids()
    {
        var c = Config(Settings + Rules);
        var pf = new FakePeopleFirst()
            .Add(Jordan, Event("A1", "2026-12-01", "2026-12-02", state: "AwaitingApproval", startPart: "HALF_PM"))
            .Add(Jordan, Event("A2", "2026-12-10", "2026-12-10", "T2", "AwaitingApproval"))
            .Add(Jordan, Event("A3", "2026-12-11", "2026-12-11", "T4", "AwaitingApproval"))   // Appointment: away, not a request
            .Add(Robin, Event("B1", "2026-12-01", "2026-12-01", "T3", "NotApplicable"))        // sickness: away
            .Add(Robin, Event("B2", "2026-12-02", "2026-12-02", state: "Rejected"))            // not away
            .Add(Robin, Event("B3", "2026-12-03", "2026-12-03"));                              // approved
        var read = Leave.ReadAll(c, pf, Today, Quiet);
        Assert.Equal([("A1", "Holiday", "HALF_PM"), ("A2", "My Birthday", "FULL")], read.Requests.Select(r => (r.Id, r.Type, r.StartPart)));
        Assert.Equal([("P1", "2026-12-01"), ("P1", "2026-12-10"), ("P1", "2026-12-11"), ("P2", "2026-12-01"), ("P2", "2026-12-03")],
            read.Away.Select(a => (a.PersonId, PyTime.Iso(a.Start))));
        Assert.Equal(["B3"], read.ApprovedIds);
        Assert.Equal(["B3"], read.Absences.Select(a => a.Id));
        Assert.Equal(["P1", "P2"], pf.EventReads);   // no more listing calls than before
    }

    [Fact]
    public void an_approval_type_people_first_doesnt_have_stops_the_read()
    {
        var c = Config(Settings + "[approvals]\ntypes = [\"Holidya\"]\n");
        var error = Assert.Throws<LeaveError>(() => Leave.ReadAll(c, new FakePeopleFirst(), Today, Quiet));
        Assert.Contains("'Holidya'", error.Message);
    }
}

/// <summary>A run with [approvals], end to end through Runner.Run: People First fake, Graph recorded.</summary>
public sealed class ApprovalRunTests
{
    private const string Rules = "[approvals]\nact = true\nmax_per_run = 5\ntypes = [\"Holiday\", \"My Birthday\"]\n";
    private static readonly DateTime Now = new(2026, 11, 12, 10, 0, 0);

    private readonly List<(string Title, List<string> Lines)> alerts = [];
    private readonly List<string> lines = [];

    private JsonObject Go(Config c, FakePeopleFirst pf, IGraph? graph = null, WriteDoor? door = null, bool dryRun = false, HookRunner? hook = null)
    {
        graph ??= new RecordingGraph();
        door ??= dryRun ? WriteDoor.Refusing("Europe/London") : WriteDoor.Live(graph, "Europe/London");
        return Runner.Run(c, graph, door, (_, _, _) => throw new InvalidOperationException("with [approvals], People First is opened, not read"),
            lines.Add, dryRun: dryRun, hook: hook is not null, now: () => Now, today: Today, alert: (t, l) => alerts.Add((t, l.ToList())),
            runner: hook, open: (_, _) => pf)!;
    }

    private static JsonObject Approvals(JsonObject report) => report["approvals"]!.AsObject();

    private static List<string> Ids(JsonObject report, string key) => Approvals(report)[key]!.AsArray().Select(i => Py.Str(i!["id"])).ToList();

    private static JsonObject? Record(Config c, string id) => TeamLeave.Approvals.Records(Runner.LoadState(c))[id] as JsonObject;

    private static FakePeopleFirst Routine(string id = "A1", string start = "2026-12-01", string end = "2026-12-02") =>
        new FakePeopleFirst().Add(Jordan, Event(id, start, end, state: "AwaitingApproval"));

    [Fact]
    public void a_routine_request_is_approved_read_back_and_in_the_calendar_in_the_same_run()
    {
        var c = Config(Settings + Rules);
        var pf = Routine();
        var graph = new RecordingGraph();
        var report = Go(c, pf, graph);
        var (path, body) = Assert.Single(pf.Posts);
        Assert.Equal(FakePeopleFirst.CreateHref + "&annotate=t", path);   // the template's link, with annotate=t, as the web app posts it
        Assert.Equal("""{"absences":{"id":"A1"}}""", body.ToJsonString());   // the old format: no "data" wrapper
        Assert.Equal(["/hrm/absences/A1/absenceChanges", "/hrm/absences/A1/approved/resourcetemplate"], pf.Gets);
        Assert.DoesNotContain(pf.Gets, g => g == "/hrm/absences/A1");   // never the absence itself (it has the reason)
        Assert.Equal(["P1", "P2", "P1"], pf.EventReads);   // the team, then Jordan's read back
        Assert.True(pf.Disposed);
        Assert.Equal(["A1"], Ids(report, "approved"));
        Assert.Single(graph.Calls, call => call.StartsWith("POST /me/events {\"subject\": \"Jordan Lee annual leave\"", StringComparison.Ordinal));
        Assert.Equal("1 added, 0 changed, 0 removed, 0 unchanged", Py.Str(report["summary"]));
        Assert.Equal("approved", Py.Str(Record(c, "A1")!["outcome"]));
        Assert.Empty(alerts);
    }

    [Fact]
    public void the_state_and_log_never_hold_names_or_types()
    {
        var c = Config(Settings + Rules);
        var pf = Routine().Add(Robin, Event("B1", "2026-12-08", "2026-12-08", state: "AwaitingApproval"));
        Go(c, pf);
        var state = File.ReadAllText(c.StateFile);
        foreach (var secret in new[] { "Jordan", "Robin", "Holiday", "annual" })
            Assert.DoesNotContain(secret, state);
        var log = string.Join("\n", lines);
        foreach (var secret in new[] { "Jordan", "Robin", "Holiday", "01/12", "2026-12" })
            Assert.DoesNotContain(secret, log);
        Assert.Contains("approved absence A1", log);
    }

    [Fact]
    public void a_request_that_leaves_nobody_in_is_asked_once_with_a_notification()
    {
        var c = Config(Settings + Rules);
        var pf = Routine().Add(Robin, Event("B1", "2026-12-02", "2026-12-02"));
        var report = Go(c, pf);
        Assert.Empty(pf.Posts);
        var item = Approvals(report)["asked"]![0]!;
        Assert.True(item["new"]!.GetValue<bool>());
        Assert.Equal(["it leaves nobody in on 02/12"], item["reasons"]!.AsArray().Select(r => Py.Str(r)));
        Assert.Equal("[{\"date\":\"2026-12-01\",\"in\":[\"Robin Hall\"]},{\"date\":\"2026-12-02\",\"in\":[]}]", item["cover"]!.ToJsonString());
        var (title, text) = Assert.Single(alerts);
        Assert.Equal("A leave request needs you", title);
        Assert.Equal(["Jordan Lee annual leave 01/12 to 02/12: it leaves nobody in on 02/12"], text);

        report = Go(c, pf);   // asked once: not again
        Assert.Single(alerts);
        Assert.False(Approvals(report)["asked"]![0]!["new"]!.GetValue<bool>());
        Assert.Empty(pf.Posts);

        pf.Find("A1")["endDate"] = "2026-12-03";   // the dates change: it's a new question
        Go(c, pf);
        Assert.Equal(2, alerts.Count);
    }

    [Fact]
    public void once_asked_it_stays_yours_even_if_the_rules_would_now_approve_it()
    {
        var c = Config(Settings + Rules);
        var pf = Routine().Add(Robin, Event("B1", "2026-12-02", "2026-12-02"));
        Go(c, pf);
        pf.Listings[Robin.Id].Clear();   // Robin's leave cancelled: cover is fine now
        var report = Go(c, pf);
        Assert.Empty(pf.Posts);
        Assert.Equal(["the rules would approve it now, but it's already with you"], Approvals(report)["asked"]![0]!["reasons"]!.AsArray().Select(r => Py.Str(r)));
    }

    [Fact]
    public void notify_false_asks_without_a_notification()
    {
        var c = Config(Settings + Rules + "notify = false\n");
        Go(c, Routine().Add(Robin, Event("B1", "2026-12-02", "2026-12-02")));
        Assert.Empty(alerts);
        Assert.Equal("asked", Py.Str(Record(c, "A1")!["outcome"]));
    }

    [Fact]
    public void another_pending_request_counts_as_away_so_both_are_asked()
    {
        var c = Config(Settings + Rules);
        var pf = Routine().Add(Robin, Event("B1", "2026-12-02", "2026-12-02", state: "AwaitingApproval"));
        var report = Go(c, pf);
        Assert.Empty(pf.Posts);
        Assert.Equal(["A1", "B1"], Ids(report, "asked"));
        Assert.Equal("2 leave requests need you", Assert.Single(alerts).Title);
    }

    [Fact]
    public void a_change_to_approved_leave_is_asked()
    {
        var c = Config(Settings + Rules);
        var pf = Routine();
        pf.Histories["A1"] = FakePeopleFirst.History("creation", "approval", "modification");
        var report = Go(c, pf);
        Assert.Empty(pf.Posts);
        Assert.Equal(["it changes leave that was already approved"], Approvals(report)["asked"]![0]!["reasons"]!.AsArray().Select(r => Py.Str(r)));
    }

    [Fact]
    public void a_lost_answer_is_unknown_never_resent_and_resolved_by_the_next_run()
    {
        var c = Config(Settings + Rules);
        var pf = Routine();
        pf.OnPost = (_, body) =>
        {
            pf.Approve(body);   // it did go through...
            throw new PeopleFirstClient.PeopleFirstDeliveryUnknownException("POST /hrm/absences/approved to People First may or may not have happened: it timed out.");
        };
        var report = Go(c, pf);
        Assert.Single(pf.Posts);
        Assert.Equal(["A1"], Ids(report, "unknown"));
        Assert.Equal("unknown", Py.Str(Record(c, "A1")!["outcome"]));
        Assert.Contains("approval of absence A1 may not have gone through; not sent again", lines);

        pf.OnPost = (_, _) => throw new InvalidOperationException("must not be sent again");
        var graph = new RecordingGraph();
        report = Go(c, pf, graph);   // ...and the next run sees it approved
        Assert.Single(pf.Posts);
        var item = Approvals(report)["approved"]![0]!;
        Assert.Equal(("A1", true), (Py.Str(item["id"]), item["confirmed_late"]!.GetValue<bool>()));
        Assert.Null(Record(c, "A1"));   // no longer waiting: no record
        Assert.Single(graph.Calls, call => call.StartsWith("POST /me/events ", StringComparison.Ordinal));
        Assert.Empty(alerts);
    }

    [Fact]
    public void an_unconfirmed_approval_still_waiting_is_raised_never_posted_again()
    {
        var c = Config(Settings + Rules);
        var pf = Routine();
        pf.OnPost = (_, _) => throw new TimeoutException("timed out");   // didn't go through
        Go(c, pf);
        Assert.Single(pf.Posts);
        var report = Go(c, pf);
        Assert.Single(pf.Posts);   // never resent
        var item = Approvals(report)["asked"]![0]!;
        Assert.Equal("an approval was sent on 12/11 at 10:00, but People First still shows it waiting: check it there before approving it again",
            Py.Str(item["reasons"]![0]));
        Assert.Single(alerts);
        Go(c, pf);
        Assert.Single(pf.Posts);
        Assert.Single(alerts);
    }

    [Fact]
    public void sent_but_not_shown_approved_is_unknown()
    {
        var c = Config(Settings + Rules);
        var pf = Routine();
        pf.OnPost = (_, _) => new JsonObject();   // accepted, but the listing still says waiting
        var report = Go(c, pf);
        Assert.Equal(["A1"], Ids(report, "unknown"));
        Assert.Empty(Approvals(report)["approved"]!.AsArray());
        Assert.Equal("0 added, 0 changed, 0 removed, 0 unchanged", Py.Str(report["summary"]));
    }

    [Theory]
    [InlineData(403, false, "People First refused it (403)")]
    [InlineData(null, true, "People First couldn't be reached, so nothing was sent")]
    public void a_refusal_fails_and_is_asked_once(int? status, bool neverSent, string reason)
    {
        var c = Config(Settings + Rules);
        var pf = Routine();
        pf.OnPost = (_, _) => throw new PeopleFirstClient.PeopleFirstException("People First answered 403 to POST /hrm/absences/approved")
            { Status = status, NeverSent = neverSent };
        var report = Go(c, pf);
        Assert.Equal($"approving it failed: {reason}", Py.Str(Approvals(report)["asked"]![0]!["reasons"]![0]));
        Assert.Equal("failed", Py.Str(Record(c, "A1")!["outcome"]));
        Go(c, pf);
        Assert.Single(pf.Posts);   // not tried again
        Assert.Single(alerts);
    }

    [Fact]
    public void a_template_of_another_shape_sends_nothing()
    {
        var c = Config(Settings + Rules);
        var pf = Routine();
        pf.Template["meta"]!["links"]!["create"]!["href"] = "https://elsewhere.example.com/hrm/absences/approved";
        var report = Go(c, pf);
        Assert.Empty(pf.Posts);
        Assert.Equal("approving it failed: People First's approval form has changed shape, so nothing was sent",
            Py.Str(Approvals(report)["asked"]![0]!["reasons"]![0]));
    }

    [Fact]
    public void never_more_than_max_per_run()
    {
        var c = Config(Settings + Rules.Replace("max_per_run = 5", "max_per_run = 2"));
        var pf = new FakePeopleFirst();
        pf.People.Add(new Person("P3", "Sam Ray"));
        pf.Add(Jordan, Event("A1", "2026-12-01", "2026-12-01", state: "AwaitingApproval"))
            .Add(Jordan, Event("A2", "2026-12-08", "2026-12-08", state: "AwaitingApproval"))
            .Add(Robin, Event("B1", "2026-12-15", "2026-12-15", state: "AwaitingApproval"));
        var report = Go(c, pf);
        Assert.Equal(2, pf.Posts.Count);
        Assert.Equal(["A1", "A2"], Ids(report, "approved"));
        Assert.Equal(["B1"], Ids(report, "deferred"));
        Assert.Null(Record(c, "B1"));
        report = Go(c, pf);
        Assert.Equal(["B1"], Ids(report, "approved"));
        Assert.Equal(3, pf.Posts.Count);
    }

    [Fact]
    public void preview_approves_nothing_and_reports_each_once()
    {
        var c = Config(Settings + Rules.Replace("act = true", "act = false"));
        var pf = Routine();
        var report = Go(c, pf);
        Assert.Empty(pf.Posts);
        Assert.DoesNotContain(pf.Gets, g => g.EndsWith("resourcetemplate", StringComparison.Ordinal));
        Assert.Equal(["A1"], Ids(report, "would_approve"));
        Assert.True(Approvals(report)["would_approve"]![0]!["new"]!.GetValue<bool>());
        Assert.Equal("would-approve", Py.Str(Record(c, "A1")!["outcome"]));
        report = Go(c, pf);
        Assert.False(Approvals(report)["would_approve"]![0]!["new"]!.GetValue<bool>());
        Assert.Empty(alerts);
        Assert.Empty(pf.Posts);
    }

    [Fact]
    public void a_dry_run_records_and_notifies_nothing()
    {
        var c = Config(Settings + Rules);
        var pf = Routine().Add(Robin, Event("B1", "2026-12-08", "2026-12-08", state: "AwaitingApproval")).Add(Jordan, Event("A9", "2026-12-08", "2026-12-08"));
        var report = Go(c, pf, dryRun: true);
        Assert.Empty(pf.Posts);
        Assert.Equal(["A1"], Ids(report, "would_approve"));
        Assert.Equal(["B1"], Ids(report, "asked"));
        Assert.False(File.Exists(c.StateFile));
        Assert.Empty(alerts);
    }

    [Fact]
    public void journal_mode_records_the_post_and_sends_nothing()
    {
        var c = Config(Settings + Rules);
        var pf = Routine();
        var door = WriteDoor.Journaling("Europe/London");
        var report = Go(c, pf, door: door);
        Assert.Empty(pf.Posts);
        var call = Assert.Single(door.Journal, w => w.Url.StartsWith(WriteDoor.PeopleFirstPrefix, StringComparison.Ordinal));
        Assert.StartsWith("POST people-first:/hrm/absences/approved?absence=2&absenceapproval=1&annotate=t {", call.Text);
        Assert.True(Approvals(report)["would_approve"]![0]!["journal"]!.GetValue<bool>());
        Assert.Null(Record(c, "A1"));
        Assert.Empty(alerts);
    }

    [Fact]
    public void refuse_mode_fails_a_run_that_would_approve_and_sends_nothing()
    {
        var c = Config(Settings + Rules);
        var pf = Routine();
        var error = Assert.Throws<WriteRefusedException>(() => Go(c, pf, door: WriteDoor.Refusing("Europe/London")));
        Assert.Contains("people-first:/hrm/absences/approved", error.Message);
        Assert.Empty(pf.Posts);
    }

    /// <summary>A Graph whose calendar writes fail.</summary>
    private sealed class FailingWrites(RecordingGraph inner) : IGraph
    {
        public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string> scopes, IReadOnlyDictionary<string, string> headers) => inner.Pages(url, scopes, headers);

        public JsonObject Send(string method, string url, JsonObject? body, IReadOnlyList<string> scopes, IReadOnlyDictionary<string, string> headers) =>
            throw new M365Graph.GraphRequestException(System.Net.HttpStatusCode.ServiceUnavailable, method, url, "");

        public void Token()
        {
        }
    }

    [Fact]
    public void an_approval_is_recorded_even_when_the_calendar_then_fails()
    {
        var c = Config(Settings + Rules);
        var pf = Routine();
        var graph = new FailingWrites(new RecordingGraph());
        Assert.Throws<M365Graph.GraphRequestException>(() => Go(c, pf, graph, WriteDoor.Live(graph, "Europe/London")));
        Assert.Equal("approved", Py.Str(Record(c, "A1")!["outcome"]));
        Assert.Equal(1, Py.Int(Runner.LoadState(c)["failures"]));
        Go(c, pf);   // approved now, so not waiting: nothing posted again, and the calendar catches up
        Assert.Single(pf.Posts);
    }

    [Fact]
    public void the_hook_hears_of_a_new_ask_even_with_no_calendar_change()
    {
        var c = Config(Settings.Replace("max_deletes = 3", "max_deletes = 3\nafter_run = [\"python\", \"hook.py\"]") + Rules);
        var hooks = new List<string>();
        HookRunner hook = (_, input, _, _) =>
        {
            hooks.Add(input);
            return new WinBackground.Completed(0, "", "");
        };
        var pf = Routine().Add(Robin, Event("B1", "2026-12-02", "2026-12-02", "T3", "NotApplicable"));   // off sick: away, not in the calendar
        Go(c, pf, hook: hook);
        var report = PyJson.Parse(Assert.Single(hooks))!;
        Assert.Equal("0 added, 0 changed, 0 removed, 0 unchanged", Py.Str(report["summary"]));
        var asked = report["approvals"]!["asked"]![0]!;
        Assert.Equal(("A1", "Jordan Lee", true), (Py.Str(asked["id"]), Py.Str(asked["name"]), asked["new"]!.GetValue<bool>()));
        Assert.Equal("1 with you (1 new)", Py.Str(report["approvals"]!["summary"]));
        Go(c, pf, hook: hook);   // nothing new: no hook
        Assert.Single(hooks);
    }

    [Fact]
    public void records_go_once_a_request_isnt_waiting()
    {
        var c = Config(Settings + Rules);
        var pf = Routine().Add(Robin, Event("B1", "2026-12-02", "2026-12-02"));
        Go(c, pf);
        Assert.NotNull(Record(c, "A1"));
        pf.Find("A1")["state"] = "Approved";   // GJ approved it in People First
        var report = Go(c, pf);
        Assert.Null(Record(c, "A1"));
        Assert.Equal("no leave requests waiting", Py.Str(Approvals(report)["summary"]));
    }

    [Fact]
    public void turning_approvals_off_drops_the_records()
    {
        var with = Config(Settings + Rules);
        Go(with, Routine().Add(Robin, Event("B1", "2026-12-02", "2026-12-02")));
        Assert.NotNull(Record(with, "A1"));
        File.WriteAllText(with.Path, Settings);
        var without = TeamLeave.Config.Load(with.Path);
        without.StateFile = with.StateFile;
        without.LogFile = with.LogFile;
        Runner.Run(without, new RecordingGraph(), WriteDoor.Live(new RecordingGraph(), "Europe/London"), (_, _, _) => ([Jordan], []), Quiet,
            today: Today, alert: (_, _) => { });
        Assert.False(Runner.LoadState(without).ContainsKey("approvals"));
    }
}

/// <summary>The People First side of the write door, and the fence.</summary>
public sealed class PeopleFirstDoorTests
{
    private static JsonObject Body() => new() { ["absences"] = new JsonObject { ["id"] = "A1" } };

    [Fact]
    public void live_posts_through_the_api_it_is_given()
    {
        var pf = Routine();
        WriteDoor.Live(new RecordingGraph(), "Europe/London").PeopleFirst(pf).Post("/hrm/absences/approved", Body());
        Assert.Single(pf.Posts);
    }

    private static FakePeopleFirst Routine() => new FakePeopleFirst().Add(Jordan, Event("A1", "2026-12-01", "2026-12-01", state: "AwaitingApproval"));

    [Fact]
    public void journal_records_and_sends_nothing()
    {
        var pf = Routine();
        var seen = new List<string>();
        var door = WriteDoor.Journaling("Europe/London", call => seen.Add(call.Text));
        Assert.Null(door.PeopleFirst(pf).Post("/hrm/absences/approved?absence=2", Body()));
        Assert.Empty(pf.Posts);
        Assert.Equal(["POST people-first:/hrm/absences/approved?absence=2 {\"absences\": {\"id\": \"A1\"}}"], seen);
    }

    [Fact]
    public void refuse_throws_and_sends_nothing()
    {
        var pf = Routine();
        Assert.Throws<WriteRefusedException>(() => WriteDoor.Refusing("Europe/London").PeopleFirst(pf).Post("/hrm/absences/approved", Body()));
        Assert.Empty(pf.Posts);
    }

    [Theory]
    [InlineData(WriteMode.Live)]
    [InlineData(WriteMode.Journal)]
    [InlineData(WriteMode.Refuse)]
    public void a_people_first_only_door_never_writes_to_the_calendar(WriteMode mode)
    {
        var door = WriteDoor.PeopleFirstOnly(mode, "Europe/London");
        Assert.Equal(mode, door.Mode);
        Assert.Throws<WriteRefusedException>(() => door.Writer.Create(new JsonObject()));
        Assert.Throws<WriteRefusedException>(() => door.Writer.Delete("e1"));
    }

    [Fact]
    public void the_fence_passes_reads_and_refuses_the_post()
    {
        var pf = Routine();
        var fence = new ReadOnlyPeopleFirst(pf);
        Assert.Equal("ME", fence.Me());
        fence.Get("/hrm/absences/A1/absenceChanges");
        Assert.Throws<WriteRefusedException>(() => fence.Post("/hrm/absences/approved", Body()));
        Assert.Empty(pf.Posts);
        Assert.Single(pf.Gets);
    }
}

/// <summary>The approvals and approve commands, and approvals in sync and status output.</summary>
public sealed class ApprovalCliTests
{
    private const string Rules = "[approvals]\nact = true\ntypes = [\"Holiday\", \"My Birthday\"]\n";

    private static (int Code, string Out, string Err, RecordingGraph Graph) Cli(Config c, FakePeopleFirst pf, params string[] argv)
    {
        var graph = new RecordingGraph();
        var output = new StringWriter();
        var error = new StringWriter();
        var code = TeamLeave.Cli.Run(["--config", c.Path, .. argv], new CliHost
        {
            Out = output,
            Err = error,
            Graph = (_, _) => graph,
            ReadPeopleFirst = (_, _, _) => throw new InvalidOperationException("with [approvals], People First is opened, not read"),
            OpenPeopleFirst = (_, _) => pf,
            Notify = (_, _) => { },
            Now = () => new DateTime(2026, 11, 12, 10, 0, 0),
        });
        return (code, output.ToString(), error.ToString(), graph);
    }

    /// <summary>Settings with the state and log beside them (Config() sets them only on the object, not the file).</summary>
    private static Config Settled(string extra = Rules)
    {
        var c = Config(Settings.Replace("max_deletes = 3", "max_deletes = 3\nstate_file = \"state.json\"\nlog_file = \"log.txt\"") + extra);
        return TeamLeave.Config.Load(c.Path);
    }

    private static FakePeopleFirst Team() => new FakePeopleFirst()
        .Add(Jordan, Event("A1", "2026-12-01", "2026-12-02", state: "AwaitingApproval"))
        .Add(Jordan, Event("A2", "2026-12-24", "2026-12-24"))
        .Add(Jordan, Event("A3", "2026-12-28", "2026-12-28", "T4", "AwaitingApproval"))
        .Add(Robin, Event("B1", "2026-12-08", "2026-12-08", state: "AwaitingApproval"))
        .Add(Jordan, Event("A4", "2026-12-08", "2026-12-08"));

    [Fact]
    public void approvals_previews_and_writes_nothing()
    {
        var c = Settled();
        var pf = Team();
        var (code, output, _, graph) = Cli(c, pf, "approvals");
        Assert.Equal(0, code);
        Assert.Contains("  Would approve: Jordan Lee annual leave 01/12 to 02/12  [A1]", output);
        Assert.Contains("      Tue 01/12 in: Robin Hall", output);
        Assert.Contains("  Needs you: Robin Hall annual leave 08/12  [B1]", output);
        Assert.Contains("      - it leaves nobody in on 08/12", output);
        Assert.Contains("      Tue 08/12 in: nobody", output);
        Assert.Contains("  Approvals: 1 would be approved, 1 with you (1 new)", output);
        Assert.DoesNotContain("A3", output);   // an Appointment: not a type the rules look at
        Assert.Empty(pf.Posts);
        Assert.Empty(graph.Calls);
        Assert.False(File.Exists(c.StateFile));
        Assert.False(File.Exists(c.LogFile));
        Assert.True(pf.Disposed);
    }

    [Fact]
    public void approvals_json()
    {
        var (code, output, _, _) = Cli(Settled(), Team(), "approvals", "--json");
        Assert.Equal(0, code);
        var report = PyJson.Parse(output)!;
        Assert.Equal("A1", Py.Str(report["would_approve"]![0]!["id"]));
    }

    [Fact]
    public void approvals_needs_the_section()
    {
        var (code, _, error, _) = Cli(Settled(""), Team(), "approvals");
        Assert.Equal(2, code);
        Assert.Contains("There's no [approvals] section", error);
    }

    [Fact]
    public void approvals_says_when_people_first_cant_be_read()
    {
        var (code, _, error, _) = Cli(Settled("[approvals]\ntypes = [\"Holidya\"]\n"), Team(), "approvals");
        Assert.Equal(2, code);
        Assert.Contains("People First has no absence type called 'Holidya'", error);
    }

    [Fact]
    public void approve_by_hand_whatever_the_rules_say()
    {
        var c = Settled();
        var pf = Team();
        var (code, output, _, graph) = Cli(c, pf, "approve", "B1");   // the rules would ask: nobody else in
        Assert.Equal(0, code);
        Assert.Contains("Approved: Robin Hall annual leave 08/12. The next sync puts it in your calendar.", output);
        Assert.Equal("B1", Py.Str(Assert.Single(pf.Posts).Body["absences"]!["id"]));
        Assert.Equal("Approved", Py.Str(pf.Find("B1")["state"]));
        Assert.Empty(graph.Calls);
        var record = TeamLeave.Approvals.Records(Runner.LoadState(c))["B1"]!;
        Assert.Equal(("approved", "hand"), (Py.Str(record["outcome"]), Py.Str(record["by"])));
    }

    [Fact]
    public void approve_by_hand_takes_any_type()
    {
        var pf = Team();
        var (code, output, _, _) = Cli(Settled(), pf, "approve", "A3");
        Assert.Equal(0, code);
        Assert.Contains("Approved: Jordan Lee leave 28/12.", output);   // a type not in [types]: never named
    }

    [Theory]
    [InlineData("A2", "isn't waiting for approval (People First says 'Approved')")]
    [InlineData("Z9", "No absence Z9 in your team's leave from today on.")]
    [InlineData("../x", "isn't a People First absence id")]
    public void approve_refuses_what_isnt_waiting_in_your_team(string id, string message)
    {
        var pf = Team();
        var (code, _, error, _) = Cli(Settled(), pf, "approve", id);
        Assert.Equal(2, code);
        Assert.Contains(message, error);
        Assert.Empty(pf.Posts);
    }

    [Fact]
    public void approve_in_journal_mode_sends_nothing()
    {
        var c = Settled();
        var pf = Team();
        var journal = Path.Combine(Path.GetDirectoryName(c.Path)!, "journal.log");
        var (code, output, _, _) = Cli(c, pf, "--writes", "journal", "--journal", journal, "approve", "A1");
        Assert.Equal(0, code);
        Assert.Contains("Not sent (journal mode): Jordan Lee annual leave 01/12 to 02/12.", output);
        Assert.Empty(pf.Posts);
        Assert.Contains("journal (not sent): POST people-first:/hrm/absences/approved?absence=2&absenceapproval=1&annotate=t {", File.ReadAllText(journal));
        Assert.False(File.Exists(c.StateFile));
    }

    [Fact]
    public void approve_in_refuse_mode_sends_nothing()
    {
        var pf = Team();
        var (code, _, error, _) = Cli(Settled(), pf, "--writes", "refuse", "approve", "A1");
        Assert.Equal(2, code);
        Assert.Contains("Refused a write (POST people-first:/hrm/absences/approved", error);
        Assert.Empty(pf.Posts);
    }

    [Fact]
    public void approve_that_isnt_confirmed_says_so()
    {
        var c = Settled();
        var pf = Team();
        pf.OnPost = (_, _) => throw new TimeoutException("timed out");
        var (code, _, error, _) = Cli(c, pf, "approve", "A1");
        Assert.Equal(2, code);
        Assert.Contains("Not confirmed: Jordan Lee annual leave 01/12 to 02/12. The approval was sent, but its answer was lost", error);
        Assert.Equal("unknown", Py.Str(TeamLeave.Approvals.Records(Runner.LoadState(c))["A1"]!["outcome"]));
    }

    [Fact]
    public void approve_needs_an_id()
    {
        var (code, _, error, _) = Cli(Settled(), Team(), "approve");
        Assert.Equal(2, code);
        Assert.Contains("the following arguments are required: absence", error);
    }

    [Fact]
    public void help_names_the_commands()
    {
        var (_, output, _, _) = Cli(Settled(), Team(), "--help");
        Assert.Contains("{config,sign-in,sync,approvals,approve,status,autostart}", output);
        Assert.Contains("team-leave approve ID", output);
    }

    [Fact]
    public void sync_approves_and_prints_it_then_status_counts()
    {
        var c = Settled();
        var pf = Team();
        var (code, output, _, graph) = Cli(c, pf, "sync");
        Assert.Equal(0, code);
        Assert.Contains("  Did add: Jordan Lee annual leave 01/12 to 02/12", output);
        Assert.Contains("  Approved: Jordan Lee annual leave 01/12 to 02/12", output);
        Assert.Contains("  Needs you: Robin Hall annual leave 08/12", output);
        Assert.Contains("      - it leaves nobody in on 08/12", output);
        Assert.Single(pf.Posts);
        Assert.Equal(3, graph.Calls.Count(call => call.StartsWith("POST /me/events ", StringComparison.Ordinal)));   // A1 just approved, A2 and A4
        (code, output, _, _) = Cli(c, pf, "status");
        Assert.Contains("Approvals:   on; 1 with you, 0 sent but not confirmed, 0 the rules would approve", output);
    }

    [Fact]
    public void sync_dry_run_approves_nothing()
    {
        var c = Settled();
        var pf = Team();
        var (code, output, _, graph) = Cli(c, pf, "sync", "--dry-run");
        Assert.Equal(0, code);
        Assert.Contains("  Would approve: Jordan Lee annual leave 01/12 to 02/12", output);
        Assert.Empty(pf.Posts);
        Assert.All(graph.Calls, call => Assert.StartsWith("GET ", call));
        Assert.False(File.Exists(c.StateFile));
    }
}
