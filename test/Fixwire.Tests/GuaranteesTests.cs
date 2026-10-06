using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using static Fixwire.Tests.FakeIngest;

namespace Fixwire.Tests;

/// <summary>The bounds every Fixwire SDK keeps (fixwire-protocol §13), at their edges.</summary>
public class GuaranteesTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    // Fixtures are split so no secret scanner sees a whole one in source.
    private const string KeyBegin = "-----BEGIN " + "RSA PRIVATE KEY-----";
    private const string KeyEnd = "-----END " + "RSA PRIVATE KEY-----";
    private const string Jwt = "eyJ" + "hbGciOiJIUzI1NiJ9.eyJ" + "zdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";

    private static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);

    [Theory]
    [InlineData("a", 1024, false)]
    [InlineData("a", 1025, true)]
    [InlineData("é", 512, false)] // 2 bytes each
    [InlineData("é", 513, true)]
    [InlineData("€", 341, false)] // 3 bytes each: 1,023
    [InlineData("\U0001F600", 256, false)] // 4 bytes each, a surrogate pair
    [InlineData("\U0001F600", 257, true)]
    public void StringsAreCutInUtf8Bytes(string c, int n, bool cut)
    {
        var s = string.Concat(Enumerable.Repeat(c, n));
        var got = Otlp.Clip(s, 1024);
        if (!cut)
        {
            Assert.Same(s, got);
            return;
        }
        Assert.EndsWith("...", got, StringComparison.Ordinal);
        Assert.InRange(Bytes(got), 1024 - Bytes(c) - 2, 1024); // as much as fits, "..." within
        var kept = got[..^3];
        Assert.Equal(s[..kept.Length], kept); // cut on a character
        Assert.False(char.IsHighSurrogate(kept[^1]));
    }

    [Fact]
    public void StringsCountWhatIsWritten()
    {
        Assert.Equal(new string('a', 1021) + "...", Otlp.Clip(new string('a', 1025), 1024));
        Assert.Equal(string.Concat(Enumerable.Repeat("€", 340)) + "...", Otlp.Clip(string.Concat(Enumerable.Repeat("€", 341)) + "aa", 1024));
        // A lone surrogate is written as U+FFFD: three bytes.
        Assert.Equal(new string('\ud800', 340) + "...", Otlp.Clip(new string('\ud800', 342), 1024));
        Assert.Equal("ab", Otlp.Clip("ab", 3));
        Assert.Equal("...", Otlp.Clip("abcd", 3));
    }

    [Fact]
    public async Task MaxValueLengthBoundsEveryString()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub(o => o.MaxValueLength = 64);
        hub.Scope.SetExtra("s", string.Concat(Enumerable.Repeat("é", 100)));
        hub.Scope.SetExtra(new string('k', 100), "v");
        hub.Scope.SetContext("order", new Dictionary<string, object?> { ["note"] = new string('n', 100) });
        Assert.NotNull(hub.CaptureMessage(new string('m', 100)));
        await hub.FlushAsync(Wait);
        var rec = LogRecords(ingest.Requests("/v1/logs")).Single();
        var a = Kv(rec["attributes"]);
        Assert.Equal(string.Concat(Enumerable.Repeat("é", 30)) + "...", a["s"]);
        Assert.Equal("v", a[new string('k', 61) + "..."]);
        Assert.Equal(new string('n', 61) + "...", Map(Map(a["fixwire.contexts"])["order"])["note"]);
        Assert.Equal(new string('m', 61) + "...", AnyValue(Map(rec["body"])));
    }

    [Fact]
    public async Task RedactionReadsPastTheCut()
    {
        // The cut at 1,024 bytes goes through a private key and a JWT: both are still found whole.
        var key = KeyBegin + "\n" + string.Join("\n", Enumerable.Repeat("MIIEpAIBAAKCAQEA0Z3VS5JJcds3xfn/ygWyF8PbnGy0AHB7MaEXAMPLEKEYAAAA", 25)) + "\n" + KeyEnd;
        var ingest = new FakeIngest();
        var hub = ingest.Hub();
        hub.CaptureMessage(new string('x', 900) + " " + key + " " + new string('y', 2000));
        hub.Scope.SetExtra("token", "unused"); // filtered whole
        hub.Scope.SetExtra("note", new string('x', 1000) + " " + Jwt);
        hub.CaptureMessage("second");
        await hub.FlushAsync(Wait);
        var recs = LogRecords(ingest.Requests("/v1/logs"));
        var body = (string)AnyValue(Map(recs[0]["body"]))!;
        Assert.StartsWith(new string('x', 900) + " [REDACTED:private_key] yyy", body, StringComparison.Ordinal);
        Assert.Equal(1024, Bytes(body));
        Assert.DoesNotContain("MII", body, StringComparison.Ordinal);
        var a = Kv(recs[1]["attributes"]);
        Assert.Equal(new string('x', 1000) + " [REDACTED:jwt]", a["note"]);
        Assert.Equal("[Filtered]", a["token"]);
    }

    [Fact]
    public async Task ConfigurationIsCutButNotMasked()
    {
        // The app's configuration is sent as given (masking "api@1.2.3.example" as an email would
        // break release health), cut to MaxValueLength; the app's data is masked.
        const string Release = "api@1.2.3.example";
        var environment = "ops-ada@example.com-" + new string('e', 100);
        var slug = "nightly-ada@example.com-" + new string('n', 100);
        var crontab = "0 3 * * * ada@example.com " + new string('c', 100);
        var timezone = "Europe/ada@example.com/" + new string('z', 100);
        static string Cut(string s) => s[..61] + "..."; // 64 bytes
        var ingest = new FakeIngest();
        var hub = ingest.Hub(o =>
        {
            o.MaxValueLength = 64;
            o.Release = Release;
            o.Environment = environment;
            o.ServerName = "web-ada@example.com";
            o.TracesSampleRate = 1;
        });
        using (hub.StartRequestSession())
        {
            hub.CaptureMessage("message");
        }
        var config = MonitorConfig.Crontab(crontab);
        config.Timezone = timezone;
        Assert.NotNull(hub.Client!.CaptureCheckIn(new CheckIn(slug, CheckInStatus.Ok) { Config = config }));
        Assert.NotNull(hub.CaptureFeedback(new Feedback("call ada@example.com " + new string('f', 100))
        {
            Name = "Ada ada@example.com",
            Url = "https://shop.example/?u=ada@example.com&" + new string('q', 100),
        }));
        using (hub.SpanBuilder("mail ada@example.com").Start())
        {
        }
        await hub.FlushAsync(Wait);

        foreach (var r in ingest.Requests("/v1/logs").Concat(ingest.Requests("/v1/traces")))
        {
            var resource = Resource(r);
            Assert.Equal(Release, resource["service.version"]);
            Assert.Equal(Cut(environment), resource["deployment.environment.name"]);
            Assert.Equal("web-ada@example.com", resource["host.name"]);
        }
        var sessions = Assert.Single(ingest.Requests("/v1/sessions")).Body;
        Assert.Equal(Release, sessions["release"]);
        Assert.Equal(Cut(environment), sessions["environment"]);

        var checkIn = Assert.Single(ingest.Requests("/v1/check-ins/" + Cut(slug))).Body;
        Assert.Equal(Cut(environment), checkIn["environment"]);
        Assert.Equal(Cut(crontab), Map(Map(checkIn["monitor_config"])["schedule"])["value"]);
        Assert.Equal(Cut(timezone), Map(checkIn["monitor_config"])["timezone"]);

        var feedback = Assert.Single(ingest.Requests("/v1/feedback")).Body;
        Assert.Equal(Release, feedback["release"]);
        Assert.Equal(Cut(environment), feedback["environment"]);
        Assert.Equal(Cut("call [REDACTED:email] " + new string('f', 100)), feedback["message"]);
        Assert.Equal("Ada [REDACTED:email]", feedback["name"]);
        Assert.Equal(Cut("https://shop.example/?u=[REDACTED:email]&" + new string('q', 100)), feedback["url"]);

        Assert.Equal("mail [REDACTED:email]", Assert.Single(Spans(ingest.Requests("/v1/traces")))["name"]);
    }

    [Fact]
    public void ValuesReadAtMostTenThousandObjects()
    {
        // 1 + 100 + 10,000 lists: after 1 + 99 × 101 of them, the last list of lists is not read.
        var outer = Enumerable.Range(0, 100)
            .Select(_ => (object?)Enumerable.Range(0, 100).Select(_ => (object?)new List<object?> { 0 }).ToList())
            .ToList();
        var plain = Otlp.PlainMap(new Dictionary<string, object?> { ["v"] = outer, ["w"] = new List<object?> { new List<object?> { 1 } } }, 1024);
        var v = (List<object?>)plain["v"]!;
        Assert.Equal(1, Count(v, "[Array]"));
        Assert.Equal("[Array]", v[99]);
        Assert.Equal(100, ((List<object?>)v[98]!).Count);
        Assert.Equal(0, Count(plain["w"], "[Array]")); // each value reads its own 10,000

        // Shared lists 100 wide and 10 deep are 100^10 paths: walked within the bound.
        object? shared = new List<object?> { 1 };
        for (var i = 0; i < 10; i++)
        {
            shared = Enumerable.Repeat(shared, 100).ToList();
        }
        var sw = Stopwatch.StartNew();
        var walked = Otlp.PlainMap(new Dictionary<string, object?> { ["shared"] = shared }, 1024);
        Assert.True(sw.ElapsedMilliseconds < 2000, "took " + sw.ElapsedMilliseconds + " ms");
        Assert.True(Count(walked["shared"], "[Array]") > 0);

        static int Count(object? v, string marker) => v switch
        {
            string s => s == marker ? 1 : 0,
            List<object?> l => l.Sum(x => Count(x, marker)),
            Dictionary<string, object?> m => m.Values.Sum(x => Count(x, marker)),
            _ => 0,
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Recurse(int n) => n == 0 ? throw new InvalidOperationException("deep") : Recurse(n - 1) + 1;

    [Fact]
    public async Task ExceptionsKeepTheirNewestFramesAndTenOfTheChain()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub();
        var deep = Assert.Throws<InvalidOperationException>(() => Recurse(150));
        hub.CaptureException(deep);

        Exception chained = new InvalidOperationException("e10");
        for (var i = 9; i >= 0; i--)
        {
            chained = new InvalidOperationException("e" + i, chained);
        }
        hub.CaptureException(chained);

        // An event made by hand is held to the same bounds.
        var made = new FixwireEvent();
        for (var i = 0; i < 12; i++)
        {
            var x = new ExceptionValue { Type = "x" + i };
            for (var f = 0; f < 150; f++)
            {
                x.Frames.Add(new Frame { Module = "Shop", Function = "f" + f });
            }
            made.Exceptions.Add(x);
        }
        hub.CaptureEvent(made);
        await hub.FlushAsync(Wait);

        var recs = LogRecords(ingest.Requests("/v1/logs"));
        List<Dictionary<string, object?>> Chain(int r) => List(Kv(recs[r]["attributes"])["fixwire.exceptions"]).Select(Map).ToList();
        List<Dictionary<string, object?>> FramesOf(Dictionary<string, object?> x) => List(x["frames"]).Select(Map).ToList();

        var frames = FramesOf(Chain(0)[0]);
        Assert.Equal(100, frames.Count);
        Assert.All(frames, f => Assert.Equal(nameof(Recurse), f["function"])); // the oldest 51 and the caller went

        Assert.Equal(Enumerable.Range(0, 10).Select(i => "e" + i), Chain(1).Select(x => (string?)x["message"]));

        var madeChain = Chain(2);
        Assert.Equal(10, madeChain.Count);
        Assert.Equal(Enumerable.Range(50, 100).Select(i => "f" + i), FramesOf(madeChain[0]).Select(f => (string?)f["function"]));

        var few = new FakeIngest();
        var fewHub = few.Hub(o => o.MaxStackFrames = 5);
        fewHub.CaptureException(deep);
        await fewHub.FlushAsync(Wait);
        Assert.Equal(5, FramesOf(List(Kv(LogRecords(few.Requests("/v1/logs")).Single()["attributes"])["fixwire.exceptions"]).Select(Map).First()).Count);
    }

    private static List<object?> Big() =>
        Enumerable.Range(0, 100).Select(_ => (object?)Enumerable.Range(0, 20).Select(_ => (object?)new string('y', 1000)).ToList()).ToList();

    [Fact]
    public async Task LargeEventsShedBreadcrumbsThenContexts()
    {
        // Over 1 MB: without the breadcrumbs, then without the contexts, else dropped.
        var crumbs = new FakeIngest();
        var hub = crumbs.Hub();
        var crumb = new Breadcrumb("upload", "big");
        crumb.Data["rows"] = Big();
        hub.AddBreadcrumb(crumb);
        hub.Scope.SetContext("order", new Dictionary<string, object?> { ["id"] = 7 });
        Assert.NotNull(hub.CaptureMessage("large breadcrumbs"));

        var contexts = new FakeIngest();
        var hub2 = contexts.Hub();
        hub2.AddBreadcrumb(new Breadcrumb("cart", "small"));
        hub2.Scope.SetContext("rows", new Dictionary<string, object?> { ["rows"] = Big() });
        hub2.Scope.SetExtra("kept", "yes");
        Assert.NotNull(hub2.CaptureMessage("large contexts"));

        var extras = new FakeIngest();
        var hub3 = extras.Hub();
        hub3.Scope.SetExtra("rows", Big());
        Assert.Null(hub3.CaptureMessage("large extras"));

        await Task.WhenAll(hub.FlushAsync(Wait), hub2.FlushAsync(Wait), hub3.FlushAsync(Wait));
        var a = Kv(LogRecords(crumbs.Requests("/v1/logs")).Single()["attributes"]);
        Assert.False(a.ContainsKey("fixwire.breadcrumbs"));
        Assert.Equal(7L, Map(Map(a["fixwire.contexts"])["order"])["id"]);
        var a2 = Kv(LogRecords(contexts.Requests("/v1/logs")).Single()["attributes"]);
        Assert.False(a2.ContainsKey("fixwire.breadcrumbs"));
        Assert.False(a2.ContainsKey("fixwire.contexts"));
        Assert.Equal("yes", a2["kept"]);
        Assert.Empty(extras.Requests());
    }

    [Fact]
    public async Task SpansGoInRequestsOfAtMostAHundred()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub(o => o.TracesSampleRate = 1);
        using (hub.Bind())
        using (var root = FixwireSdk.StartSpan("job", "task"))
        {
            for (var i = 0; i < 250; i++)
            {
                FixwireSdk.StartSpan("step " + i, "task").Dispose();
            }
            for (var i = 0; i < 200; i++)
            {
                root.SetAttribute("a" + i, i);
            }
            root.SetAttribute("a0", "changed"); // a key it has may still change
        }
        await hub.FlushAsync(Wait);
        var requests = ingest.Requests("/v1/traces");
        Assert.Equal(new[] { 100, 100, 51 }, requests.Select(r => Spans(new[] { r }).Count));
        var job = Spans(requests).Single(s => (string?)s["name"] == "job");
        var attributes = Kv(job["attributes"]);
        Assert.Equal(Span.MaxAttributes, attributes.Count); // fixwire.op among them
        Assert.Equal("task", attributes["fixwire.op"]);
        Assert.Equal("changed", attributes["a0"]);
    }

    [Fact]
    public void TraceContextIsStrict()
    {
        var hub = new Hub(null);
        const string Parent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        string? Kept(string? tracestate, string? baggage, bool baggageOf = false)
        {
            using var s = hub.SpanBuilder("GET /").ContinueTrace(Parent, tracestate, baggage).Start();
            return baggageOf ? s.Baggage : s.Tracestate;
        }

        // Over the limit in bytes, or holding a control character: dropped whole, never cut.
        var tracestate = "fw=" + string.Concat(Enumerable.Repeat("é", 254)) + "a"; // 512 bytes, 258 characters
        Assert.Equal(tracestate, Kept(tracestate, null));
        Assert.Null(Kept(tracestate + "a", null));
        var baggage = "k=" + string.Concat(Enumerable.Repeat("é", 4095)); // 8,192 bytes
        Assert.Equal(baggage, Kept(null, baggage, baggageOf: true));
        Assert.Null(Kept(null, baggage + "a", baggageOf: true));
        Assert.Null(Kept("fw=1\u0085x=2", null));
        Assert.Null(Kept(null, "k=1\u0000", baggageOf: true));
        Assert.Null(Kept("fw=1\u007f", null));
        Assert.Equal("fw=1,\tx=2", Kept("fw=1,\tx=2", null)); // a tab is W3C list whitespace
        Assert.Equal("k=1\t,\tj=2", Kept(null, "k=1\t,\tj=2", baggageOf: true));

        Assert.NotNull(Span.ParseTraceparent(" " + Parent + " "));
        foreach (var bad in new[]
        {
            "01-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", // version 00 only
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01-00", // nothing after the flags
            "00-4BF92F3577B34DA6A3CE929D0E0E4736-00f067aa0ba902b7-01", // lower-case hex: ignored, not lowered
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00F067AA0BA902B7-01",
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-0A",
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7", // four fields
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-1",
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-0g",
        })
        {
            Assert.Null(Span.ParseTraceparent(bad));
        }
    }

    [Theory]
    [InlineData("example.com", "https://example.com/", true)]
    [InlineData("example.com", "https://api.example.com/x", true)]
    [InlineData("example.com", "HTTPS://API.EXAMPLE.COM/x", true)]
    [InlineData("Example.COM", "https://api.example.com:8443/x", true)]
    [InlineData("example.com", "https://badexample.com/", false)]
    [InlineData("example.com", "https://example.com.evil.net/", false)]
    [InlineData("example.com", "https://example.com@evil.net/", false)]
    [InlineData("example.com", "https://evil.net/?next=https://example.com/", false)]
    [InlineData("example.com", "https://evil.net/#example.com", false)]
    [InlineData("example.com", "https://evil.net/example.com", false)]
    [InlineData("localhost:5000", "http://localhost:5000/x", true)]
    [InlineData("localhost:5000", "http://localhost:5001/x", false)]
    [InlineData("example.com:443", "https://example.com/", true)]
    [InlineData("[::1]:8080", "http://[::1]:8080/", true)]
    [InlineData("https://api.example.com/v2", "https://api.example.com/v2/items?id=1", true)]
    [InlineData("https://api.example.com/v2", "https://user:pw@api.example.com/v2/items", true)]
    [InlineData("https://api.example.com/v2", "https://api.example.com/v1/items", false)]
    [InlineData("https://api.example.com/v2", "https://evil.net/?u=https://api.example.com/v2", false)]
    [InlineData("/api", "/api/items", true)]
    [InlineData("/api", "/web/api", false)]
    [InlineData("/api", "https://evil.net/api/items", false)]
    [InlineData("/api", "//evil.net/api", false)]
    [InlineData("api.example.com/v2", "https://api.example.com/v2", false)] // a host has no path
    [InlineData("", "https://example.com/", false)]
    public void PropagationTargetsMatchHostsUrlsAndPaths(string target, string url, bool match)
    {
        var client = new Client(new FixwireOptions { TracePropagationTargets = { target } });
        Assert.Equal(match, client.ShouldPropagate(url));
    }

    [Fact]
    public void PropagationNeedsATarget()
    {
        var client = new Client(new FixwireOptions());
        Assert.False(client.ShouldPropagate("https://example.com/"));
        Assert.False(client.ShouldPropagate(null!));
        Assert.Equal("https://h.example/p", Client.Compared("https://u:p@w@h.example/p?q=1#f"));
    }
}
