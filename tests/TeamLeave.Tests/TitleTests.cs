namespace TeamLeave.Tests;

/// <summary>Titles come from background-tools-lib's ReportLeaveTitles since 2.0.0 (22 Nov 2026), so mail-triage reads
/// back exactly what this tool writes. These prove they are byte for byte what 1.9.0 wrote: an entry's hash includes
/// its title, so any difference would rewrite every entry.</summary>
public sealed class TitleTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-team-leave-sync-titles-" + Guid.NewGuid().ToString("N"));

    public TitleTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private Config Load(string titles)
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, "[people_first]\nurl = \"https://example.people-first.com\"\n" + titles);
        return Config.Load(path, name => name switch
        {
            "LOCALAPPDATA" => Path.Combine(dir, "local"),
            "APPDATA" => Path.Combine(dir, "roaming"),
            _ => null,
        });
    }

    /// <summary>1.9.0's Absence.Halves and Absence.Title, verbatim, as the reference.</summary>
    private static string Legacy(Absence a, Config config)
    {
        (string? Single, string? First, string? Last) halves;
        if (a.Start == a.End)
        {
            var parts = new HashSet<string>(StringComparer.Ordinal) { a.StartPart, a.EndPart };
            parts.Remove(Leave.Full);
            halves = (parts.Count == 1 ? parts.First() : null, null, null);
        }
        else
            halves = (null, a.StartPart == Leave.Pm ? Leave.Pm : null, a.EndPart == Leave.Am ? Leave.Am : null);

        var title = PyBits.Format(a.Template, ("name", a.Person.Name));
        var (single, first, last) = halves;
        var t = config.Titles;
        if (single == Leave.Am)
            title = PyBits.Format(t.Am, ("title", title));
        else if (single == Leave.Pm)
            title = PyBits.Format(t.Pm, ("title", title));
        if (first is not null)
            title = PyBits.Format(t.FromPm, ("title", title));
        if (last is not null)
            title = PyBits.Format(t.ToAm, ("title", title));
        return title;
    }

    public static IEnumerable<object[]> TitleSettings() =>
    [
        [""],
        ["[titles]\nam = \"{title} - morning\"\npm = \"{title} - afternoon\"\nfrom_pm = \"{title} [from the afternoon]\"\nto_am = \"{title} [until midday]\"\n"],
        ["[titles]\nam = \"{{AM}} {title}\"\npm = \"{title} {{PM}}\"\nfrom_pm = \"{{from}} {title}\"\nto_am = \"{title} {{to}}\"\n"],
    ];

    [Theory]
    [MemberData(nameof(TitleSettings))]
    public void Every_title_is_what_1_9_0_wrote(string titles)
    {
        var config = Load(titles);
        string[] parts = [Leave.Full, Leave.Am, Leave.Pm];
        string[] templates = ["{name} annual leave", "{name} on leave", "{name} leave", "Leave: {name} {{private}}", "{{{name}}} away"];
        var people = new[] { new Person("1", "Dan Clark"), new Person("2", "Fahad Sarwar"), new Person("3", "Jo O'Brien-Smith") };
        var day = new DateOnly(2026, 12, 21);
        var checkedCount = 0;
        foreach (var template in templates)
            foreach (var person in people)
                foreach (var end in new[] { day, day.AddDays(1), day.AddDays(4) })
                    foreach (var startPart in parts)
                        foreach (var endPart in parts)
                        {
                            var absence = new Absence("id", person, day, end, startPart, endPart, template);
                            Assert.Equal(Legacy(absence, config), absence.Title(config));
                            checkedCount++;
                        }
        Assert.Equal(templates.Length * people.Length * 3 * 9, checkedCount);
    }

    [Fact]
    public void A_template_the_library_cannot_fill_is_a_reported_settings_error()
    {
        var config = Load("");
        var absence = new Absence("id", new Person("1", "Dan Clark"), new DateOnly(2026, 12, 21), new DateOnly(2026, 12, 21),
            Leave.Full, Leave.Full, "{nme} annual leave");
        Assert.Throws<PyValueError>(() => absence.Title(config));
    }

    [Fact]
    public void What_it_writes_mail_triage_reads_back()
    {
        var config = Load("");
        var day = new DateOnly(2026, 12, 21);
        var dan = new Person("1", "Dan Clark");
        var cases = new (Absence Absence, string? Each, string? First, string? Last)[]
        {
            (new Absence("a", dan, day, day, Leave.Full, Leave.Full, "{name} annual leave"), null, null, null),
            (new Absence("b", dan, day, day, Leave.Am, Leave.Am, "{name} annual leave"), "AM", null, null),
            (new Absence("c", dan, day, day, Leave.Pm, Leave.Pm, "{name} annual leave"), "PM", null, null),
            (new Absence("d", dan, day, day.AddDays(2), Leave.Pm, Leave.Am, "{name} annual leave"), null, "PM", "AM"),
        };
        foreach (var (absence, each, first, last) in cases)
        {
            var read = CalendarDays.ReportLeaveTitles.Parse(absence.Title(config), dan.Name);
            Assert.NotNull(read);
            Assert.Equal((each, first, last), (read!.Each, read.First, read.Last));
        }
    }
}
