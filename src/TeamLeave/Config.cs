using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PyCompat;
using Tomlyn;
using Tomlyn.Model;

namespace TeamLeave;

/// <summary>[titles]: how half days are marked ({title} is the type's title).</summary>
public sealed record Titles(string Am = "{title} (AM)", string Pm = "{title} (PM)", string FromPm = "{title} (from PM)",
    string ToAm = "{title} (to AM)");

/// <summary>[event]: the calendar entry itself.</summary>
public sealed class EventSettings
{
    public const string DefaultBody = "Synced from People First by team-leave. Changes made here are overwritten.";

    /// <summary>Kept as the JSON values Python would put in the payload (usually text), so the hash matches.</summary>
    public List<JsonNode?> Categories { get; set; } = [JsonValue.Create("Planned")];

    /// <summary>Minutes before; null: no reminder.</summary>
    public long? Reminder { get; set; }

    public string Sensitivity { get; set; } = "normal";
    public string ShowAs { get; set; } = "free";
    public string TimeZone { get; set; } = "Europe/London";
    public string Body { get; set; } = DefaultBody;
}

/// <summary>Dates when the rules never approve leave (first and last day, inclusive), and why: a cutover, say.</summary>
public sealed record ProtectedDates(DateOnly Start, DateOnly End, string Why);

/// <summary>[approvals]: routine leave requests approved by rules, and the rest raised with you. With no [approvals]
/// section the feature is off, and nothing more is read than without it.
/// <para>A request is approved only if every rule holds; otherwise it's raised with you, with every reason that applies.
/// Nothing is ever rejected.</para></summary>
public sealed class ApprovalSettings
{
    public static readonly string[] Keys =
        ["act", "max_per_run", "types", "min_in", "max_working_days", "notice_days", "short_notice_ok_days", "protected", "no_cover_needed", "notify"];

    /// <summary>False (the default): preview. It records and reports what it would approve, and approves nothing.</summary>
    public bool Act { get; init; }

    /// <summary>Never more approvals than this in one run.</summary>
    public int MaxPerRun { get; init; } = 5;

    /// <summary>People First absence types the rules may approve.</summary>
    public List<string> Types { get; init; } = ["Holiday"];

    /// <summary>At least this many of the team must be in on every working day the request covers.</summary>
    public int MinIn { get; init; } = 1;

    /// <summary>Longer than this many working days: asked.</summary>
    public int MaxWorkingDays { get; init; } = 10;

    /// <summary>Less notice than this (calendar days from today to the first day): asked, unless it's
    /// <see cref="ShortNoticeOkDays"/> working days or fewer.</summary>
    public int NoticeDays { get; init; } = 7;

    public int ShortNoticeOkDays { get; init; } = 2;

    /// <summary>Overlapping any of these: asked.</summary>
    public List<ProtectedDates> Protected { get; init; } = [];

    /// <summary>Days in these ranges need no one in (Christmas, say), so min_in isn't checked on them.</summary>
    public List<ProtectedDates> NoCoverNeeded { get; init; } = [];

    /// <summary>A Windows notification when a request needs you (once per request).</summary>
    public bool Notify { get; init; } = true;
}

/// <summary>The settings file: whose leave, how it looks in your calendar, and when it runs. The same file the Python
/// version read, read the same way: paths come out as the same text, so the state file, log, locks and hook are shared.
///
///     base = "base-config.toml"               # optional: settings shared with other tools
///     login_hint = "you@example.com"          # optional: your Microsoft sign-in
///     tenant = "organizations"                # optional: a tenant id pins sign-in to one organisation
///     msal_cache = "..."                      # optional: share another tool's sign-in
///     state_file = "..."                      # optional: failure counts and the last run (no names or dates)
///     log_file = "..."                        # optional
///     after_run = ["python", "hook.py"]       # optional: run after a sync that changed the calendar (JSON on stdin)
///     days_ahead = 365                        # optional: how far ahead to copy leave
///     max_deletes = 5                         # optional: more removals than this in one run stops it
///     every_minutes = 15                      # optional: autostart's interval (also at logon)
///     hours = "07:00-19:00"                   # optional: scheduled runs outside these hours do nothing
///     weekdays = true                         # optional: ...nor at weekends
///
///     [people_first]
///     url = "https://yourorg.people-first.com"
///     tenant_code = "yourorg"                 # optional: default, the first part of url
///     account = "you@example.com"             # optional: the account to pick at sign-in (default login_hint)
///     edge_profile = "..."                    # optional: Edge profile; share another tool's to share its sign-in
///     depth = 1                               # optional: levels of reports (1: direct reports only)
///     states = ["Approved"]                   # optional: which absences count (pending is "AwaitingApproval")
///
///     [types]                                 # optional: People First absence type -> calendar title
///     "Holiday" = "{name} annual leave"       # the default: annual leave only
///
///     [titles]                                # optional: how half days are marked ({title} is the type's title)
///     am = "{title} (AM)"
///     pm = "{title} (PM)"
///     from_pm = "{title} (from PM)"
///     to_am = "{title} (to AM)"
///
///     [event]                                 # optional: the calendar entry itself
///     categories = ["Planned"]
///     reminder = false                        # or minutes before, e.g. 1080 for 6pm the day before
///     sensitivity = "normal"                  # normal, personal, private or confidential
///     show_as = "free"                        # free, tentative, busy, oof or workingElsewhere
///     time_zone = "Europe/London"             # default: the top-level time_zone (from a base), else Europe/London
///     body = "Synced from People First by team-leave. Changes made here are overwritten."
///
///     [names]                                 # optional: People First's "First Last" -> the name to use
///     "Robert Example" = "Bob Example"
///
///     [approvals]                             # optional: approve routine leave requests by rules (no section: off)
///     act = false                             # false: preview, approving nothing; true: approve
///     max_per_run = 5                         # never more approvals than this in one run
///     types = ["Holiday"]                     # People First types the rules may approve
///     min_in = 1                              # at least this many of the team in on every working day it covers
///     max_working_days = 10                   # longer: ask
///     notice_days = 7                         # less notice than this: ask...
///     short_notice_ok_days = 2                # ...unless it's this many working days or fewer
///     protected = [ { start = "2027-02-08", end = "2027-02-09", why = "cutover" } ]   # overlapping: ask
///     no_cover_needed = [ { start = "2026-12-24", end = "2027-01-01", why = "Christmas" } ]   # min_in not checked
///     notify = true                           # a notification when a request needs you, once per request
///
///     [review]                                # optional: what changed, in a review queue (no section: off)
///     file = "%LOCALAPPDATA%/my-tools/review.json"
///     source = "team-leave"
///     lock = "my-tools-review"                # optional: default, the queue's folder and file name
///
/// Paths may use ~ and %VARIABLES%; relative paths are relative to the settings file.</summary>
public sealed partial class Config
{
    public const string EnvConfig = "TEAM_LEAVE_CONFIG";
    public const string SignInHint = "At a console, run: team-leave sign-in";

    public const string Template = """
        # team-leave settings. See the README.
        # base = "base-config.toml"
        # login_hint = "you@example.com"

        [people_first]
        url = "https://yourorg.people-first.com"

        """;

    public static readonly string[] ShowAsChoices = ["free", "tentative", "busy", "oof", "workingElsewhere"];
    public static readonly string[] SensitivityChoices = ["normal", "personal", "private", "confidential"];

    public required string Path { get; init; }
    public required PeopleFirstClient.Settings PeopleFirst { get; init; }
    public string? LoginHint { get; init; }
    public string Tenant { get; init; } = "organizations";
    public string? MsalCache { get; init; }
    public required string StateFile { get; set; }
    public required string LogFile { get; set; }
    public List<string> AfterRun { get; set; } = [];
    public int DaysAhead { get; set; } = 365;
    public long MaxDeletes { get; set; } = 5;
    public int EveryMinutes { get; set; } = 15;
    public (string From, string To) Hours { get; set; } = ("07:00", "19:00");
    public bool Weekdays { get; set; } = true;
    public int Depth { get; set; } = 1;

    /// <summary>The states that count, as Python compares them (by value: text, or a number).</summary>
    public List<JsonNode?> States { get; set; } = [JsonValue.Create("Approved")];

    /// <summary>People First absence type -> title template, in the settings' order.</summary>
    public List<KeyValuePair<string, string>> Types { get; set; } = [new("Holiday", "{name} annual leave")];

    public Titles Titles { get; set; } = new();
    public EventSettings Event { get; set; } = new();
    public Dictionary<string, string> Names { get; set; } = new(StringComparer.Ordinal);

    /// <summary>[approvals], or null: the feature is off.</summary>
    public ApprovalSettings? Approvals { get; set; }

    /// <summary>[review], or null: no review queue. Gets an item per change, when after_run would run.</summary>
    public ReviewQueue.ReviewSettings? Review { get; set; }

    public string? TypeTitle(string name)
    {
        foreach (var (key, value) in Types)
            if (key == name)
                return value;
        return null;
    }

    public static string? Environment(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('\0') ? null : System.Environment.GetEnvironmentVariable(name);

    /// <summary>%APPDATA%\team-leave\config.toml (or under the home folder if APPDATA isn't set).</summary>
    public static string DefaultPath(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var appData = env("APPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(appData) ? PyPath.Home(env) : appData, "team-leave", "config.toml"));
    }

    /// <summary>%LOCALAPPDATA%\team-leave: the default state, log and Edge profile.</summary>
    public static string LocalFolder(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "team-leave"));
    }

    private static string PathSetting(string value, string folder, Func<string, string?> env)
    {
        var expanded = PyPath.Pure(PyPath.ExpandVars(PyPath.ExpandUser(value, env), env));
        return PyPath.IsAbsolute(expanded) ? expanded : PyPath.Resolve(System.IO.Path.Combine(folder, expanded));
    }

    /// <summary>12 hex characters from the settings path, ignoring case (Python's str.lower()): the default state
    /// file's name.</summary>
    internal static string StateKey(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PyStr.Lower(path))))[..12];

    // Python's \d matches any Unicode digit; Task Scheduler's times are ASCII, and so is this check.
    [GeneratedRegex(@"\A([01][0-9]|2[0-3]):[0-5][0-9]\z", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();

    private static string Time(string value, string path) =>
        TimePattern().IsMatch(value) ? value : throw new PyValueError($"{Py.ReprString(value)} in {path} should be a time such as \"07:00\".");

    /// <summary>load(path): the given path, else TEAM_LEAVE_CONFIG, else the default. Throws FileNotFoundException if
    /// there's no file there, and PyValueError for settings that can't be used.</summary>
    public static Config Load(string? path = null, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var given = !string.IsNullOrEmpty(path) ? path : env(EnvConfig) is { Length: > 0 } fromEnv ? fromEnv : DefaultPath(env);
        var full = PyPath.Resolve(given);
        if (!File.Exists(full))
            throw new FileNotFoundException($"No settings file at {full}. Run `team-leave config` to create one.");
        SettingsWithBase read;
        try
        {
            var text = PyStr.ReadText(full);
            if (text.StartsWith((char)0xFEFF))
                throw new PyValueError("Invalid statement (at line 1, column 1)");   // tomllib refuses a byte order mark
            read = BaseSettings.Read(text, full, env, KnownKeys);
        }
        catch (FormatException error)   // the base it names: missing, unreadable, or not a base
        {
            throw new PyValueError(error.Message, error);
        }
        catch (Exception error) when (error is not (IOException or UnauthorizedAccessException or PyValueError))
        {
            throw new PyValueError($"{full}: {error.Message}");
        }
        return Parse(read.Raw, full, read.Env);   // paths expand with the base's [paths] first
    }

    /// <summary>Every top-level setting this tool reads: what a base file (base = "...") may supply. time_zone is the
    /// shared one, used when [event] doesn't set its own.</summary>
    public static readonly string[] KnownKeys =
    [
        "login_hint", "tenant", "msal_cache", "state_file", "log_file", "after_run", "days_ahead", "max_deletes",
        "every_minutes", "hours", "weekdays", "time_zone", "people_first", "types", "titles", "event", "names", "approvals",
        "review",
    ];

    /// <summary>parse(raw, path): the settings from a TOML table, checked as Python checks them.</summary>
    public static Config Parse(TomlTable raw, string full, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var folder = System.IO.Path.GetDirectoryName(full)!;
        var local = LocalFolder(env);
        var key = StateKey(full);
        var pf = raw.TryGetValue("people_first", out var pfValue) && Truthy(pfValue) ? pfValue as TomlTable ?? throw new PyValueError($"{full}: people_first must be a table.") : [];
        var url = PyStr.RStrip(pf.TryGetValue("url", out var u) ? Str(u, "url") : "", "/");
        if (!url.StartsWith("https://", StringComparison.Ordinal))
            throw new PyValueError($"[people_first] in {full} needs url, such as \"https://yourorg.people-first.com\".");
        var tenantCode = Truthy(pf, "tenant_code") ? Str(pf["tenant_code"], "tenant_code") : PeopleFirstClient.Settings.DefaultTenantCode(url);
        if (tenantCode.Length == 0)
            throw new PyValueError($"[people_first] in {full} needs tenant_code: it couldn't be worked out from the url.");
        var loginHint = raw.TryGetValue("login_hint", out var hint) && hint is not null ? Str(hint, "login_hint") : null;
        var account = Truthy(pf, "account") ? Str(pf["account"], "account") : Truthy(raw, "login_hint") ? loginHint! : "";
        var settings = new PeopleFirstClient.Settings(url, tenantCode, account,
            Truthy(pf, "edge_profile") ? PathSetting(Str(pf["edge_profile"], "edge_profile"), folder, env)
                                       : PyPath.Pure(System.IO.Path.Combine(local, "edge-profile")),
            SignInHint);

        var hours = (raw.TryGetValue("hours", out var h) ? Str(h, "hours") : "07:00-19:00").Split('-');
        if (hours.Length != 2)
            throw new PyValueError($"hours in {full} should look like \"07:00-19:00\".");
        var config = new Config
        {
            Path = full,
            PeopleFirst = settings,
            LoginHint = loginHint,
            Tenant = raw.TryGetValue("tenant", out var tenant) ? Str(tenant, "tenant") : "organizations",
            MsalCache = Truthy(raw, "msal_cache") ? PathSetting(Str(raw["msal_cache"], "msal_cache"), folder, env) : null,
            StateFile = Truthy(raw, "state_file") ? PathSetting(Str(raw["state_file"], "state_file"), folder, env)
                                                  : PyPath.Pure(System.IO.Path.Combine(local, $"state-{key}.json")),
            LogFile = Truthy(raw, "log_file") ? PathSetting(Str(raw["log_file"], "log_file"), folder, env)
                                              : PyPath.Pure(System.IO.Path.Combine(local, "team-leave.log")),
            AfterRun = raw.TryGetValue("after_run", out var hook) ? Strings(hook, "after_run") : [],
            DaysAhead = raw.TryGetValue("days_ahead", out var days) ? Int(days, "days_ahead") : 365,
            MaxDeletes = raw.TryGetValue("max_deletes", out var deletes) ? Int(deletes, "max_deletes") : 5,
            EveryMinutes = raw.TryGetValue("every_minutes", out var every) ? Int(every, "every_minutes") : 15,
            Hours = (Time(PyStr.Strip(hours[0]), full), Time(PyStr.Strip(hours[1]), full)),
            Weekdays = !raw.TryGetValue("weekdays", out var weekdays) || Truthy(weekdays),
            Depth = pf.TryGetValue("depth", out var depth) ? Int(depth, "depth") : 1,
            States = pf.TryGetValue("states", out var states) ? Items(states, "states") : [JsonValue.Create("Approved")],
            Names = raw.TryGetValue("names", out var names) ? NameTable(names) : new(StringComparer.Ordinal),
        };
        if (config.DaysAhead is < 1 or > 730)
            throw new PyValueError($"days_ahead in {full} should be between 1 and 730.");
        if (config.EveryMinutes < 1)
            throw new PyValueError($"every_minutes in {full} should be at least 1.");
        if (config.Depth < 1)
            throw new PyValueError($"depth under [people_first] in {full} should be at least 1.");

        if (raw.TryGetValue("types", out var types))
        {
            config.Types = types is TomlTable table
                ? table.Select(pair => new KeyValuePair<string, string>(pair.Key, Str(pair.Value, pair.Key))).ToList()
                : throw new PyValueError($"{full}: types must be a [types] table.");
        }
        if (config.Types.Count == 0)
            throw new PyValueError($"[types] in {full} is empty: name at least one People First absence type.");
        foreach (var (name, title) in config.Types)
            if (!title.Contains("{name}", StringComparison.Ordinal))
                throw new PyValueError($"The title for {Py.ReprString(name)} under [types] in {full} needs {{name}}.");

        var titles = raw.TryGetValue("titles", out var t) && Truthy(t) ? t as TomlTable ?? throw new PyValueError($"{full}: titles must be a table.") : [];
        string Title(string k, string fallback) => titles.TryGetValue(k, out var v) ? Str(v, k) : fallback;
        var defaults = new Titles();
        config.Titles = new Titles(Title("am", defaults.Am), Title("pm", defaults.Pm), Title("from_pm", defaults.FromPm), Title("to_am", defaults.ToAm));
        foreach (var (k, v) in new[] { ("am", config.Titles.Am), ("pm", config.Titles.Pm), ("from_pm", config.Titles.FromPm), ("to_am", config.Titles.ToAm) })
            if (!v.Contains("{title}", StringComparison.Ordinal))
                throw new PyValueError($"{k} under [titles] in {full} needs {{title}}.");

        var ev = raw.TryGetValue("event", out var e) && Truthy(e) ? e as TomlTable ?? throw new PyValueError($"{full}: event must be a table.") : [];
        var reminder = ev.TryGetValue("reminder", out var r) ? r : false;
        if (reminder is true || (reminder is not false && reminder is not long))
            throw new PyValueError($"reminder under [event] in {full} should be false, or minutes before (a number).");
        config.Event = new EventSettings
        {
            Categories = ev.TryGetValue("categories", out var categories) ? Items(categories, "categories") : [JsonValue.Create("Planned")],
            Reminder = reminder is long minutes ? minutes : null,
            Sensitivity = ev.TryGetValue("sensitivity", out var sensitivity) ? Str(sensitivity, "sensitivity") : "normal",
            ShowAs = ev.TryGetValue("show_as", out var showAs) ? Str(showAs, "show_as") : "free",
            TimeZone = ev.TryGetValue("time_zone", out var zone) ? Str(zone, "time_zone")
                : raw.TryGetValue("time_zone", out var shared) ? Str(shared, "time_zone") : "Europe/London",
            Body = ev.TryGetValue("body", out var body) ? Str(body, "body") : EventSettings.DefaultBody,
        };
        if (!SensitivityChoices.Contains(config.Event.Sensitivity))
            throw new PyValueError($"sensitivity under [event] in {full} should be one of {string.Join(", ", SensitivityChoices)}.");
        if (!ShowAsChoices.Contains(config.Event.ShowAs))
            throw new PyValueError($"show_as under [event] in {full} should be one of {string.Join(", ", ShowAsChoices)}.");
        if (raw.TryGetValue("approvals", out var approvals))
            config.Approvals = ParseApprovals(approvals, full);
        if (raw.TryGetValue("review", out var review))
        {
            try
            {
                config.Review = ReviewQueue.ReviewSettings.Read(review, full, file => PathSetting(file, folder, env));
            }
            catch (FormatException error)
            {
                throw new PyValueError(error.Message);
            }
        }
        return config;
    }

    /// <summary>[approvals], checked strictly: it approves leave, so a misspelt key, "false" in quotes or a number that
    /// isn't whole is an error rather than a guess.</summary>
    private static ApprovalSettings ParseApprovals(object? value, string full)
    {
        if (value is not TomlTable table)
            throw new PyValueError($"{full}: approvals must be an [approvals] table.");
        foreach (var key in table.Keys)
            if (!ApprovalSettings.Keys.Contains(key))
                throw new PyValueError($"{key} under [approvals] in {full} isn't a setting team-leave knows (it knows "
                                       + $"{string.Join(", ", ApprovalSettings.Keys)}).");
        var defaults = new ApprovalSettings();
        bool Flag(string key, bool fallback) => !table.TryGetValue(key, out var v) ? fallback
            : v is bool b ? b : throw new PyValueError($"{key} under [approvals] in {full} should be true or false (no quotes).");
        int Whole(string key, int fallback, int least) => !table.TryGetValue(key, out var v) ? fallback
            : v is long l && l >= least && l <= int.MaxValue ? (int)l
            : throw new PyValueError($"{key} under [approvals] in {full} should be a whole number, at least {least}.");
        var types = defaults.Types;
        if (table.TryGetValue("types", out var t))
        {
            types = t is TomlArray array && array.Count > 0 && array.All(item => item is string { Length: > 0 })
                ? array.Cast<string>().ToList()
                : throw new PyValueError($"types under [approvals] in {full} should be a list of People First absence types, as [\"Holiday\"].");
        }
        var settings = new ApprovalSettings
        {
            Act = Flag("act", defaults.Act),
            MaxPerRun = Whole("max_per_run", defaults.MaxPerRun, 1),
            Types = types,
            MinIn = Whole("min_in", defaults.MinIn, 0),
            MaxWorkingDays = Whole("max_working_days", defaults.MaxWorkingDays, 1),
            NoticeDays = Whole("notice_days", defaults.NoticeDays, 0),
            ShortNoticeOkDays = Whole("short_notice_ok_days", defaults.ShortNoticeOkDays, 0),
            Protected = table.TryGetValue("protected", out var p) ? ProtectedList(p, full, "protected") : [],
            NoCoverNeeded = table.TryGetValue("no_cover_needed", out var nc) ? ProtectedList(nc, full, "no_cover_needed") : [],
            Notify = Flag("notify", defaults.Notify),
        };
        return settings;
    }

    private static List<ProtectedDates> ProtectedList(object? value, string full, string name)
    {
        IEnumerable<object?> items = value switch
        {
            TomlArray array => array,
            TomlTableArray tables => tables,
            _ => throw new PyValueError($"{name} under [approvals] in {full} should be a list, as "
                                        + "[ { start = \"2027-02-08\", end = \"2027-02-09\", why = \"cutover\" } ]."),
        };
        var output = new List<ProtectedDates>();
        foreach (var item in items)
        {
            if (item is not TomlTable range)
                throw new PyValueError($"Each of {name} under [approvals] in {full} should be {{ start = ..., end = ..., why = ... }}.");
            foreach (var key in range.Keys)
                if (key is not ("start" or "end" or "why"))
                    throw new PyValueError($"{key} in {name} under [approvals] in {full} isn't start, end or why.");
            DateOnly Day(string key) => range.TryGetValue(key, out var v) ? v switch
            {
                string s when DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d,
                TomlDateTime { Kind: TomlDateTimeKind.LocalDate } d => new DateOnly(d.DateTime.Year, d.DateTime.Month, d.DateTime.Day),
                _ => throw new PyValueError($"{key} in {name} under [approvals] in {full} should be a date, as \"2027-02-08\"."),
            } : throw new PyValueError($"Each of {name} under [approvals] in {full} needs {key}.");
            var start = Day("start");
            var end = Day("end");
            if (end < start)
                throw new PyValueError($"A {name} range under [approvals] in {full} ends before it starts.");
            var why = range.TryGetValue("why", out var w) ? w as string ?? throw new PyValueError($"why in {name} under [approvals] in {full} must be text.") : "";
            output.Add(new ProtectedDates(start, end, why));
        }
        return output;
    }

    /// <summary>create_default(): the default settings file, from the template if it isn't there yet.</summary>
    public static string CreateDefault(Func<string, string?>? env = null)
    {
        var path = DefaultPath(env);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            PyStr.WriteText(path, Template.ReplaceLineEndings("\n"));
        }
        return path;
    }

    private static Dictionary<string, string> NameTable(object? value)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (value is TomlTable table)
            foreach (var (k, v) in table)
                names[k] = Str(v, k);
        else if (Truthy(value))
            throw new PyValueError("names must be a [names] table.");
        return names;
    }

    /// <summary>Python's truthiness of a setting that's there (bool(raw.get(key))).</summary>
    private static bool Truthy(TomlTable table, string key) => table.TryGetValue(key, out var value) && Truthy(value);

    private static bool Truthy(object? value) => value switch
    {
        null => false,
        string s => s.Length > 0,
        bool b => b,
        long l => l != 0,
        double d => d != 0,
        TomlArray a => a.Count > 0,
        TomlTableArray a => a.Count > 0,
        TomlTable t => t.Count > 0,
        _ => true,
    };

    /// <summary>Python's str() of a setting, for the settings that are text.</summary>
    private static string Str(object? value, string key) => value switch
    {
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "True" : "False",
        double d => Py.Repr(d),
        _ => throw new PyValueError($"{key} must be text."),
    };

    /// <summary>Python's int() of a setting: whole numbers, decimals truncated, or digits in text.</summary>
    private static int Int(object? value, string key) => value switch
    {
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        double d when double.IsFinite(d) && Math.Abs(d) < int.MaxValue => (int)Math.Truncate(d),
        bool b => b ? 1 : 0,
        string s when int.TryParse(PyStr.Strip(s).Replace("_", "", StringComparison.Ordinal), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => throw new PyValueError($"{key} must be a whole number."),
    };

    private static List<string> Strings(object? value, string key) => value switch
    {
        TomlArray array => array.Select(item => Str(item, key)).ToList(),
        _ => throw new PyValueError($"{key} must be a list, as [\"python\", \"hook.py\"]."),
    };

    /// <summary>list(value) for a list of plain values (text, numbers, true/false), kept as JSON values; Python's
    /// list() of text is its characters, so that's what text gives.</summary>
    private static List<JsonNode?> Items(object? value, string key) => value switch
    {
        TomlArray array => array.Select(item => Node(item, key)).ToList(),
        string s => PyBits.Characters(s).Select(c => (JsonNode?)JsonValue.Create(c)).ToList(),
        _ => throw new PyValueError($"{key} must be a list, as [\"Planned\"]."),
    };

    private static JsonNode? Node(object? value, string key) => value switch
    {
        string s => JsonValue.Create(s),
        long l => PyJson.Int(l),
        bool b => JsonValue.Create(b),
        double d => PyJson.Float(d),
        _ => throw new PyValueError($"{key} must hold text or numbers."),
    };
}
