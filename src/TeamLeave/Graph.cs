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
    /// empty). Throws GraphRequestException for a status outside 200-299, and GraphDeliveryUnknownException when a
    /// write that's sent once (a POST) may have been applied but wasn't confirmed (a 5xx, a timeout, a dropped
    /// connection): it isn't sent again.</summary>
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
/// Reads retry (429 and 5xx, and dropped connections). <b>Writes are sent as raw bytes, exactly json.dumps(body)</b>.
/// <b>A POST (a create) is sent once</b> (the library's default for a POST): only a 429 or a request that never
/// connected is sent again; a 5xx, a timeout or a connection dropped after sending throws
/// <see cref="GraphDeliveryUnknownException"/>, because a second try could make a second entry. <b>PATCH opts in to
/// retries</b> (<see cref="GraphRetryPolicy.Read"/>): it rewrites the owner's own entry, which has no attendees (its
/// body never carries any, and only entries without attendees are adopted), so it notifies nobody, and writing the
/// same fields twice leaves the same entry. DELETE retries as the library's default (a retry that finds it gone is
/// done).</summary>
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
        var verb = new HttpMethod(method);
        var policy = verb == HttpMethod.Patch ? GraphRetryPolicy.Read : GraphRetryPolicy.For(verb);
        var reply = Client.SendRawAsync(verb, url, bytes, "application/json", policy, scopes, headers)
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

    /// <summary>Rewrite one of calendar-team-leave-sync's entries, or stamp a hand-made one (adoption).</summary>
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

/// <summary>Every write to People First (an approval) goes through one of these, from the write door.</summary>
public interface IPeopleFirstWriter
{
    /// <summary>POST a body to a path under /api/v1, once. Live: People First's reply; Journal: null, nothing sent.</summary>
    JsonNode? Post(string path, JsonObject body);
}

/// <summary>The writer for one mode. Live writes through the given (unfenced) Graph, and to People First through the
/// People First it's given at the time; the others never touch either.</summary>
public sealed class WriteDoor
{
    /// <summary>How a People First write is named in the journal and in refusals ("POST people-first:/hrm/...").</summary>
    public const string PeopleFirstPrefix = "people-first:";

    private readonly Func<IPeopleFirstApi, IPeopleFirstWriter> peopleFirst;

    private WriteDoor(WriteMode mode, ICalendarWriter writer, List<WriteCall> journal, Func<IPeopleFirstApi, IPeopleFirstWriter> peopleFirst)
    {
        Mode = mode;
        Writer = writer;
        Journal = journal;
        this.peopleFirst = peopleFirst;
    }

    public WriteMode Mode { get; }

    public ICalendarWriter Writer { get; }

    /// <summary>What a Journal door was asked to write, in order (empty otherwise).</summary>
    public List<WriteCall> Journal { get; }

    /// <summary>The People First writer for this mode. Live posts through <paramref name="api"/> (the unfenced one);
    /// Journal records the call and sends nothing; Refuse throws.</summary>
    public IPeopleFirstWriter PeopleFirst(IPeopleFirstApi api) => peopleFirst(api);

    private static IPeopleFirstWriter LivePeopleFirst(IPeopleFirstApi api) => new PeopleFirstWrites(api.Post);

    private static WriteCall PeopleFirstCall(string path, JsonObject body) => new("POST", PeopleFirstPrefix + path, (JsonObject)body.DeepClone());

    private static readonly Func<IPeopleFirstApi, IPeopleFirstWriter> RefusePeopleFirst =
        _ => new PeopleFirstWrites((path, _) => throw new WriteRefusedException($"POST {PeopleFirstPrefix}{path}"));

    public static WriteDoor Live(IGraph graph, string timeZone) =>
        new(WriteMode.Live, new GraphWrites((call, scopes, headers) => graph.Send(call.Method, call.Url, call.Body, scopes, headers), timeZone), [],
            LivePeopleFirst);

    public static WriteDoor Journaling(string timeZone, Action<WriteCall>? also = null)
    {
        var journal = new List<WriteCall>();
        void Record(WriteCall call)
        {
            journal.Add(call);
            also?.Invoke(call);
        }
        return new WriteDoor(WriteMode.Journal, new GraphWrites((call, _, _) =>
        {
            Record(call);
            return [];
        }, timeZone), journal, _ => new PeopleFirstWrites((path, body) =>
        {
            Record(PeopleFirstCall(path, body));
            return null;
        }));
    }

    public static WriteDoor Refusing(string timeZone) =>
        new(WriteMode.Refuse, new GraphWrites((call, _, _) => throw new WriteRefusedException($"{call.Method} {call.Url}"), timeZone), [],
            RefusePeopleFirst);

    public static WriteDoor For(WriteMode mode, IGraph graph, string timeZone, Action<WriteCall>? journal = null) => mode switch
    {
        WriteMode.Live => Live(graph, timeZone),
        WriteMode.Journal => Journaling(timeZone, journal),
        _ => Refusing(timeZone),
    };

    /// <summary>A door for People First alone (approving by hand, which never touches the calendar): its calendar
    /// writer always refuses; People First writes follow the mode.</summary>
    public static WriteDoor PeopleFirstOnly(WriteMode mode, string timeZone, Action<WriteCall>? journal = null)
    {
        var refuse = Refusing(timeZone).Writer;
        switch (mode)
        {
            case WriteMode.Live:
                return new WriteDoor(WriteMode.Live, refuse, [], LivePeopleFirst);
            case WriteMode.Journal:
                var journaling = Journaling(timeZone, journal);
                return new WriteDoor(WriteMode.Journal, refuse, journaling.Journal, journaling.peopleFirst);
            default:
                return Refusing(timeZone);
        }
    }
}

/// <summary>People First writes, sent to People First or to a sink.</summary>
internal sealed class PeopleFirstWrites(Func<string, JsonObject, JsonNode?> send) : IPeopleFirstWriter
{
    public JsonNode? Post(string path, JsonObject body) => send(path, body);
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
