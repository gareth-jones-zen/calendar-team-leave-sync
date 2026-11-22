using PyCompat;

namespace TeamLeave.Tests;

/// <summary>base = "...": settings shared with other tools, from a base file beside the settings.</summary>
public sealed class BaseSettingsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-team-leave-sync-base-" + Guid.NewGuid().ToString("N"));

    private const string Base = """
        login_hint = "base@example.com"
        tenant = "contoso"
        msal_cache = "%SHARED%/msal-cache.json"
        time_zone = "GMT Standard Time"

        [paths]
        SHARED = "%LOCALAPPDATA%/shared"
        EDGE_PEOPLE_FIRST = "%SHARED%/people-first-edge"

        [git]
        repo = ".."

        [review]
        file = "%SHARED%/pending.json"
        lock = "shared-pending"
        """;

    private const string Own = """
        base = "base-config.toml"
        [people_first]
        url = "https://example.people-first.com"
        edge_profile = "%EDGE_PEOPLE_FIRST%"
        """;

    public BaseSettingsTests()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "base-config.toml"), Base);
    }

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

    private string? Env(string name) => name switch
    {
        "LOCALAPPDATA" => Path.Combine(dir, "local"),
        "APPDATA" => Path.Combine(dir, "roaming"),
        _ => null,
    };

    private Config Load(string text)
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, text);
        return Config.Load(path, Env);
    }

    private string Local(string rest) => PyPath.Pure(Path.Combine(dir, "local") + "/" + rest);

    [Fact]
    public void Sign_in_settings_paths_and_time_zone_come_from_the_base()
    {
        var config = Load(Own);
        Assert.Equal("base@example.com", config.LoginHint);
        Assert.Equal("contoso", config.Tenant);
        Assert.Equal(Local("shared/msal-cache.json"), config.MsalCache);
        Assert.Equal(Local("shared/people-first-edge"), config.PeopleFirst.EdgeProfile);
        Assert.Equal("base@example.com", config.PeopleFirst.Account);   // the account defaults to login_hint
        Assert.Equal("GMT Standard Time", config.Event.TimeZone);
        Assert.Null(config.Review);   // no [review] here, so the base's doesn't apply
    }

    [Fact]
    public void The_tools_own_settings_win()
    {
        var config = Load("login_hint = \"own@example.com\"\ntenant = \"own\"\nlog_file = \"%SHARED%/own.log\"\n" + Own
                          + "\n[event]\ntime_zone = \"Europe/London\"\n");
        Assert.Equal("own@example.com", config.LoginHint);
        Assert.Equal("own", config.Tenant);
        Assert.Equal(Local("shared/own.log"), config.LogFile);
        Assert.Equal("Europe/London", config.Event.TimeZone);
    }

    [Fact]
    public void Without_a_base_the_time_zone_is_as_before()
    {
        Assert.Equal("Europe/London", Load("[people_first]\nurl = \"https://example.people-first.com\"\n").Event.TimeZone);
    }

    [Fact]
    public void Review_applies_only_where_the_tool_has_the_section()
    {
        var config = Load(Own + "\n[review]\nsource = \"team-leave\"\nlock = \"mine\"\n");
        Assert.Equal(Local("shared/pending.json"), config.Review!.File);
        Assert.Equal("team-leave", config.Review.Source);
        Assert.Equal("mine", config.Review.Lock);
    }

    [Fact]
    public void The_long_range_look_ahead_comes_from_the_base_when_days_ahead_is_not_set()
    {
        BaseWithLongRange(6);
        var config = Load(Own);
        Assert.Equal(6, config.MonthsAhead);
        Assert.Equal(new DateOnly(2027, 2, 28), config.WindowEnd(new DateOnly(2026, 8, 31)));   // calendar months
    }

    [Fact]
    public void Days_ahead_wins_over_the_base_long_range_months()
    {
        BaseWithLongRange(6);
        var config = Load("days_ahead = 30\n" + Own);
        Assert.Null(config.MonthsAhead);
        Assert.Equal(new DateOnly(2026, 12, 22), config.WindowEnd(new DateOnly(2026, 11, 22)));
    }

    [Fact]
    public void Without_either_the_window_is_365_days()
    {
        var config = Load(Own);
        Assert.Null(config.MonthsAhead);
        Assert.Equal(new DateOnly(2027, 11, 22), config.WindowEnd(new DateOnly(2026, 11, 22)));
    }

    [Fact]
    public void A_long_range_months_value_out_of_range_is_a_settings_error()
    {
        var error = Assert.Throws<PyValueError>(() => Load("long_range_months_ahead = 0\n" + Own));
        Assert.Contains("long_range_months_ahead", error.Message);
    }

    private void BaseWithLongRange(int months) =>
        File.WriteAllText(Path.Combine(dir, "base-config.toml"), $"long_range_months_ahead = {months}\n" + Base);

    [Fact]
    public void A_missing_base_is_a_settings_error()
    {
        var error = Assert.Throws<PyValueError>(() => Load("base = \"nowhere.toml\"\n[people_first]\nurl = \"https://example.people-first.com\"\n"));
        Assert.Contains("doesn't exist", error.Message);
    }
}
