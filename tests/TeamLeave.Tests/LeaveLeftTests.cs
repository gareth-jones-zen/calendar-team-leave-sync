using System.Globalization;
using System.Text.Json.Nodes;
using PyCompat;
using static TeamLeave.Tests.Fixtures;

namespace TeamLeave.Tests;

/// <summary>People First for leave-left: the team, each person's occupancy, allowance summary and details, and
/// listing (answered for the range asked, as People First does). Every call recorded; a POST fails the test.
/// Invented people and figures only.</summary>
public sealed class LeaveLeftPeopleFirst : IPeopleFirstApi
{
    public readonly List<Person> People = [Jordan, Robin];
    public readonly Dictionary<string, List<JsonObject>> Listings = new(StringComparer.Ordinal);
    public readonly Dictionary<string, (double Allowance, double Remaining)> Balances = new(StringComparer.Ordinal);
    public readonly HashSet<string> PartYear = new(StringComparer.Ordinal);
    public readonly HashSet<string> Missing = new(StringComparer.Ordinal);
    public readonly List<string> Calls = [];
    public LeaveYear Year = new(D("2026-01-01"), D("2026-12-31"));

    public LeaveLeftPeopleFirst Add(Person person, JsonObject e)
    {
        if (!Listings.TryGetValue(person.Id, out var list))
            Listings[person.Id] = list = [];
        list.Add(e);
        return this;
    }

    public string Me() => "ME";

    public IReadOnlyList<JsonNode?> TeamMembers(string person) => person != "ME" ? [] :
        People.Select(p => (JsonNode?)new JsonObject { ["personId"] = p.Id, ["firstName"] = p.Name.Split(' ')[0], ["lastName"] = p.Name.Split(' ')[1] }).ToList();

    public JsonNode? Events(string person, DateOnly start, DateOnly end)
    {
        Calls.Add($"events {person} {start:yyyy-MM-dd} {end:yyyy-MM-dd}");
        var events = Listings.GetValueOrDefault(person, [])
            .Where(e => D(Py.Str(e["startDate"])) <= end && D(Py.Str(e["endDate"])) >= start)
            .Select(e => (JsonObject)e.DeepClone()).ToArray();
        return Listing(events);
    }

    public JsonNode? Get(string path)
    {
        Calls.Add($"GET {path}");
        var parts = path.Split('/');
        if (path.EndsWith("/occupancies", StringComparison.Ordinal))
        {
            var person = parts[3];
            if (Missing.Contains(person))
                throw new PeopleFirstClient.PeopleFirstException("People First answered 404 to /hrm/people/x/occupancies") { Status = 404 };
            return new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["occupancies"] = new JsonArray(
                        new JsonObject { ["occupancyId"] = $"OLD-{person}", ["startDate"] = "2019-01-01T00:00:00", ["endDate"] = "2023-12-31T00:00:00" },
                        new JsonObject { ["occupancyId"] = $"OCC-{person}", ["startDate"] = "2024-01-01T00:00:00", ["endDate"] = null }),
                },
            };
        }
        var occupancy = parts[3];
        var who = occupancy["OCC-".Length..];
        if (path.Contains("/absenceallowancesummary/", StringComparison.Ordinal))
        {
            var (allowance, remaining) = Balances.GetValueOrDefault(who, (33, 33));
            return new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["absenceAllowanceSummary"] = new JsonObject
                    {
                        ["startDate"] = $"{Year.Start:yyyy-MM-dd}T00:00:00",
                        ["endDate"] = $"{Year.End:yyyy-MM-dd}T00:00:00",
                        ["allowance"] = PyJson.Float(allowance),
                        ["remainingBalance"] = PyJson.Float(remaining),
                        ["overridden"] = false,
                    },
                },
            };
        }
        if (path.Contains("/absenceallowancedetails/", StringComparison.Ordinal))
            return new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["absenceAllowanceDetails"] = new JsonObject
                    {
                        ["allowance"] = 25, ["nonProRatedAdjustments"] = 2, ["isPartYear"] = PartYear.Contains(who),
                    },
                },
            };
        throw new PeopleFirstClient.PeopleFirstException($"People First answered 404 to {path}") { Status = 404 };
    }

    public JsonNode? Post(string path, JsonObject body) => throw new InvalidOperationException("leave-left must never write");
}

public class LeaveLeftTests
{
    private static readonly HashSet<DateOnly> Bank2026 =
        [.. new[] { "2026-01-01", "2026-04-03", "2026-04-06", "2026-05-04", "2026-05-25", "2026-08-31", "2026-12-25", "2026-12-28" }.Select(D)];

    private static readonly HashSet<DateOnly> Bank2027 =
        [.. new[] { "2027-01-01", "2027-03-26", "2027-03-29", "2027-05-03", "2027-05-31", "2027-08-30", "2027-12-27", "2027-12-28" }.Select(D)];

    private static IReadOnlySet<DateOnly> Bank => Bank2026.Union(Bank2027).ToHashSet();

    private static Booking B(string start, string end, string sp = "FULL", string ep = "FULL", bool approved = true) =>
        new(D(start), D(end), sp, ep, approved);

    private static readonly AllowanceSummary Year2026 = new(new LeaveYear(D("2026-01-01"), D("2026-12-31")), 33, 12, false);

    // ---- bank holidays ----------------------------------------------------------------------------------------------

    [Fact]
    public void Bank_holidays_by_rule_match_govuk_for_2026_and_2027()
    {
        Assert.Equal(Bank2026.Order(), BankHolidays.ByRule(2026).Order());
        Assert.Equal(Bank2027.Order(), BankHolidays.ByRule(2027).Order());
        Assert.Equal(D("2027-03-28"), BankHolidays.Easter(2027));
        // Christmas on a Saturday (2021): Monday and Tuesday; New Year's Day on a Saturday (2022): Monday 3 Jan
        Assert.Contains(D("2021-12-27"), BankHolidays.ByRule(2021));
        Assert.Contains(D("2021-12-28"), BankHolidays.ByRule(2021));
        Assert.Contains(D("2022-01-03"), BankHolidays.ByRule(2022));
    }

    private static string Feed(params string[] dates) =>
        new JsonObject
        {
            ["england-and-wales"] = new JsonObject
            {
                ["division"] = "england-and-wales",
                ["events"] = new JsonArray(dates.Select(d => (JsonNode?)new JsonObject { ["title"] = "A day", ["date"] = d, ["notes"] = "" }).ToArray()),
            },
        }.ToJsonString();

    [Fact]
    public void Govuk_list_is_used_where_it_has_the_year_and_the_rules_where_it_doesnt()
    {
        var list = BankHolidays.For([2026, 2027], "england-and-wales", () => Feed("2026-01-01", "2026-06-15"));
        Assert.Contains(D("2026-06-15"), list.Days);   // a one-off from the list
        Assert.DoesNotContain(D("2026-04-03"), list.Days);   // the list wins for its years
        Assert.Contains(D("2027-03-26"), list.Days);   // 2027 isn't in it: by rule
        Assert.Equal("GOV.UK's list, and by rule for 2027", list.Source);
    }

    [Fact]
    public void An_unreadable_govuk_list_falls_back_to_the_rules_and_says_so()
    {
        var list = BankHolidays.For([2026], "england-and-wales", () => throw new HttpRequestException("no route"));
        Assert.Equal(Bank2026.Order(), list.Days.Order());
        Assert.Contains("couldn't be read (no route)", list.Source, StringComparison.Ordinal);
        Assert.Throws<LeaveError>(() => BankHolidays.For([2026], "scotland", () => throw new HttpRequestException("no route")));
    }

    // ---- counting working days ------------------------------------------------------------------------------------

    [Fact]
    public void Working_days_leave_out_weekends_and_bank_holidays()
    {
        // Mon 21 Dec to Thu 31 Dec 2026: 9 weekdays, two of them bank holidays (25th, and the 28th for Boxing Day)
        Assert.Equal(7, LeaveLeft.Days(B("2026-12-21", "2026-12-31"), Bank, D("2026-01-01"), D("2026-12-31")));
        Assert.Equal(0, LeaveLeft.Days(B("2026-11-14", "2026-11-15"), Bank, D("2026-01-01"), D("2026-12-31")));   // a weekend
    }

    [Fact]
    public void Half_days_count_as_half()
    {
        var year = (D("2026-01-01"), D("2026-12-31"));
        Assert.Equal(0.5, LeaveLeft.Days(B("2026-11-17", "2026-11-17", "HALF_AM", "HALF_AM"), Bank, year.Item1, year.Item2));
        Assert.Equal(0.5, LeaveLeft.Days(B("2026-11-17", "2026-11-17", "FULL", "HALF_PM"), Bank, year.Item1, year.Item2));
        Assert.Equal(1, LeaveLeft.Days(B("2026-11-17", "2026-11-17", "HALF_AM", "HALF_PM"), Bank, year.Item1, year.Item2));   // both halves
        // Tue afternoon to Thu morning: 0.5 + 1 + 0.5
        Assert.Equal(2, LeaveLeft.Days(B("2026-11-17", "2026-11-19", "HALF_PM", "HALF_AM"), Bank, year.Item1, year.Item2));
        // starting in the morning is the whole first day
        Assert.Equal(3, LeaveLeft.Days(B("2026-11-17", "2026-11-19", "HALF_AM", "FULL"), Bank, year.Item1, year.Item2));
    }

    [Fact]
    public void Leave_across_the_year_end_is_split_between_the_years()
    {
        var b = B("2026-12-30", "2027-01-04");
        Assert.Equal(2, LeaveLeft.Days(b, Bank, D("2026-01-01"), D("2026-12-31")));
        Assert.Equal(1, LeaveLeft.Days(b, Bank, D("2027-01-01"), D("2027-12-31")));   // 1 Jan is a bank holiday
    }

    // ---- the figures ------------------------------------------------------------------------------------------------

    private static readonly List<Booking> Bookings =
    [
        B("2026-11-02", "2026-11-04"),                       // taken: 3
        B("2026-11-11", "2026-11-13"),                       // 11th and today taken, 13th booked
        B("2026-12-21", "2026-12-31"),                       // booked: 7
        B("2026-12-07", "2026-12-07", approved: false),      // waiting: 1
        B("2026-11-20", "2026-11-20", "HALF_PM", "HALF_PM", approved: false),   // waiting: 0.5
    ];

    private static LeaveLeftRow Row(double remaining, bool flagActive = false, LeaveLeftSettings? settings = null) =>
        LeaveLeft.Figures(Jordan, Year2026 with { Remaining = remaining }, false, Bookings, [B("2027-02-01", "2027-02-02")],
            new LeaveYear(D("2027-01-01"), D("2027-12-31")), Bank, Today, settings ?? new LeaveLeftSettings(), flagActive);

    [Fact]
    public void Taken_and_booked_split_at_today_with_bank_holidays_counted_as_used()
    {
        var row = Row(remaining: 12);
        // taken: 3 + 2 (11th, 12th) + 6 bank holidays to 12 Nov; booked: 1 (13th) + 7 + 2 bank holidays in December
        Assert.Equal(11, row.Taken);
        Assert.Equal(10, row.Booked);
        Assert.Equal(12, row.Left);
        Assert.Equal(2, row.NextYearBooked);
        Assert.True(row.Reconciles);
    }

    [Fact]
    public void Waiting_is_kept_out_of_taken_booked_and_the_balance()
    {
        var row = Row(remaining: 12);
        Assert.Equal(1.5, row.Waiting);
        Assert.Equal(0, row.Difference);
    }

    [Fact]
    public void A_balance_that_doesnt_reconcile_is_still_shown_with_a_note()
    {
        var off = Row(remaining: 9);
        Assert.False(off.Reconciles);
        Assert.Equal(9, off.Left);
        Assert.Equal(-3, off.Difference);
        Assert.False(off.WaitingExplains);

        var waiting = Row(remaining: 10.5);   // as if the 1.5 days waiting were used
        Assert.False(waiting.Reconciles);
        Assert.True(waiting.WaitingExplains);

        Assert.True(Row(remaining: 11.5).Reconciles);   // within half a day
        var report = new LeaveLeftReport(Today, Year2026.Year, true, [off], "GOV.UK's list", null, false, 5, "Holiday");
        var text = LeaveLeft.Text(report);
        Assert.Contains("Jordan Lee: doesn't reconcile: allowance less taken and booked is 12, People First says 9 left.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_flag_is_more_than_flag_days_left_less_anything_waiting()
    {
        Assert.True(Row(remaining: 12, flagActive: true).Flagged);              // 12 - 1.5 > 5
        Assert.False(Row(remaining: 12, flagActive: false).Flagged);            // before flag_from
        Assert.False(Row(remaining: 6, flagActive: true).Flagged);              // 6 - 1.5 = 4.5
        Assert.False(Row(remaining: 12, flagActive: true, new LeaveLeftSettings { FlagDays = 11 }).Flagged);
    }

    [Fact]
    public void The_flag_date_is_in_the_leave_year()
    {
        Assert.Equal(D("2026-10-01"), LeaveLeft.FlagDate(new LeaveYear(D("2026-01-01"), D("2026-12-31")), 10, 1));
        Assert.Equal(D("2027-02-01"), LeaveLeft.FlagDate(new LeaveYear(D("2026-04-01"), D("2027-03-31")), 2, 1));
        Assert.Equal(D("2027-02-28"), LeaveLeft.FlagDate(new LeaveYear(D("2026-04-01"), D("2027-03-31")), 2, 29));
    }

    // ---- the command ------------------------------------------------------------------------------------------------

    private static LeaveLeftPeopleFirst Team()
    {
        var pf = new LeaveLeftPeopleFirst();
        pf.Add(Jordan, Event("A1", "2026-11-02", "2026-11-04"))
          .Add(Jordan, Event("A2", "2026-12-21", "2026-12-31"))
          .Add(Jordan, Event("A3", "2026-12-07", "2026-12-07", state: "AwaitingApproval"))
          .Add(Jordan, Event("A4", "2026-10-05", "2026-10-09", type: "T3"))          // sickness: never counted
          .Add(Jordan, Event("A5", "2026-09-01", "2026-09-01", type: "T2"))          // another type: not this allowance
          .Add(Jordan, Event("A6", "2026-08-03", "2026-08-03", state: "Rejected"))
          .Add(Jordan, Event("A7", "2027-02-01", "2027-02-03"))                      // next year: 3
          .Add(Robin, Event("B1", "2026-06-01", "2026-06-05"));
        // Jordan: taken 3 + 6 bank holidays, booked 7 + 2 = 18, so 15 left of 33; Robin: 5 + 6 taken, 2 booked, 20 left of 33
        pf.Balances["P1"] = (33, 15);
        pf.Balances["P2"] = (33, 21);   // a day out
        pf.PartYear.Add("P2");
        return pf;
    }

    private static (int Code, string Out, string Err, Config Config) Run(LeaveLeftPeopleFirst pf, DateTime now, params string[] argv)
    {
        // the log and state beside the settings, as the command line loads them from the file
        var c = TeamLeave.Config.Load(Config(Settings.Replace("max_deletes = 3", "max_deletes = 3\nstate_file = \"state.json\"\nlog_file = \"log.txt\"")).Path);
        var output = new StringWriter();
        var error = new StringWriter();
        var code = TeamLeave.Cli.Run(["--config", c.Path, "leave-left", .. argv], new CliHost
        {
            Out = output,
            Err = error,
            Graph = (_, _) => throw new InvalidOperationException("leave-left doesn't use the calendar"),
            OpenPeopleFirst = (_, _) => pf,
            Notify = (_, _) => throw new InvalidOperationException("leave-left notifies nothing"),
            Now = () => now,
            BankHolidayFeed = () => Feed([.. Bank2026.Concat(Bank2027).Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]),
        });
        return (code, output.ToString(), error.ToString(), c);
    }

    private static readonly DateTime Thursday = new(2026, 11, 12, 10, 0, 0);

    [Fact]
    public void Leave_left_shows_each_report_and_the_footer()
    {
        var (code, output, error, c) = Run(Team(), Thursday);
        Assert.Equal(0, code);
        Assert.Equal("", error);
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.Equal("Leave left to book, 1 Jan 2026 to 31 Dec 2026 (as of Thu 12 Nov)", lines[0]);
        Assert.Matches(@"^  Name\s+Allowance\s+Taken\s+Booked\s+Waiting\s+Left to book\s+2027 booked\s+Notes$", lines[2]);
        Assert.Matches(@"^  Jordan Lee\s+33\s+9\s+9\s+1\s+15\s+3\s+more than 5 left to book$", lines[3]);
        Assert.Matches(@"^  Robin Hall\s+33\s+11\s+2\s+0\s+21\s+0\s+more than 5 left to book; part year; doesn't reconcile \(below\)$", lines[4]);
        Assert.Contains("  Robin Hall: doesn't reconcile: allowance less taken and booked is 20, People First says 21 left.", lines);
        Assert.Contains("Leave year 1 Jan 2026 to 31 Dec 2026; figures from People First.", lines);
        Assert.Contains("From 1 Oct, more than 5 days left to book (less any waiting) is flagged: 2 people.", lines);
        Assert.False(File.Exists(c.StateFile));
    }

    [Fact]
    public void Leave_left_reads_only_and_logs_counts_only()
    {
        var pf = Team();
        var (_, _, _, c) = Run(pf, Thursday);
        Assert.All(pf.Calls, call => Assert.True(call.StartsWith("GET ", StringComparison.Ordinal) || call.StartsWith("events ", StringComparison.Ordinal)));
        Assert.Contains("GET /hrm/people/P1/occupancies", pf.Calls);
        Assert.Contains("GET /hrm/occupancies/OCC-P1/absenceallowancesummary/effdate/2026-11-12", pf.Calls);
        Assert.Contains("GET /hrm/occupancies/OCC-P1/absenceallowancedetails/effdate/2026-11-12", pf.Calls);
        Assert.Contains("events P1 2026-01-01 2026-12-31", pf.Calls);
        Assert.Contains("events P1 2027-01-01 2027-12-31", pf.Calls);
        Assert.DoesNotContain(pf.Calls, call => call.Contains("/hrm/absences/", StringComparison.Ordinal) || call.Contains("adjustments", StringComparison.Ordinal));
        var log = File.ReadAllText(c.LogFile);
        Assert.Single(log.Trim().Split('\n'));
        Assert.Contains("leave-left: 2 people, 1 reconciled, 2 flagged, 0 couldn't be read", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Jordan", log, StringComparison.Ordinal);
        Assert.DoesNotContain("33", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Before_flag_from_nobody_is_flagged()
    {
        var (code, output, _, _) = Run(Team(), new DateTime(2026, 9, 1, 10, 0, 0));
        Assert.Equal(0, code);
        Assert.DoesNotContain("more than 5 left to book", output, StringComparison.Ordinal);
        Assert.Contains("More than 5 days left to book is flagged from 1 Oct 2026.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Leave_left_as_json()
    {
        var (code, output, _, _) = Run(Team(), Thursday, "--json");
        Assert.Equal(0, code);
        var json = JsonNode.Parse(output)!;
        Assert.Equal("People First", json["source"]!.GetValue<string>());
        Assert.Equal("2026-01-01", json["year_start"]!.GetValue<string>());
        Assert.True(json["flag"]!["active"]!.GetValue<bool>());
        var jordan = json["people"]![0]!;
        Assert.Equal("Jordan Lee", jordan["name"]!.GetValue<string>());
        Assert.Equal(15, jordan["left"]!.GetValue<double>());
        Assert.Equal(1, jordan["waiting"]!.GetValue<double>());
        Assert.Equal(3, jordan["next_year_booked"]!.GetValue<double>());
        Assert.True(jordan["reconciles"]!.GetValue<bool>());
        var robin = json["people"]![1]!;
        Assert.False(robin["reconciles"]!.GetValue<bool>());
        Assert.Equal(1, robin["difference"]!.GetValue<double>());
        Assert.True(robin["part_year"]!.GetValue<bool>());
    }

    [Fact]
    public void Another_year_is_read_as_of_1_January_with_no_next_year_column()
    {
        var pf = Team();
        pf.Year = new LeaveYear(D("2027-01-01"), D("2027-12-31"));
        pf.Balances["P1"] = (33, 22);   // 3 booked + 8 bank holidays
        pf.Balances["P2"] = (33, 25);
        var (code, output, _, _) = Run(pf, Thursday, "--year", "2027");
        Assert.Equal(0, code);
        Assert.Contains("GET /hrm/occupancies/OCC-P1/absenceallowancesummary/effdate/2027-01-01", pf.Calls);
        Assert.DoesNotContain("2028 booked", output, StringComparison.Ordinal);
        Assert.StartsWith("Leave left to book, 1 Jan 2027 to 31 Dec 2027\r\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("doesn't reconcile", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Someone_who_cant_be_read_is_shown_and_the_rest_still_are()
    {
        var pf = Team();
        pf.Missing.Add("P2");
        var (code, output, _, _) = Run(pf, Thursday);
        Assert.Equal(2, code);
        Assert.Contains("Robin Hall", output, StringComparison.Ordinal);
        Assert.Contains("couldn't be read: People First answered 404", output, StringComparison.Ordinal);
        Assert.Matches(@"Jordan Lee\s+33", output);
    }

    [Fact]
    public void Bad_year_is_a_usage_error()
    {
        var (code, _, error, _) = Run(Team(), Thursday, "--year", "26");
        Assert.Equal(2, code);
        Assert.Contains("argument --year: invalid year: '26'", error, StringComparison.Ordinal);
    }

    // ---- settings ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Leave_left_settings_default_and_are_checked()
    {
        var plain = Config();
        Assert.Equal((10, 1, 5.0, "Holiday", "england-and-wales"),
            (plain.LeaveLeft.FlagMonth, plain.LeaveLeft.FlagDay, plain.LeaveLeft.FlagDays, plain.LeaveLeft.Type, plain.LeaveLeft.Region));
        var set = Config(Settings + "[leave_left]\nflag_from = \"11-15\"\nflag_days = 2.5\n");
        Assert.Equal((11, 15, 2.5), (set.LeaveLeft.FlagMonth, set.LeaveLeft.FlagDay, set.LeaveLeft.FlagDays));
        Assert.Throws<PyValueError>(() => Config(Settings + "[leave_left]\nflag_from = \"13-01\"\n"));
        Assert.Throws<PyValueError>(() => Config(Settings + "[leave_left]\nflag_day = 5\n"));
        Assert.Throws<PyValueError>(() => Config(Settings + "[leave_left]\nregion = \"wales\"\n"));
    }
}
