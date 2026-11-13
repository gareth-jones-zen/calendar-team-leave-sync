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

/// <summary>The settings file: whose leave, how it looks in your calendar, and when it runs. The same file the Python
/// version read, read the same way: paths come out as the same text, so the state file, log, locks and hook are shared.
///
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
///     time_zone = "Europe/London"
///     body = "Synced from People First by team-leave. Changes made here are overwritten."
///
///     [names]                                 # optional: People First's "First Last" -> the name to use
///     "Robert Example" = "Bob Example"
///
/// Paths may use ~ and %VARIABLES%; relative paths are relative to the settings file.</summary>
public sealed partial class Config
{
    public const string EnvConfig = "TEAM_LEAVE_CONFIG";
    public const string SignInHint = "At a console, run: team-leave sign-in";

    public const string Template = """
        # team-leave settings. See the README.
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
        TomlTable raw;
        try
        {
            var text = PyStr.ReadText(full);
            if (text.StartsWith((char)0xFEFF))
                throw new PyValueError("Invalid statement (at line 1, column 1)");   // tomllib refuses a byte order mark
            raw = TomlSerializer.Deserialize(text, TomlContext.Default.TomlTable) ?? [];
        }
        catch (Exception error) when (error is not (IOException or UnauthorizedAccessException or PyValueError))
        {
            throw new PyValueError($"{full}: {error.Message}");
        }
        return Parse(raw, full, env);
    }

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
            TimeZone = ev.TryGetValue("time_zone", out var zone) ? Str(zone, "time_zone") : "Europe/London",
            Body = ev.TryGetValue("body", out var body) ? Str(body, "body") : EventSettings.DefaultBody,
        };
        if (!SensitivityChoices.Contains(config.Event.Sensitivity))
            throw new PyValueError($"sensitivity under [event] in {full} should be one of {string.Join(", ", SensitivityChoices)}.");
        if (!ShowAsChoices.Contains(config.Event.ShowAs))
            throw new PyValueError($"show_as under [event] in {full} should be one of {string.Join(", ", ShowAsChoices)}.");
        return config;
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

[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal partial class TomlContext : Tomlyn.Serialization.TomlSerializerContext;
