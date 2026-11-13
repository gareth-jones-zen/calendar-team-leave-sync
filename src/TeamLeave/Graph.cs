using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using M365Graph;
using PyCompat;

namespace TeamLeave;

/// <summary>The Graph calls the tool makes, so tests can give it recorded replies instead. Synchronous, as the
/// Python was: one call at a time.</summary>
public interface IGraph
{
    /// <summary>Every item across @odata.nextLink pages (GETs), the same headers on every page. Lazy, as Python's
    /// generator.</summary>
    IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string> scopes, IReadOnlyDictionary<string, string> headers);

    /// <summary>One request with a JSON body sent as Python's json.dumps(body) wrote it. Returns the reply ({} if
    /// empty). Throws GraphRequestException for a status outside 200-299.</summary>
    JsonObject Send(string method, string url, JsonObject? body, IReadOnlyList<string> scopes, IReadOnlyDictionary<string, string> headers);

    /// <summary>Make sure there's a token for the tool's scopes (sign-in).</summary>
    void Token();
}

/// <summary>A write was asked of something that may only read: a bug, never a decision. Nothing was sent.</summary>
public sealed class WriteRefusedException(string what) : Exception($"Refused a write ({what}): this run may only read.");

/// <summary>The fence under the write door: the planner reads the calendar only through this, which passes GETs
/// (pages) and refuses everything else. Only a Live write door holds the Graph underneath it.</summary>
public sealed class ReadOnlyGraph(IGraph inner) : IGraph
{
    public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string> scopes, IReadOnlyDictionary<string, string> headers) =>
        inner.Pages(url, scopes, headers);

    public JsonObject Send(string method, string url, JsonObject? body, IReadOnlyList<string> scopes, IReadOnlyDictionary<string, string> headers) =>
        method == "GET" ? inner.Send(method, url, body, scopes, headers) : throw new WriteRefusedException($"{method} {url}");

    public void Token() => inner.Token();
}

/// <summary>The real Graph, through background-tools-lib's M365Graph (the shared sign-in, timeouts, IPv4 first).
/// Reads retry as Python's did (429 and 5xx, and dropped connections). <b>Writes are sent as raw bytes, exactly
/// json.dumps(body)</b>, and a POST (a create) is never sent twice once it may have reached Graph: Python retried it
/// after a 5xx or a timeout, which could make a second entry. PATCH and DELETE are repeatable, so they retry as
/// Python's did.</summary>
public sealed class M365GraphClient(GraphClient client) : IGraph
{
    public const string SignInHint = Config.SignInHint;

    public GraphClient Client { get; } = client;

    /// <summary>client(config, interactive): the settings' sign-in, and a browser only when interactive says so (null:
    /// only if someone is at the console).</summary>
    public static M365GraphClient For(Config config, bool? interactive, Action<string>? log = null)
    {
        var options = new GraphClientOptions
        {
            Tenant = config.Tenant,
            LoginHint = config.LoginHint,
            Scopes = Calendar.Scopes,
            Interactive = interactive,
            SignInHint = SignInHint,
            Log = log,
        };
        if (config.MsalCache is not null)
            options = options with { CachePath = config.MsalCache };
        return new M365GraphClient(new GraphClient(options));
    }

    public IEnumerable<JsonObject> Pages(string url, IReadOnlyList<string> scopes, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var item in Client.PagesAsync(url, scopes, headers).ToBlockingEnumerable())
            if (item is JsonObject o)
                yield return o;
    }

    public JsonObject Send(string method, string url, JsonObject? body, IReadOnlyList<string> scopes, IReadOnlyDictionary<string, string> headers)
    {
        var bytes = body is null ? null : Encoding.UTF8.GetBytes(PyJson.Dump(body));
        var policy = method == "POST" ? GraphRetryPolicy.Write : GraphRetryPolicy.Read;
        var reply = Client.SendRawAsync(new HttpMethod(method), url, bytes, "application/json", policy, scopes, headers)
            .GetAwaiter().GetResult();
        if (!reply.IsSuccess)
            throw new GraphRequestException(reply.Status, method, url, Encoding.UTF8.GetString(reply.Body));
        if (reply.Body.Length == 0)
            return [];
        return JsonNode.Parse(reply.Body) as JsonObject ?? throw new JsonException($"Graph didn't return a JSON object for {url}.");
    }

    public void Token() => Client.TokenAsync().GetAwaiter().GetResult();
}

/// <summary>Every change to the calendar goes through one of these. Nothing else in the tool can write to it.</summary>
public interface ICalendarWriter
{
    /// <summary>Create an entry (the stamped payload; no attendees are added here). Returns its id.</summary>
    string Create(JsonObject payload);

    /// <summary>Rewrite one of team-leave's entries, or stamp a hand-made one (adoption).</summary>
    void Update(string eventId, JsonObject payload);

    void Delete(string eventId);
}

/// <summary>How a run may write: Live sends; Journal records what it would send and sends nothing; Refuse throws.
/// A dry run uses Refuse, so a bug that writes fails loudly.</summary>
public enum WriteMode
{
    Live,
    Journal,
    Refuse,
}

/// <summary>One intended Graph write: the same method, path, body and headers Live would send.</summary>
public sealed record WriteCall(string Method, string Url, JsonObject? Body)
{
    /// <summary>"METHOD url body", the body as json.dumps(body) (what goes on the wire).</summary>
    public string Text => Body is null ? $"{Method} {Url}" : $"{Method} {Url} {PyJson.Dump(Body)}";
}

/// <summary>The writer for one mode. Live writes through the given (unfenced) Graph; the others never touch it.</summary>
public sealed class WriteDoor
{
    private WriteDoor(WriteMode mode, ICalendarWriter writer, List<WriteCall> journal)
    {
        Mode = mode;
        Writer = writer;
        Journal = journal;
    }

    public WriteMode Mode { get; }

    public ICalendarWriter Writer { get; }

    /// <summary>What a Journal door was asked to write, in order (empty otherwise).</summary>
    public List<WriteCall> Journal { get; }

    public static WriteDoor Live(IGraph graph, string timeZone) =>
        new(WriteMode.Live, new GraphWrites((call, scopes, headers) => graph.Send(call.Method, call.Url, call.Body, scopes, headers), timeZone), []);

    public static WriteDoor Journaling(string timeZone, Action<WriteCall>? also = null)
    {
        var journal = new List<WriteCall>();
        return new WriteDoor(WriteMode.Journal, new GraphWrites((call, _, _) =>
        {
            journal.Add(call);
            also?.Invoke(call);
            return [];
        }, timeZone), journal);
    }

    public static WriteDoor Refusing(string timeZone) =>
        new(WriteMode.Refuse, new GraphWrites((call, _, _) => throw new WriteRefusedException($"{call.Method} {call.Url}"), timeZone), []);

    public static WriteDoor For(WriteMode mode, IGraph graph, string timeZone, Action<WriteCall>? journal = null) => mode switch
    {
        WriteMode.Live => Live(graph, timeZone),
        WriteMode.Journal => Journaling(timeZone, journal),
        _ => Refusing(timeZone),
    };
}

/// <summary>The Graph calls for each write, exactly as calendar.py made them, sent to a Graph or to a sink.</summary>
internal sealed class GraphWrites(Func<WriteCall, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, JsonObject> send, string timeZone)
    : ICalendarWriter
{
    private readonly Dictionary<string, string> headers = Calendar.Headers(timeZone);

    public string Create(JsonObject payload)
    {
        var body = (JsonObject)payload.DeepClone();
        body["attendees"] = new JsonArray();
        var reply = send(new WriteCall("POST", "/me/events", body), Calendar.Scopes, headers);
        return reply.TryGetPropertyValue("id", out var id) && id is not null ? Py.Str(id) : "";
    }

    public void Update(string eventId, JsonObject payload) =>
        send(new WriteCall("PATCH", $"/me/events/{eventId}", (JsonObject)payload.DeepClone()), Calendar.Scopes, headers);

    public void Delete(string eventId) =>
        send(new WriteCall("DELETE", $"/me/events/{eventId}", null), Calendar.Scopes, headers);
}
