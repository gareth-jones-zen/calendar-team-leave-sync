using System.Text.Json.Nodes;
using PyCompat;
using ReviewQueue;
using static TeamLeave.Tests.Fixtures;

namespace TeamLeave.Tests;

/// <summary>[review]: the items queued exactly as the Python after_run hook queued them for the same report
/// (Golden/review.json: the hook run on each report against a seeded queue, the items it left, added times dropped;
/// synthetic people), and only when the hook would have run.</summary>
public sealed class ReviewTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly Config c = Config();
    private readonly RecordingGraph graph = new();
    private readonly List<string> lines = [];

    public void Dispose() => folder.Dispose();

    private string QueuePath => folder.File("review", "pending.json");

    private ReviewSettings Review() => new(QueuePath, "team-leave", "team-leave-review-test-" + Guid.NewGuid().ToString("N"));

    private static JsonArray Golden() => PyJson.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "review.json")))!.AsArray();

    public static TheoryData<string> Cases() => [.. Golden().Select(g => (string)g!["name"]!)];

    private static string WithoutAdded(IEnumerable<JsonObject> items) => PyJson.Dump(new JsonArray(items.Select(i =>
    {
        var copy = i.DeepClone().AsObject();
        copy.Remove("added");
        return (JsonNode)copy;
    }).ToArray()));

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_items_are_the_python_hooks(string name)
    {
        var golden = Golden().Single(g => (string)g!["name"]! == name)!;
        Directory.CreateDirectory(Path.GetDirectoryName(QueuePath)!);
        PyStr.WriteText(QueuePath, PyJson.Dump(golden["seed"], indent: 2, ensureAscii: false));
        c.Review = Review();
        Runner.Review(c, golden["report"]!.AsObject(), lines.Add);
        Assert.Equal(PyJson.Dump(golden["items"]), WithoutAdded(c.Review.Open().Items()));
        Assert.Equal([$"review: queued {Runner.ReviewItems(golden["report"]!.AsObject()).Count} leave item(s) under team-leave"], lines);
    }

    private JsonObject? Go(Absence[] absences, bool hook = true, bool dryRun = false, WriteDoor? door = null) =>
        Runner.Run(c, graph, door ?? (dryRun ? WriteDoor.Refusing("Europe/London") : WriteDoor.Live(graph, "Europe/London")),
            (_, _, _) => ([Jordan], [.. absences]), lines.Add, dryRun: dryRun, hook: hook, today: Today, alert: (_, _) => { });

    [Fact]
    public void A_hooked_run_that_changed_the_calendar_queues_each_change_and_logs_no_names()
    {
        c.Review = Review();
        Go([Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31")]);
        var item = Assert.Single(c.Review.Open().Items());
        Assert.Equal(("team-leave", "added", "Jordan Lee annual leave 24/12 to 31/12 added", "2026-12-24", "2026-12-31"),
            ((string)item["source"]!, (string)item["type"]!, (string)item["title"]!, (string)item["start"]!, (string)item["end"]!));
        Assert.Contains("review: queued 1 leave item(s) under team-leave", lines);
        var joined = string.Join("\n", lines);
        foreach (var secret in new[] { "Jordan", "24/12", "2026-12", "Holiday" })
            Assert.DoesNotContain(secret, joined);
    }

    [Fact]
    public void Nothing_is_queued_when_nothing_changed()
    {
        c.Review = Review();
        var a = Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31");
        graph.Answers[Calendar.OwnedUrl()] = [Recorded.Item(c, Owned(c, a, "E1"))];   // already in the calendar
        Go([a]);
        Assert.False(File.Exists(QueuePath));
    }

    [Theory]
    [InlineData(false, false)]   // no --hook
    [InlineData(true, true)]     // a dry run
    public void Nothing_is_queued_when_the_hook_wouldnt_run(bool hook, bool dryRun)
    {
        c.Review = Review();
        Go([Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31")], hook, dryRun);
        Assert.False(File.Exists(QueuePath));
    }

    [Fact]
    public void A_journal_run_queues_nothing_and_says_so()
    {
        c.Review = Review();
        Go([Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31")], door: WriteDoor.Journaling("Europe/London"));
        Assert.False(File.Exists(QueuePath));
        Assert.Contains("review queue not written: journal mode, nothing was written", lines);
    }

    [Fact]
    public void A_queue_that_cant_be_written_is_logged_and_the_run_goes_on()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(QueuePath)!);
        File.WriteAllText(QueuePath, "[]");
        c.Review = Review();
        var report = Go([Absence(c, "A1", Jordan, "2026-12-24", "2026-12-31")]);
        Assert.Single(report!["added"]!.AsArray());
        Assert.Contains(lines, l => l.StartsWith("review queue failed: ", StringComparison.Ordinal));
        Assert.Equal("[]", File.ReadAllText(QueuePath));
    }

    [Fact]
    public void Review_settings_are_read_with_paths_like_the_others()
    {
        var read = Config(Settings + "\n[review]\nfile = \"queue/pending.json\"\nsource = \"team-leave\"\n");
        Assert.Equal(Path.Combine(Path.GetDirectoryName(read.Path)!, "queue", "pending.json"), read.Review!.File);
        Assert.Equal("team-leave", read.Review.Source);
        Assert.Equal("queue-pending", read.Review.Open().LockName);
        Assert.Null(c.Review);
    }

    [Theory]
    [InlineData("[review]\nsource = \"team-leave\"\n", "needs file")]
    [InlineData("[review]\nfile = \"q.json\"\nsource = \"team-leave\"\nact = true\n", "act under [review] in ")]
    [InlineData("[review]\nfile = \"q.json\"\nsource = \"\"\n", "source under [review] in ")]
    public void Bad_review_settings_are_refused(string toml, string message)
    {
        var error = Assert.Throws<PyValueError>(() => Config(Settings + "\n" + toml));
        Assert.Contains(message, error.Message);
    }
}
