using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PyCompat;

namespace TeamLeave;

/// <summary>People First's daily "Today's notifications" email, checked against what team-leave has seen (1.4.0,
/// 18 Nov 2026: in the EA, tools/handlers/mail-triage/people-first-digest.py until then, which read this tool's state
/// file and matched by dates alone).
///
/// <para><b>What team-leave has seen</b> is kept in its state file as "seen": each leave request waiting for approval
/// and each approved absence a sync read, for 30 days after it was last read. Kept because a request stops waiting once
/// it's approved, so the approvals record alone forgets it before the digest arrives. <b>Only a fingerprint of who and
/// when is kept</b> (the first 16 hex characters of a SHA-256 of the name and dates), never the name or the dates, as
/// the state file never holds them; digest makes the same fingerprint from the email.</para>
///
/// <para><b>digest</b> reads the email as JSON on standard input (a mail-triage handler's input) and answers with an
/// exit code: 0 when every notification is a leave request team-leave has seen (by person and dates) or someone liking
/// a post (1.6.0), so the email can go; 75 when a request isn't seen yet (try again later: the next sync may read it); 1 for anything else (an HR action,
/// a cancellation, a count that doesn't add up), so the email is raised. It reads only the email and the state file, and
/// changes nothing.</para></summary>
public static partial class Digest
{
    public const int TryAgain = 75;
    public const int KeepDays = 30;

    [GeneratedRegex(@"^(?<name>.+?)\s*(?:\([^)]*\))?\s+has requested leave for\s+(?<start>\d{1,2} \w{3,9} \d{4})(?:\s+[AP]M)?(?:\s*-\s*(?<end>\d{1,2} \w{3,9} \d{4})(?:\s+[AP]M)?)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Request();   // a half day carries AM or PM after its date (1.6.0: "17 Dec 2026 PM - 18 Dec
                                              // 2026" was read as 17 Dec alone, so it never matched what a sync had seen)

    /// <summary>Someone liking a post ("Lindsey Gibson liked: \"Happy Anniversary\""): People First's social notices,
    /// noise in a digest (GJ, 19 Nov 2026: 1.6.0). Counted, so the unread total still adds up, and otherwise ignored.</summary>
    [GeneratedRegex(@"^\S.*?\sliked:\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Like();

    [GeneratedRegex(@"^(\d+)\s+unread notifications?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Unread();

    /// <summary>A link in the email's text: "[...]" (an HTML-to-text conversion), "<https://...>" (Graph's text body,
    /// 1.4.1: it was glued to the name, so no request matched) or a bare address.</summary>
    [GeneratedRegex(@"\[[^\]]*\]|<[^>]*>|https?://\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    /// <summary>Remembers what a sync read: who and when, for each waiting request and each approved absence. Entries
    /// not read for KeepDays go.</summary>
    public static void Note(JsonObject state, IEnumerable<(Person Person, DateOnly Start, DateOnly End, string Kind)> read, DateTime now)
    {
        var seen = state["seen"] as JsonObject ?? [];
        var stamp = now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        foreach (var (person, start, end, kind) in read)
            seen[Fingerprint(person.Name, start, end)] = new JsonObject { ["kind"] = kind, ["last"] = stamp };
        var cutoff = now.AddDays(-KeepDays).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        foreach (var (key, node) in seen.ToList())
            if (node is not JsonObject e || string.CompareOrdinal(e["last"]?.ToString() ?? "", cutoff) < 0)
                seen.Remove(key);
        state["seen"] = seen;
    }

    /// <summary>Who and when, as a fingerprint: the name (ignoring case and spacing) and the dates, hashed.</summary>
    public static string Fingerprint(string name, DateOnly start, DateOnly end) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{Spaces().Replace(name.Trim(), " ").ToLowerInvariant()}|{PyTime.Iso(start)}|{PyTime.Iso(end)}")))[..16];

    /// <summary>A digest's leave requests: (name, start, end), how many likes it holds, and how many notifications it
    /// says it holds (null if it doesn't say). Lines lose their [link] references and runs of spaces first.</summary>
    public static (List<(string Name, DateOnly Start, DateOnly End)> Requests, int Likes, int? Unread) Read(string body)
    {
        var requests = new List<(string, DateOnly, DateOnly)>();
        var likes = 0;
        int? unread = null;
        foreach (var raw in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = Spaces().Replace(Link().Replace(raw, " ").Trim(), " ");
            if (line.Length == 0)
                continue;
            if (Unread().Match(line) is { Success: true } u)
                unread = int.Parse(u.Groups[1].Value, CultureInfo.InvariantCulture);
            else if (Request().Match(line) is { Success: true } m)
            {
                var start = Day(m.Groups["start"].Value);
                requests.Add((m.Groups["name"].Value.Trim(), start, m.Groups["end"].Success ? Day(m.Groups["end"].Value) : start));
            }
            else if (Like().IsMatch(line))
                likes++;
        }
        return (requests, likes, unread);
    }

    private static DateOnly Day(string text) =>
        DateOnly.TryParseExact(text, ["d MMM yyyy", "d MMMM yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day : throw new FormatException($"'{text}' isn't a date");

    /// <summary>The names a digest's name could be seen as: itself, and its [names] override (People First's formal
    /// name to the one the calendar uses), compared ignoring case and spacing.</summary>
    private static HashSet<string> Names(Config config, string name)
    {
        string Norm(string s) => Spaces().Replace(s.Trim(), " ").ToLowerInvariant();
        // The name, and (1.4.1) the name without each leading word in turn down to two words, in case anything is still
        // glued to its front; the dates have to match too, so a shorter name can't stand in for someone else's leave.
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tries = Enumerable.Range(0, Math.Max(1, words.Length - 1)).Select(skip => string.Join(' ', words.Skip(skip))).ToList();
        var names = new HashSet<string>(tries, StringComparer.Ordinal);
        foreach (var t in tries)
            foreach (var (formal, used) in config.Names)
                if (Norm(formal) == Norm(t))
                    names.Add(used);
        return names;
    }

    /// <summary>The check: (exit code, the one-line note mail-triage records).</summary>
    public static (int Code, string Note) Check(Config config, JsonObject state, string email)
    {
        JsonObject input;
        try
        {
            input = JsonNode.Parse(email) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException error)
        {
            return (1, $"the input isn't the email as JSON: {error.Message}");
        }
        List<(string Name, DateOnly Start, DateOnly End)> requests;
        int likes;
        int? unread;
        try
        {
            (requests, likes, unread) = Read(input["body"]?.ToString() ?? "");
        }
        catch (FormatException error)
        {
            return (1, $"couldn't read a date in it ({error.Message}): raised as usual");
        }
        var liked = likes switch { 0 => "", 1 => ", and 1 like", _ => $", and {likes} likes" };
        if (requests.Count == 0 && likes == 0)
            return (1, "not only leave requests: raised as usual");
        if (unread is { } count && count != requests.Count + likes)
            return (1, $"{count} notifications, {requests.Count} of them leave requests{liked}: raised as usual");
        if (requests.Count == 0)
            return (0, $"only likes ({likes})");
        var seen = state["seen"] as JsonObject ?? [];
        var kinds = new SortedSet<string>(StringComparer.Ordinal);
        var missing = 0;
        foreach (var (name, start, end) in requests)
        {
            var match = Names(config, name).Select(n => seen[Fingerprint(n, start, end)]).OfType<JsonObject>().FirstOrDefault();
            if (match is null)
                missing++;
            else
                kinds.Add(match["kind"]?.ToString() ?? "");
        }
        if (missing > 0)
            return (TryAgain, $"{missing} of {requests.Count} leave request(s) not yet seen by team-leave: will check again");
        return (0, $"{requests.Count} leave request(s), all seen by team-leave ({string.Join(", ", kinds)}){liked}");
    }
}
