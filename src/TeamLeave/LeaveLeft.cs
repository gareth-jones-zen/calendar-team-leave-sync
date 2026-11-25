using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyCompat;

namespace TeamLeave;

/// <summary>A leave year (allowance period), first and last day inclusive.</summary>
public sealed record LeaveYear(DateOnly Start, DateOnly End)
{
    public bool Contains(DateOnly day) => day >= Start && day <= End;
}

/// <summary>People First's allowance summary for one person and leave year (days): the allowance, what it says is left,
/// and whether the allowance was set by hand.</summary>
public sealed record AllowanceSummary(LeaveYear Year, double Allowance, double Remaining, bool Overridden);

/// <summary>One absence of the allowance's type from the listing, approved or waiting for approval: who and when only.
/// End is inclusive; the parts are FULL, HALF_AM or HALF_PM.</summary>
public sealed record Booking(DateOnly Start, DateOnly End, string StartPart, string EndPart, bool Approved);

/// <summary>The bank holidays used for counting, and where they came from (for the footer).</summary>
public sealed record BankHolidayList(IReadOnlySet<DateOnly> Days, string Source);

/// <summary>One person's line: People First's allowance and balance, and the taken, booked and waiting days worked out
/// from its absences. Held in memory and printed; never logged, stored or notified.</summary>
public sealed record LeaveLeftRow(Person Person)
{
    public LeaveYear? Year { get; init; }
    public double Allowance { get; init; }
    public double Taken { get; init; }
    public double Booked { get; init; }
    public double Waiting { get; init; }
    public double Left { get; init; }

    /// <summary>Approved days already in the next leave year (only when the year shown is the current one).</summary>
    public double? NextYearBooked { get; init; }

    public bool PartYear { get; init; }
    public bool Overridden { get; init; }

    /// <summary>People First's balance less (allowance - taken - booked): within half a day, it reconciles.</summary>
    public double Difference { get; init; }

    public bool Reconciles => Math.Abs(Difference) <= LeaveLeft.Tolerance;

    /// <summary>It doesn't reconcile, but would if the days waiting for approval counted as used.</summary>
    public bool WaitingExplains { get; init; }

    public bool Flagged { get; init; }

    /// <summary>Why this person couldn't be read (the figures are then empty).</summary>
    public string? Error { get; init; }
}

/// <summary>What leave-left shows: the leave year, today, a row per report, and whether the late-year flag is on.</summary>
public sealed record LeaveLeftReport(DateOnly Today, LeaveYear? Year, bool Current, List<LeaveLeftRow> Rows, string BankHolidays,
    DateOnly? FlagFrom, bool FlagActive, double FlagDays, string Type);

/// <summary>GOV.UK's bank holidays (https://www.gov.uk/bank-holidays.json), and England and Wales's worked out by rule
/// for a year GOV.UK doesn't list, or when its list can't be read.</summary>
public static class BankHolidays
{
    public const string Feed = "https://www.gov.uk/bank-holidays.json";

    /// <summary>The list as text, read now (a failure throws).</summary>
    public static string Fetch()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("calendar-team-leave-sync");
        return http.GetStringAsync(Feed).GetAwaiter().GetResult();
    }

    /// <summary>One region's dates from GOV.UK's list. FormatException if it isn't the shape GOV.UK publishes.</summary>
    public static HashSet<DateOnly> Parse(string json, string region)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException error)
        {
            throw new FormatException($"GOV.UK's bank holiday list isn't JSON: {error.Message}", error);
        }
        if (root?[region]?["events"] is not JsonArray events)
            throw new FormatException($"GOV.UK's bank holiday list has no {region} events.");
        var days = new HashSet<DateOnly>();
        foreach (var e in events)
        {
            var text = e?["date"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (text is null || !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                throw new FormatException("GOV.UK's bank holiday list has an event without a date.");
            days.Add(day);
        }
        return days;
    }

    /// <summary>Easter Sunday (the Gregorian computus).</summary>
    public static DateOnly Easter(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }

    /// <summary>England and Wales's eight bank holidays in a year, by the usual rules (substitute days for a weekend).
    /// One-off holidays (a coronation, say) and moved ones aren't known to the rules: GOV.UK's list has them.</summary>
    public static List<DateOnly> ByRule(int year)
    {
        static DateOnly Monday(DateOnly day, int step)
        {
            while (day.DayOfWeek != DayOfWeek.Monday)
                day = day.AddDays(step);
            return day;
        }
        var newYear = new DateOnly(year, 1, 1);
        newYear = newYear.DayOfWeek switch { DayOfWeek.Saturday => newYear.AddDays(2), DayOfWeek.Sunday => newYear.AddDays(1), _ => newYear };
        var easter = Easter(year);
        var christmas = new DateOnly(year, 12, 25);
        var (xmas, boxing) = christmas.DayOfWeek switch
        {
            DayOfWeek.Friday => (christmas, christmas.AddDays(3)),       // Boxing Day on Saturday: Monday
            DayOfWeek.Saturday => (christmas.AddDays(2), christmas.AddDays(3)),
            DayOfWeek.Sunday => (christmas.AddDays(2), christmas.AddDays(1)),   // Boxing Day is Monday; Christmas on Tuesday
            _ => (christmas, christmas.AddDays(1)),
        };
        return
        [
            newYear, easter.AddDays(-2), easter.AddDays(1), Monday(new DateOnly(year, 5, 1), 1), Monday(new DateOnly(year, 5, 31), -1),
            Monday(new DateOnly(year, 8, 31), -1), xmas, boxing,
        ];
    }

    /// <summary>The bank holidays for these years: GOV.UK's, or (England and Wales only) by rule for a year its list
    /// doesn't have, or for all of them if it can't be read. A region with no rules and no list throws LeaveError.</summary>
    public static BankHolidayList For(IEnumerable<int> years, string region, Func<string> fetch)
    {
        HashSet<DateOnly>? listed = null;
        string? failure = null;
        try
        {
            listed = Parse(fetch(), region);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or FormatException or IOException)
        {
            failure = error is TaskCanceledException ? "it timed out" : error.Message;
        }
        var days = new HashSet<DateOnly>();
        var byRule = new List<int>();
        foreach (var year in years.Distinct().Order())
        {
            var fromList = listed?.Where(d => d.Year == year).ToList() ?? [];
            if (fromList.Count > 0)
            {
                days.UnionWith(fromList);
                continue;
            }
            if (region != "england-and-wales")
                throw new LeaveError(failure is not null
                    ? $"GOV.UK's bank holiday list couldn't be read ({failure}), and {region}'s can't be worked out by rule."
                    : $"GOV.UK's bank holiday list has nothing for {year} in {region}.");
            days.UnionWith(ByRule(year));
            byRule.Add(year);
        }
        var source = failure is not null ? $"England and Wales's by rule, as GOV.UK's list couldn't be read ({failure})"
            : byRule.Count > 0 ? $"GOV.UK's list, and by rule for {string.Join(", ", byRule)}"
            : "GOV.UK's list";
        return new BankHolidayList(days, source);
    }
}

/// <summary>leave-left: how much annual leave each report has left to book this leave year. <b>Reads only</b>, every
/// call a GET through the read-only fence, and <b>holds the figures in memory only</b>: they're printed, never logged,
/// stored or notified (the log gets counts).
///
/// Per person: the occupancy (/hrm/people/{id}/occupancies), its allowance summary (allowance, remaining balance,
/// overridden) and details (only isPartYear is kept: the rest, adjustments included, is dropped as read), and the
/// events listing for the year (and the next), of which only the allowance's type, approved or waiting, is kept as
/// dates. Never /hrm/absences/{id} (the reason) or the adjustments endpoint.</summary>
public static class LeaveLeft
{
    public const double Tolerance = 0.5;
    public static readonly string[] Regions = ["england-and-wales", "scotland", "northern-ireland"];

    /// <summary>"10-01" as month 10, day 1 (any day that exists in a leap year).</summary>
    public static bool TryMonthDay(string text, out int month, out int day)
    {
        month = day = 0;
        if (!DateOnly.TryParseExact("2000-" + text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return false;
        (month, day) = (date.Month, date.Day);
        return true;
    }

    /// <summary>The first day on or after the year's start with this month and day (29 Feb in a common year: 28 Feb).</summary>
    public static DateOnly FlagDate(LeaveYear year, int month, int day)
    {
        DateOnly In(int y) => new(y, month, Math.Min(day, DateTime.DaysInMonth(y, month)));
        var date = In(year.Start.Year);
        return date >= year.Start ? date : In(year.Start.Year + 1);
    }

    /// <summary>The working days an absence uses between two days (inclusive): Monday to Friday, not bank holidays, a
    /// half day as 0.5 (People First's day parts, read as the calendar titles read them).</summary>
    public static double Days(Booking booking, IReadOnlySet<DateOnly> bankHolidays, DateOnly from, DateOnly to)
    {
        var (single, first, last) = CalendarDays.ReportLeaveTitles.Halves(booking.Start, booking.End, booking.StartPart, booking.EndPart,
            Leave.Full, Leave.Am, Leave.Pm);
        var total = 0.0;
        var start = booking.Start > from ? booking.Start : from;
        var end = booking.End < to ? booking.End : to;
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            if (!Approvals.IsWorkingDay(day) || bankHolidays.Contains(day))
                continue;
            var half = booking.Start == booking.End ? single is not null
                : (day == booking.Start && first is not null) || (day == booking.End && last is not null);
            total += half ? 0.5 : 1;
        }
        return total;
    }

    /// <summary>The bank holidays on working days between two days (inclusive).</summary>
    public static int BankHolidaysBetween(IReadOnlySet<DateOnly> bankHolidays, DateOnly from, DateOnly to) =>
        bankHolidays.Count(d => d >= from && d <= to && Approvals.IsWorkingDay(d));

    /// <summary>One person's figures, from People First's summary and the absences of the allowance's type. Taken is up
    /// to and including today, booked after it, both counting the year's bank holidays as used (People First adds them
    /// to the allowance and counts them as taken); waiting is kept out of both, as People First keeps it out of the
    /// balance. A balance that doesn't reconcile is still People First's, with a note.</summary>
    public static LeaveLeftRow Figures(Person person, AllowanceSummary summary, bool partYear, IReadOnlyList<Booking> bookings,
        IReadOnlyList<Booking>? nextYear, LeaveYear? next, IReadOnlySet<DateOnly> bankHolidays, DateOnly today, LeaveLeftSettings settings,
        bool flagActive)
    {
        var year = summary.Year;
        var past = today < year.End ? today : year.End;
        var future = today >= year.Start ? today.AddDays(1) : year.Start;
        double taken = 0, booked = 0, waiting = 0;
        foreach (var b in bookings)
        {
            if (b.Approved)
            {
                taken += past >= year.Start ? Days(b, bankHolidays, year.Start, past) : 0;
                booked += future <= year.End ? Days(b, bankHolidays, future, year.End) : 0;
            }
            else
                waiting += Days(b, bankHolidays, year.Start, year.End);
        }
        if (past >= year.Start)
            taken += BankHolidaysBetween(bankHolidays, year.Start, past);
        if (future <= year.End)
            booked += BankHolidaysBetween(bankHolidays, future, year.End);
        var expected = summary.Allowance - taken - booked;
        var difference = summary.Remaining - expected;
        double? nextBooked = next is null ? null
            : (nextYear ?? []).Where(b => b.Approved).Sum(b => Days(b, bankHolidays, next.Start, next.End));
        return new LeaveLeftRow(person)
        {
            Year = year,
            Allowance = summary.Allowance,
            Taken = taken,
            Booked = booked,
            Waiting = waiting,
            Left = summary.Remaining,
            NextYearBooked = nextBooked,
            PartYear = partYear,
            Overridden = summary.Overridden,
            Difference = difference,
            WaitingExplains = Math.Abs(difference) > Tolerance && waiting > 0 && Math.Abs(difference + waiting) <= Tolerance,
            Flagged = flagActive && summary.Remaining - waiting > settings.FlagDays,
        };
    }

    // ---- reading People First's answers ------------------------------------------------------------------------

    private static JsonNode? Get(JsonNode? obj, string key) => obj is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    /// <summary>A key, ignoring case (People First spells usageTotals both ways).</summary>
    private static JsonNode? GetAnyCase(JsonNode? obj, string key) =>
        obj is JsonObject o ? o.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value : null;

    private static double Number(JsonNode? node, string what) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : throw new LeaveError($"People First's {what} isn't a number.");

    private static DateOnly? Date(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var text) || text.Length < 10)
            return null;
        return DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
    }

    /// <summary>The occupancy (job) covering a day, from /hrm/people/{id}/occupancies; with none covering it, the one
    /// that started last.</summary>
    public static string Occupancy(JsonNode? body, DateOnly day)
    {
        if (Get(Get(body, "data"), "occupancies") is not JsonArray list || list.Count == 0)
            throw new LeaveError("People First gives no occupancy (job) for them.");
        var jobs = list.Select(o => (Id: Get(o, "occupancyId") is JsonValue v && v.TryGetValue<string>(out var id) ? id : null,
                Start: Date(Get(o, "startDate")), End: Date(Get(o, "endDate"))))
            .Where(o => !string.IsNullOrEmpty(o.Id)).ToList();
        if (jobs.Count == 0)
            throw new LeaveError("People First's occupancies have no occupancyId.");
        var covering = jobs.FirstOrDefault(o => (o.Start is null || o.Start <= day) && (o.End is null || o.End >= day));
        var id = covering.Id ?? jobs.OrderByDescending(o => o.Start ?? DateOnly.MinValue).First().Id;
        return Approvals.SafeId().IsMatch(id!) ? id! : throw new LeaveError("People First's occupancy id isn't one it could be asked about.");
    }

    /// <summary>data.absenceAllowanceSummary: the year, allowance, remaining balance (days) and overridden.</summary>
    public static AllowanceSummary Summary(JsonNode? body)
    {
        var s = Get(Get(body, "data"), "absenceAllowanceSummary") ?? throw new LeaveError("People First's allowance summary has changed shape.");
        var start = Date(Get(s, "startDate")) ?? throw new LeaveError("People First's allowance summary has no startDate.");
        var end = Date(Get(s, "endDate")) ?? throw new LeaveError("People First's allowance summary has no endDate.");
        var overridden = Get(s, "overridden") is JsonValue o && o.GetValueKind() == JsonValueKind.True;
        return new AllowanceSummary(new LeaveYear(start, end), Number(Get(s, "allowance"), "allowance"),
            Number(Get(s, "remainingBalance"), "remaining balance"), overridden);
    }

    /// <summary>isPartYear from the allowance details, wherever it sits; nothing else in them is kept.</summary>
    public static bool PartYear(JsonNode? body)
    {
        switch (body)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    if (string.Equals(key, "isPartYear", StringComparison.OrdinalIgnoreCase))
                        return value is JsonValue v && v.GetValueKind() == JsonValueKind.True;
                    if (value is JsonObject or JsonArray && PartYear(value))
                        return true;
                }
                return false;
            case JsonArray a:
                return a.Any(PartYear);
            default:
                return false;
        }
    }

    /// <summary>The listing's absences of one type, approved or waiting for approval, as dates and day parts. Everything
    /// else is dropped unread. LeaveError if the listing has changed shape or People First has no such type.</summary>
    public static List<Booking> Bookings(JsonNode? body, string type)
    {
        if (Get(Get(body, "data"), "events") is not JsonArray events)
            throw new LeaveError("People First's events listing has changed shape (no data.events).");
        var meta = Get(body, "meta") ?? new JsonObject();
        var types = Leave.Lookup(meta, "events.eventTypeId");
        if (types.Count == 0)
            throw new LeaveError("People First's events listing didn't name the absence types (annotate=t has changed?).");
        var ids = types.Where(t => t.Value == type).Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
            throw new LeaveError($"People First has no absence type called {Py.ReprString(type)}: check type under [leave_left].");
        var starts = Leave.Lookup(meta, "events.startTypeId");
        var ends = Leave.Lookup(meta, "events.endTypeId");
        var approved = PyBits.Key(JsonValue.Create(Approvals.ApprovedState));
        var pending = PyBits.Key(JsonValue.Create(Approvals.PendingState));
        var output = new List<Booking>();
        foreach (var e in events)
        {
            var state = PyBits.Key(Get(e, "state"));
            if (!ids.Contains(PyBits.Key(Get(e, "eventTypeId"))) || (state != approved && state != pending))
                continue;   // another type or state: dropped unread
            var start = Date(Get(e, "startDate"));
            var end = Date(Get(e, "endDate"));
            if (start is null || end is null)
                throw new LeaveError("An absence in People First's listing couldn't be read (its dates).");
            var startPart = Leave.Part(Py.Truthy(Get(e, "startTypeId")) ? Get(e, "startTypeId") : JsonValue.Create(Leave.Full), starts);
            var endPart = Leave.Part(Py.Truthy(Get(e, "endTypeId")) ? Get(e, "endTypeId") : JsonValue.Create(Leave.Full), ends);
            output.Add(new Booking(start.Value, end.Value, startPart, endPart, state == approved));
        }
        return output;
    }

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed record Raw(Person Person, AllowanceSummary? Summary, bool PartYear, List<Booking> Bookings, LeaveYear? Next,
        List<Booking>? NextBookings, string? Error);

    /// <summary>Read your reports' allowances and leave, and work out each one's line. <paramref name="year"/> null (or
    /// this year): the leave year People First has today; another year: the one it has on 1 January of it. Only GETs,
    /// through <paramref name="api"/> (give it the read-only fence). A person who can't be read gets a line saying why;
    /// a sign-in needed stops it.</summary>
    public static LeaveLeftReport Read(Config config, IPeopleFirstApi api, DateOnly today, int? year,
        Func<IEnumerable<int>, BankHolidayList> bankHolidays)
    {
        var settings = config.LeaveLeft;
        var effective = year is null || year == today.Year ? today : new DateOnly(year.Value, 1, 1);
        var raws = new List<Raw>();
        foreach (var person in Leave.Team(config, api))
        {
            try
            {
                var occupancy = Occupancy(api.Get($"/hrm/people/{person.Id}/occupancies"), effective);
                var summary = Summary(api.Get($"/hrm/occupancies/{occupancy}/absenceallowancesummary/effdate/{Iso(effective)}"));
                var partYear = PartYear(api.Get($"/hrm/occupancies/{occupancy}/absenceallowancedetails/effdate/{Iso(effective)}"));
                var bookings = Bookings(api.Events(person.Id, summary.Year.Start, summary.Year.End), settings.Type);
                LeaveYear? next = null;
                List<Booking>? nextBookings = null;
                if (summary.Year.Contains(today))
                {
                    next = new LeaveYear(summary.Year.End.AddDays(1), summary.Year.End.AddDays(1).AddYears(1).AddDays(-1));
                    nextBookings = Bookings(api.Events(person.Id, next.Start, next.End), settings.Type);
                }
                raws.Add(new Raw(person, summary, partYear, bookings, next, nextBookings, null));
            }
            catch (PeopleFirstClient.PeopleFirstException error) when (error is not PeopleFirstClient.SignInNeededException)
            {
                raws.Add(new Raw(person, null, false, [], null, null, error.Message));
            }
            catch (LeaveError error)
            {
                raws.Add(new Raw(person, null, false, [], null, null, error.Message));
            }
        }
        var read = raws.Where(r => r.Summary is not null).ToList();
        var yearShown = read.GroupBy(r => r.Summary!.Year).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        var current = yearShown?.Contains(today) ?? false;
        var years = read.SelectMany(r => new[] { r.Summary!.Year.Start.Year, r.Summary.Year.End.Year }
            .Concat(r.Next is null ? [] : [r.Next.Start.Year, r.Next.End.Year])).ToList();
        var holidays = years.Count == 0 ? new BankHolidayList(new HashSet<DateOnly>(), "none needed") : bankHolidays(years);
        DateOnly? flagFrom = yearShown is null ? null : FlagDate(yearShown, settings.FlagMonth, settings.FlagDay);
        var rows = raws.Select(r =>
        {
            if (r.Summary is null)
                return new LeaveLeftRow(r.Person) { Error = r.Error };
            var flagDate = FlagDate(r.Summary.Year, settings.FlagMonth, settings.FlagDay);
            var active = r.Summary.Year.Contains(today) && today >= flagDate;
            return Figures(r.Person, r.Summary, r.PartYear, r.Bookings, r.NextBookings, r.Next, holidays.Days, today, settings, active);
        }).ToList();
        var flagActive = current && flagFrom is { } from && today >= from;
        return new LeaveLeftReport(today, yearShown, current, rows, holidays.Source, flagFrom, flagActive, settings.FlagDays, settings.Type);
    }

    // ---- showing it -------------------------------------------------------------------------------------------------

    public static string Num(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static string Long(DateOnly day) => day.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The report as a table, with a note line for anything that doesn't reconcile and a footer.</summary>
    public static string Text(LeaveLeftReport report)
    {
        var lines = new List<string>();
        var title = report.Year is { } y
            ? $"Leave left to book, {Long(y.Start)} to {Long(y.End)}{(report.Current ? $" (as of {report.Today.ToString("ddd d MMM", CultureInfo.InvariantCulture)})" : "")}"
            : "Leave left to book";
        lines.Add(title);
        lines.Add("");
        var nextHeader = report.Current && report.Year is { } year ? $"{year.End.AddDays(1).Year} booked" : null;
        var header = new List<string> { "Name", "Allowance", "Taken", "Booked", "Waiting", "Left to book" };
        if (nextHeader is not null)
            header.Add(nextHeader);
        header.Add("Notes");
        var table = new List<List<string>> { header };
        foreach (var row in report.Rows)
        {
            if (row.Error is not null)
            {
                var blank = Enumerable.Repeat("-", header.Count - 2).ToList();
                table.Add([row.Person.Name, .. blank, $"couldn't be read: {row.Error}"]);
                continue;
            }
            var notes = new List<string>();
            if (row.Flagged)
                notes.Add($"more than {Num(report.FlagDays)} left to book");
            if (row.PartYear)
                notes.Add("part year");
            if (row.Overridden)
                notes.Add("allowance overridden");
            if (row.Year is { } own && own != report.Year)
                notes.Add($"leave year {Long(own.Start)} to {Long(own.End)}");
            if (!row.Reconciles)
                notes.Add("doesn't reconcile (below)");
            var cells = new List<string> { row.Person.Name, Num(row.Allowance), Num(row.Taken), Num(row.Booked), Num(row.Waiting), Num(row.Left) };
            if (nextHeader is not null)
                cells.Add(row.NextYearBooked is { } n ? Num(n) : "-");
            cells.Add(string.Join("; ", notes));
            table.Add(cells);
        }
        var widths = Enumerable.Range(0, header.Count - 1).Select(i => table.Max(r => r[i].Length)).ToList();
        foreach (var r in table)
        {
            var parts = r.Take(header.Count - 1).Select((cell, i) => i == 0 ? cell.PadRight(widths[i]) : cell.PadLeft(widths[i]));
            lines.Add(("  " + string.Join("  ", parts) + "  " + r[^1]).TrimEnd());
        }
        var notReconciled = report.Rows.Where(r => r.Error is null && !r.Reconciles).ToList();
        if (notReconciled.Count > 0)
        {
            lines.Add("");
            foreach (var row in notReconciled)
                lines.Add($"  {row.Person.Name}: doesn't reconcile: allowance less taken and booked is {Num(row.Allowance - row.Taken - row.Booked)}, "
                          + $"People First says {Num(row.Left)} left{(row.WaitingExplains ? " (it would if the days waiting for approval counted as used)" : "")}.");
        }
        lines.Add("");
        if (report.Year is { } shown)
            lines.Add($"Leave year {Long(shown.Start)} to {Long(shown.End)}; figures from People First.");
        else
            lines.Add("Figures from People First.");
        lines.Add($"Left to book is People First's own balance. Taken (to today) and booked (still to come) are approved {report.Type} days, "
                  + $"bank holidays included as used ({report.BankHolidays}); waiting for approval isn't in the balance.");
        if (report.FlagFrom is { } flag)
        {
            var flagged = report.Rows.Count(r => r.Flagged);
            lines.Add(report.FlagActive
                ? $"From {flag.ToString("d MMM", CultureInfo.InvariantCulture)}, more than {Num(report.FlagDays)} days left to book (less any waiting) is flagged: "
                  + $"{flagged} {(flagged == 1 ? "person" : "people")}."
                : $"More than {Num(report.FlagDays)} days left to book is flagged from {Long(flag)}.");
        }
        return string.Join("\n", lines);
    }

    /// <summary>The report as JSON, for the briefing and other tools.</summary>
    public static JsonObject Json(LeaveLeftReport report)
    {
        JsonNode? Day(DateOnly? day) => day is { } d ? JsonValue.Create(Iso(d)) : null;
        var people = new JsonArray();
        foreach (var row in report.Rows)
        {
            var item = new JsonObject { ["name"] = row.Person.Name };
            if (row.Error is not null)
            {
                item["error"] = row.Error;
                people.Add((JsonNode)item);
                continue;
            }
            item["year_start"] = Day(row.Year?.Start);
            item["year_end"] = Day(row.Year?.End);
            item["allowance"] = PyJson.Float(row.Allowance);
            item["taken"] = PyJson.Float(row.Taken);
            item["booked"] = PyJson.Float(row.Booked);
            item["waiting"] = PyJson.Float(row.Waiting);
            item["left"] = PyJson.Float(row.Left);
            item["next_year_booked"] = row.NextYearBooked is { } n ? PyJson.Float(n) : null;
            item["part_year"] = row.PartYear;
            item["overridden"] = row.Overridden;
            item["reconciles"] = row.Reconciles;
            item["difference"] = PyJson.Float(Math.Round(row.Difference, 2));
            item["waiting_explains"] = row.WaitingExplains;
            item["flagged"] = row.Flagged;
            people.Add((JsonNode)item);
        }
        return new JsonObject
        {
            ["today"] = Iso(report.Today),
            ["year_start"] = Day(report.Year?.Start),
            ["year_end"] = Day(report.Year?.End),
            ["current"] = report.Current,
            ["source"] = "People First",
            ["type"] = report.Type,
            ["bank_holidays"] = report.BankHolidays,
            ["flag"] = new JsonObject
            {
                ["from"] = Day(report.FlagFrom),
                ["days"] = PyJson.Float(report.FlagDays),
                ["active"] = report.FlagActive,
            },
            ["people"] = people,
        };
    }
}
