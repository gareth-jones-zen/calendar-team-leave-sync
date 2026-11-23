using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using M365Graph;
using PyCompat;
using static TeamLeave.Tests.Fixtures;

namespace TeamLeave.Tests;

/// <summary>A write that may have reached Microsoft is never resent, through the real Graph adapter over a fake
/// handler: a create that gets a 5xx or no answer is sent once and stops the run as "unconfirmed", and the next run's
/// read of the calendar finds it rather than making it again. Reads, PATCHes (opted in) and DELETEs still retry.</summary>
public sealed class SendOnceTests
{
    private static readonly IReadOnlyDictionary<string, string> Headers = Calendar.Headers("Europe/London");

    /// <summary>Answers GETs from a table by path (an empty page if not there), and everything else from a queue.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly List<string> Seen = [];
        public readonly Queue<Func<HttpResponseMessage>> Answers = new();
        public readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> Gets = new(StringComparer.Ordinal);

        public static HttpResponseMessage Json(HttpStatusCode status, string body = "{}") =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        public void Reply(HttpStatusCode status, string body = "{}") => Answers.Enqueue(() => Json(status, body));

        public void Fail(Exception error) => Answers.Enqueue(() => throw error);

        public void Get(string path, params Func<HttpResponseMessage>[] answers)
        {
            if (!Gets.TryGetValue(path, out var queue))
                Gets[path] = queue = new();
            foreach (var answer in answers)
                queue.Enqueue(answer);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var path = request.RequestUri!.AbsolutePath.Replace("/v1.0", "", StringComparison.Ordinal);
            Seen.Add($"{request.Method} {path}");
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Gets.TryGetValue(path, out var queue) && queue.Count > 0 ? queue.Dequeue()() : Json(HttpStatusCode.OK, "{\"value\": []}"));
            if (Answers.Count == 0)
                throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
            return Task.FromResult(Answers.Dequeue()());
        }
    }

    private static M365GraphClient Graph(FakeHandler http, List<TimeSpan>? waits = null) =>
        new(new GraphClient(new GraphClientOptions { Log = _ => { } }, http, token: (_, _) => Task.FromResult("t"),
            delay: (wait, _) =>
            {
                waits?.Add(wait);
                return Task.CompletedTask;
            }));

    private static ICalendarWriter Writer(FakeHandler http) => WriteDoor.Live(Graph(http), "Europe/London").Writer;

    private static JsonObject Payload() => new() { ["subject"] = "Jordan Lee annual leave", ["isAllDay"] = true };

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void a_create_that_gets_a_5xx_is_sent_once_and_unconfirmed(HttpStatusCode status)
    {
        var http = new FakeHandler();
        http.Reply(status, "{\"error\": \"busy\"}");
        var error = Assert.Throws<GraphDeliveryUnknownException>(() => Writer(http).Create(Payload()));
        Assert.Equal(["POST /me/events"], http.Seen);
        Assert.Equal(status, error.Status);
        Assert.Equal("unconfirmed", Runner.Problem(error));
    }

    [Fact]
    public void a_create_that_times_out_or_drops_after_sending_is_sent_once_and_unconfirmed()
    {
        foreach (var failure in new Exception[]
                 {
                     new TaskCanceledException("timed out", new TimeoutException()),
                     new HttpRequestException("reset", new IOException("forcibly closed")),
                 })
        {
            var http = new FakeHandler();
            http.Fail(failure);
            var error = Assert.Throws<GraphDeliveryUnknownException>(() => Writer(http).Create(Payload()));
            Assert.Equal(["POST /me/events"], http.Seen);
            Assert.Null(error.Status);
            Assert.Equal("unconfirmed", Runner.Problem(error));
        }
    }

    [Fact]
    public void a_create_that_never_connected_or_was_throttled_is_sent_again()
    {
        var http = new FakeHandler();
        http.Fail(new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        http.Answers.Enqueue(() =>
        {
            var throttled = FakeHandler.Json(HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return throttled;
        });
        http.Reply(HttpStatusCode.Created, "{\"id\": \"E9\"}");
        Assert.Equal("E9", Writer(http).Create(Payload()));
        Assert.Equal(3, http.Seen.Count);
    }

    [Fact]
    public void a_create_refused_with_a_4xx_is_a_failure_not_an_unknown()
    {
        var http = new FakeHandler();
        http.Reply(HttpStatusCode.BadRequest, "{\"error\": \"no\"}");
        var error = Assert.Throws<GraphRequestException>(() => Writer(http).Create(Payload()));
        Assert.Single(http.Seen);
        Assert.Equal("failure", Runner.Problem(error));
    }

    [Fact]
    public void a_read_still_retries_a_5xx_and_a_dropped_connection()
    {
        var http = new FakeHandler();
        http.Get("/me/events",
            () => FakeHandler.Json(HttpStatusCode.BadGateway),
            () => throw new TaskCanceledException("timed out", new TimeoutException()),
            () => FakeHandler.Json(HttpStatusCode.OK, "{\"value\": [{\"id\": \"E1\"}]}"));
        var items = Graph(http).Pages("/me/events", Calendar.Scopes, Headers).ToList();
        Assert.Equal("E1", Py.Str(items.Single()["id"]));
        Assert.Equal(3, http.Seen.Count);
    }

    [Fact]
    public void an_update_and_a_delete_retry_a_5xx()
    {
        // PATCH is opted in: it rewrites the owner's own entry, which has no attendees, so a second try notifies nobody.
        var http = new FakeHandler();
        http.Reply(HttpStatusCode.ServiceUnavailable);
        http.Reply(HttpStatusCode.OK, "{\"id\": \"E1\"}");
        http.Reply(HttpStatusCode.ServiceUnavailable);
        http.Reply(HttpStatusCode.NoContent, "");
        var writer = Writer(http);
        writer.Update("E1", Payload());
        writer.Delete("E2");
        Assert.Equal(["PATCH /me/events/E1", "PATCH /me/events/E1", "DELETE /me/events/E2", "DELETE /me/events/E2"], http.Seen);
    }

    [Fact]
    public void an_unconfirmed_create_stops_the_run_and_the_next_run_does_not_make_it_again()
    {
        var c = Config();
        var a = Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31");
        var http = new FakeHandler();
        var graph = Graph(http);
        var alerts = new List<string>();
        var lines = new List<string>();
        JsonObject? Go() => Runner.Run(c, graph, WriteDoor.Live(graph, "Europe/London"), (_, _, _) => ([Jordan], [a]), lines.Add,
            scheduled: true, now: () => new DateTime(2026, 11, 12, 10, 0, 0), today: Today, alert: (title, _) => alerts.Add(title));

        // Three runs in a row whose create gets a 503: each sent once, never retried; notified on the third, once.
        for (var i = 0; i < 3; i++)
        {
            http.Reply(HttpStatusCode.ServiceUnavailable, "{\"error\": \"busy\"}");
            Assert.Throws<GraphDeliveryUnknownException>(Go);
        }
        Assert.Equal(3, http.Seen.Count(s => s == "POST /me/events"));
        Assert.Contains(lines, line => line.StartsWith("sync failed (unconfirmed): ", StringComparison.Ordinal)
                                       && line.EndsWith(Runner.UnconfirmedNote, StringComparison.Ordinal));
        Assert.Equal(["calendar-team-leave-sync couldn't confirm a calendar entry 3 runs in a row: it may have gone"], alerts);
        Assert.StartsWith("GraphDeliveryUnknownException: ", Py.Str(Runner.LoadState(c)["last_error"]));

        // The last one had in fact landed: the next run reads it back by its absence id and writes nothing.
        var made = Calendar.Stamped(Calendar.Body(c, a), a.Id);
        made["id"] = "E1";
        http.Get("/me/events", () => FakeHandler.Json(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(made) }.ToJsonString()));
        http.Seen.Clear();
        var report = Go()!;
        Assert.All(http.Seen, s => Assert.StartsWith("GET ", s));
        Assert.Equal(1, Py.Int(report["unchanged"]));
        Assert.Empty(report["added"]!.AsArray());
        Assert.Equal(0, Py.Int(Runner.LoadState(c)["failures"]));
    }
}
