using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using static Fixwire.Tests.FakeIngest;

namespace Fixwire.Tests;

public class FixwireTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private sealed class CartException(string message, Exception inner) : Exception(message, inner);

    private static void ChargeCard(int amount)
    {
        if (amount > 100)
        {
            throw new ArgumentException($"amount {amount} exceeds the limit");
        }
    }

    private static async Task CheckoutAsync(int amount)
    {
        await Task.Yield();
        try
        {
            ChargeCard(amount);
        }
        catch (ArgumentException e)
        {
            throw new CartException("checkout failed", e);
        }
    }

    [Fact]
    public void ParsesDsns()
    {
        var d = Dsn.Parse("https://fw_pk_live_abc@ingest.fixwire.io");
        Assert.Equal("fw_pk_live_abc", d.Key);
        Assert.Equal("https://ingest.fixwire.io", d.BaseUrl);
        Assert.Equal("http://127.0.0.1:9000/v1/logs", Dsn.Parse("http://k@127.0.0.1:9000/").Url("/v1/logs").ToString());
        Assert.Equal("https://self.example.com/fixwire", Dsn.Parse(" https://k@self.example.com/fixwire ").BaseUrl);
        foreach (var bad in new[] { "", "ingest.fixwire.io", "https://ingest.fixwire.io", "ftp://k@host", "https://@host" })
        {
            Assert.Throws<ArgumentException>(() => Dsn.Parse(bad));
        }
    }

    [Fact]
    public void DoesNothingWithoutADsn()
    {
        var c = new Client(new FixwireOptions { Dsn = "" });
        Assert.True(!c.Enabled || Environment.GetEnvironmentVariable("FIXWIRE_DSN") != null);
        Assert.Null(new Hub(c).CaptureException(new InvalidOperationException("x")));
    }

    [Fact]
    public void InitNeverThrows()
    {
        var was = Console.Error;
        var said = new StringWriter();
        Console.SetError(said);
        try
        {
            // A malformed DSN is said on stderr, debug or not (without the key), and the SDK stays off.
            foreach (var bad in new[] { "ingest.fixwire.io", "fw_pk_live_secret@ingest.fixwire.io", "https://ingest.fixwire.io", "ftp://secret@host", "https://@host" })
            {
                Assert.False(new Client(new FixwireOptions { Dsn = bad, CaptureUnhandledExceptions = false }).Enabled);
            }
            using (FixwireSdk.Init(o => o.Dsn = "https://:secret@ingest.fixwire.io"))
            {
                Assert.False(FixwireSdk.IsEnabled);
                Assert.Null(FixwireSdk.CaptureMessage("nowhere"));
            }
            Assert.Equal(6, said.ToString().Split("fixwire: the DSN must look like https://<key>@<host>; Fixwire is off").Length - 1);
            Assert.DoesNotContain("secret", said.ToString(), StringComparison.Ordinal);

            // So is an option HttpClient or a timer can't take; the longest they take are used.
            Assert.False(Enabled(o => o.Timeout = TimeSpan.FromMilliseconds(int.MaxValue + 1.0)));
            Assert.Contains("fixwire: Timeout must be at most 24 days; Fixwire is off", said.ToString(), StringComparison.Ordinal);
            Assert.True(Enabled(o => o.Timeout = TimeSpan.FromMilliseconds(int.MaxValue)));
            Assert.False(Enabled(o => o.SessionInterval = TimeSpan.FromMilliseconds(uint.MaxValue)));
            Assert.Contains("fixwire: SessionInterval must be at most 49 days; Fixwire is off", said.ToString(), StringComparison.Ordinal);
            Assert.True(Enabled(o => o.SessionInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1.0)));
            Assert.True(Enabled(o =>
            {
                o.Release = null; // no release: no sessions, no timer
                o.SessionInterval = TimeSpan.FromDays(365);
            }));
        }
        finally
        {
            Console.SetError(was);
        }

        static bool Enabled(Action<FixwireOptions> configure)
        {
            using var c = new FakeIngest().Hub(o =>
            {
                o.Release = "shop@1.2.0";
                configure(o);
            }).Client!;
            return c.Enabled;
        }
    }

    [Fact]
    public async Task CapturesExceptionsWithTheirInnerExceptions()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub(o =>
        {
            o.Release = "shop@1.2.0";
            o.Environment = "staging";
            o.ServerName = "web-1";
            o.InAppInclude.Add("Fixwire.Tests");
        });
        hub.Scope.User = new User("user-1") { Username = "ada" };
        hub.Scope.SetTag("plan", "team");
        hub.Scope.SetContext("order", new Dictionary<string, object?> { ["id"] = 42 });
        hub.AddBreadcrumb(new Breadcrumb("cart", "checkout started"));

        var e = await Assert.ThrowsAsync<CartException>(() => CheckoutAsync(500));
        var id = hub.CaptureException(e);
        Assert.Equal(32, id!.Length);
        Assert.True(await hub.FlushAsync(Wait));

        var reqs = ingest.Requests("/v1/logs");
        Assert.Single(reqs);
        Assert.Equal("Bearer publickey", reqs[0].Authorization);
        Assert.Equal("gzip", reqs[0].Encoding);
        Assert.Equal("fixwire.dotnet/" + Client.SdkVersion, reqs[0].UserAgent);
        var res = Resource(reqs[0]);
        Assert.Equal("shop", res["service.name"]);
        Assert.Equal("shop@1.2.0", res["service.version"]);
        Assert.Equal("staging", res["deployment.environment.name"]);
        Assert.Equal("web-1", res["host.name"]);
        Assert.Equal("dotnet", res["telemetry.sdk.language"]);

        var rec = LogRecords(reqs)[0];
        Assert.Equal("exception", rec["eventName"]);
        Assert.Equal(17L, rec["severityNumber"]);
        var a = Kv(rec["attributes"]);
        Assert.Equal(id, a["fixwire.event_id"]);
        Assert.Equal(typeof(CartException).FullName, a["exception.type"]);
        Assert.Equal("checkout failed", a["exception.message"]);
        Assert.Equal("user-1", a["user.id"]);
        Assert.Equal("ada", a["user.name"]);
        Assert.Equal("team", Map(a["fixwire.tags"])["plan"]);
        Assert.Equal(42L, Map(Map(a["fixwire.contexts"])["order"])["id"]);
        Assert.Equal("checkout started", Map(List(a["fixwire.breadcrumbs"])[0])["message"]);
        Assert.False(a.ContainsKey("fixwire.handled"));

        var chain = List(a["fixwire.exceptions"]).Select(Map).ToList();
        Assert.Equal(2, chain.Count);
        Assert.Equal("generic", Map(chain[0]["mechanism"])["type"]);
        Assert.Equal("chained", Map(chain[1]["mechanism"])["type"]);
        Assert.Equal("System.ArgumentException", chain[1]["type"]);
        Assert.Equal("System", chain[1]["module"]);
        var inner = List(chain[1]["frames"]).Select(Map).ToList();
        Assert.Equal("ChargeCard", inner[^1]["function"]);
        Assert.Equal("Fixwire.Tests.FixwireTests", inner[^1]["module"]);
        Assert.Equal(true, inner[^1]["in_app"]);
        Assert.EndsWith("FixwireTests.cs", (string)inner[^1]["file"]!, StringComparison.Ordinal);
        Assert.True((long)inner[^1]["line"]! > 0);
        // An async method's state machine is named as written.
        var outer = List(chain[0]["frames"]).Select(Map).ToList();
        Assert.Equal("CheckoutAsync", outer[^1]["function"]);
        Assert.Equal("Fixwire.Tests.FixwireTests", outer[^1]["module"]);
    }

    [Fact]
    public async Task CapturesUnthrownExceptionsWithTheCallersStack()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub(o => o.InAppInclude.Add("Fixwire.Tests"));
        hub.CaptureException(new InvalidOperationException("never thrown"));
        await hub.FlushAsync(Wait);
        var a = Kv(LogRecords(ingest.Requests("/v1/logs"))[0]["attributes"]);
        var frames = List(Map(List(a["fixwire.exceptions"])[0])["frames"]).Select(Map).ToList();
        Assert.Contains(frames, f => (string?)f["function"] == nameof(CapturesUnthrownExceptionsWithTheCallersStack));
        Assert.DoesNotContain(frames, f => ((string?)f["module"] ?? "").StartsWith("Fixwire.Hub", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CapturesMessagesAtTheirLevel()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub();
        hub.CaptureMessage("disk almost full");
        hub.Scope.Level = Level.Warning;
        hub.CaptureMessage("slow query");
        hub.CaptureMessage("on fire", Level.Fatal);
        await hub.FlushAsync(Wait);
        var recs = LogRecords(ingest.Requests("/v1/logs"));
        Assert.Equal(3, recs.Count);
        Assert.Equal("fixwire.message", recs[0]["eventName"]);
        Assert.Equal("disk almost full", AnyValue(Map(recs[0]["body"])));
        Assert.Equal(new[] { 9L, 13L, 21L }, recs.Select(r => (long)r["severityNumber"]!));
    }

    [Fact]
    public async Task BeforeSendChangesOrDrops()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub(o => o.BeforeSend = e =>
        {
            if (e.Message!.Contains("noise", StringComparison.Ordinal))
            {
                return null;
            }
            e.Tags["seen"] = "yes";
            return e;
        });
        Assert.Null(hub.CaptureMessage("noise"));
        Assert.NotNull(hub.CaptureMessage("signal"));
        await hub.FlushAsync(Wait);
        var recs = LogRecords(ingest.Requests("/v1/logs"));
        Assert.Single(recs);
        Assert.Equal("yes", Map(Kv(recs[0]["attributes"])["fixwire.tags"])["seen"]);
    }

    [Fact]
    public async Task MasksSecretsAndPersonalDataOnTheDevice()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub(o => o.TracesSampleRate = 1);
        hub.Scope.SetExtra("password", "hunter2hunter2");
        hub.Scope.SetExtra("note", "card 4111 1111 1111 1111 declined");
        hub.CaptureException(new InvalidOperationException("mail to ada@example.com bounced"));
        hub.CaptureFeedback(new Feedback("call me at ada@example.com"));
        using (var span = hub.SpanBuilder("send mail").WithParent(null).Start())
        {
            span.SetError(new InvalidOperationException("mail to ada@example.com bounced"));
        }
        await hub.FlushAsync(Wait);
        var a = Kv(LogRecords(ingest.Requests("/v1/logs"))[0]["attributes"]);
        Assert.Equal("[Filtered]", a["password"]);
        Assert.Equal("card [REDACTED:credit_card] declined", a["note"]);
        Assert.Equal("mail to [REDACTED:email] bounced", a["exception.message"]);
        Assert.Equal("call me at [REDACTED:email]", ingest.Requests("/v1/feedback")[0].Body["message"]);
        Assert.Equal("mail to [REDACTED:email] bounced", Map(Spans(ingest.Requests("/v1/traces"))[0]["status"])["message"]);

        var raw = new FakeIngest();
        var off = raw.Hub(o => o.Redact = false);
        off.CaptureException(new InvalidOperationException("mail to ada@example.com bounced"));
        await off.FlushAsync(Wait);
        Assert.Equal("mail to ada@example.com bounced", Kv(LogRecords(raw.Requests("/v1/logs"))[0]["attributes"])["exception.message"]);
    }

    [Fact]
    public async Task SpansFollowTheAsyncFlow()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub(o => o.TracesSampleRate = 1);
        using (hub.Bind())
        {
            using (var root = FixwireSdk.SpanBuilder("POST /checkout").WithOp("http.server").WithAttribute("http.request.method", "POST").Start())
            {
                Assert.Same(root, Span.Current);
                // Parallel branches each see the request's span, not each other's.
                await Task.WhenAll(
                    Task.Run(async () =>
                    {
                        using var q = FixwireSdk.StartSpan("SELECT carts", "db.query");
                        await Task.Delay(20);
                        Assert.Same(q, Span.Current);
                        q.SetError(new InvalidOperationException("deadlock"));
                    }),
                    Task.Run(async () =>
                    {
                        using var c = FixwireSdk.StartSpan("POST /charge", "http.client");
                        await Task.Delay(10);
                        Assert.Same(c, Span.Current);
                    }));
                Assert.Same(root, Span.Current);
                Assert.Empty(ingest.Requests("/v1/traces")); // children wait for their segment
                FixwireSdk.CaptureMessage("linked");
            }
            Assert.Null(Span.Current);
        }
        await hub.FlushAsync(Wait);

        var spans = Spans(ingest.Requests("/v1/traces")).ToDictionary(s => (string)s["name"]!);
        Assert.Equal(3, spans.Count);
        var r = spans["POST /checkout"];
        Assert.Equal(2L, r["kind"]);
        Assert.Equal(0x101L, r["flags"]);
        Assert.False(r.ContainsKey("parentSpanId"));
        Assert.Equal("http.server", Kv(r["attributes"])["fixwire.op"]);
        Assert.Equal("POST", Kv(r["attributes"])["http.request.method"]);
        var q = spans["SELECT carts"];
        Assert.Equal(3L, q["kind"]);
        Assert.Equal(r["spanId"], q["parentSpanId"]);
        Assert.Equal(2L, Map(q["status"])["code"]);
        Assert.Equal("deadlock", Map(q["status"])["message"]);
        Assert.Equal(r["spanId"], spans["POST /charge"]["parentSpanId"]);

        var rec = LogRecords(ingest.Requests("/v1/logs"))[0];
        Assert.Equal(r["traceId"], rec["traceId"]);
        Assert.Equal(r["spanId"], rec["spanId"]);
        Assert.Equal("POST /checkout", Kv(rec["attributes"])["fixwire.transaction"]);
    }

    [Fact]
    public async Task ContinuesCallersTraces()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub(o => o.TracesSampleRate = 0);
        using (var s = hub.SpanBuilder("GET /").ContinueTrace("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", "fw=1", "user=1").Start())
        {
            Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", s.TraceId);
            Assert.Equal("00f067aa0ba902b7", s.ParentSpanId);
            Assert.True(s.Sampled, "the caller's decision holds");
            Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-" + s.SpanId + "-01", s.Traceparent);
            Assert.Equal("fw=1", s.Tracestate);
            Assert.Equal("user=1", s.Baggage);
        }
        await hub.FlushAsync(Wait);
        Assert.Equal(0x301L, Spans(ingest.Requests("/v1/traces"))[0]["flags"]);

        using (var u = hub.SpanBuilder("GET /").ContinueTrace("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00").Start())
        {
            Assert.False(u.Sampled);
        }
        // What is passed on to the services this one calls is one line, within the W3C limits.
        using (var v = hub.SpanBuilder("GET /").ContinueTrace("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00", "fw=1\r\nx-injected: 1", new string('b', Span.MaxBaggage + 1)).Start())
        {
            Assert.Null(v.Tracestate);
            Assert.Null(v.Baggage);
        }
        foreach (var bad in new[]
        {
            "", "00-xyz-00f067aa0ba902b7-01", "00-00000000000000000000000000000000-00f067aa0ba902b7-01",
            "ff-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", "00-4bf92f3577b34da6a3ce929d0e0e4736-0000000000000000-01",
        })
        {
            Assert.Null(Span.ParseTraceparent(bad));
        }
    }

    [Theory]
    [InlineData("4bf92f3577b34da6ffffffffffffffff", 0.01, true)]
    [InlineData("4bf92f3577b34da6a000000000000000", 0.5, false)]
    [InlineData("4bf92f3577b34da6a080000000000000", 0.5, true)]
    [InlineData("4bf92f3577b34da6a07ffffffffff000", 0.5, false)]
    [InlineData("4bf92f3577b34da6a000000000000000", 1, true)]
    [InlineData("4bf92f3577b34da6ffffffffffffffff", 0, false)]
    public void SamplesTracesByTheSharedRule(string traceId, double rate, bool kept) =>
        Assert.Equal(kept, Span.Sample(traceId, rate));

    [Fact]
    public async Task CountsRequestSessions()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub(o => o.Release = "shop@1.2.0");
        var outcomes = new[] { "ok", "ok", "handled", "crash" };
        for (var i = 0; i < outcomes.Length; i++)
        {
            var request = hub.Clone();
            request.Scope.User = new User("user-" + (i % 2));
            using (request.StartRequestSession())
            {
                if (outcomes[i] == "handled")
                {
                    request.CaptureException(new InvalidOperationException("x"));
                }
                else if (outcomes[i] == "crash")
                {
                    request.CaptureException(new InvalidOperationException("y"), "aspnetcore", handled: false);
                }
            }
        }
        await hub.FlushAsync(Wait);
        var reqs = ingest.Requests("/v1/sessions");
        Assert.Single(reqs);
        var body = reqs[0].Body;
        Assert.Equal("shop@1.2.0", body["release"]);
        Assert.Equal("production", body["environment"]);
        Assert.Equal("fixwire.dotnet", Map(body["sdk"])["name"]);
        var aggregates = List(body["aggregates"]).Select(Map).ToList();
        Assert.Equal(2L, aggregates.Sum(x => (long)x["exited"]!));
        Assert.Equal(1L, aggregates.Sum(x => (long)x["errored"]!));
        Assert.Equal(1L, aggregates.Sum(x => (long)x["crashed"]!));
        Assert.Equal(2, aggregates.Select(x => x["did"]).Distinct().Count());
        Assert.Contains(Sessions.DeviceId(new User("user-0")), aggregates.Select(x => (string?)x["did"]));
        Assert.Equal(32, Sessions.DeviceId(new User("user-0"))!.Length);
        Assert.Null(ingest.Hub().Client!.Sessions);
    }

    [Fact]
    public async Task SendsCheckInsAndFeedback()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub(o => o.Release = "shop@1.2.0");
        using (hub.Bind())
        {
            var config = MonitorConfig.Crontab("0 3 * * *");
            config.CheckInMargin = 5;
            config.Timezone = "Europe/Berlin";
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                FixwireSdk.WithMonitorAsync("nightly report", config, () => throw new InvalidOperationException("no data")));
            Assert.NotNull(FixwireSdk.CaptureFeedback(new Feedback("The refund was wrong") { Score = -3, TraceId = "4bf92f3577b34da6a3ce929d0e0e4736" }));
            Assert.Null(FixwireSdk.CaptureFeedback(new Feedback("  ")));
        }
        await hub.FlushAsync(Wait);

        var checkIns = ingest.Requests("/v1/check-ins/nightly report");
        Assert.Equal(2, checkIns.Count);
        var start = checkIns[0].Body;
        var end = checkIns[1].Body;
        Assert.Equal("in_progress", start["status"]);
        Assert.Equal("0 3 * * *", Map(Map(start["monitor_config"])["schedule"])["value"]);
        Assert.Equal(5L, Map(start["monitor_config"])["checkin_margin"]);
        Assert.Equal("error", end["status"]);
        Assert.Equal(start["check_in_id"], end["check_in_id"]);
        Assert.True(end.ContainsKey("duration"));
        Assert.False(end.ContainsKey("monitor_config"));

        var fb = Assert.Single(ingest.Requests("/v1/feedback")).Body;
        Assert.Equal(-1L, fb["score"]);
        Assert.Equal("The refund was wrong", fb["message"]);
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", fb["trace_id"]);
        Assert.Equal("api", fb["source"]);
        Assert.Equal("shop@1.2.0", fb["release"]);
    }

    [Fact]
    public async Task RetriesAndHonoursRateLimits()
    {
        Transport.BackoffUnit = TimeSpan.FromMilliseconds(10);
        try
        {
            var ingest = new FakeIngest
            {
                Answer = (n, _) => n switch
                {
                    0 => (503, new Dictionary<string, string>()), // retried
                    1 => (200, new Dictionary<string, string> { ["Fixwire-Rate-Limits"] = "3600:error" }),
                    _ => (200, new Dictionary<string, string>()),
                },
            };
            var hub = ingest.Hub();
            hub.CaptureMessage("first");
            await hub.FlushAsync(Wait);
            Assert.Equal(2, ingest.Requests("/v1/logs").Count);
            // Errors are paused for an hour (past the longest wait: dropped); feedback isn't.
            hub.CaptureMessage("dropped");
            hub.CaptureFeedback(new Feedback { Score = 1 });
            await hub.FlushAsync(Wait);
            Assert.Equal(2, ingest.Requests("/v1/logs").Count);
            Assert.Single(ingest.Requests("/v1/feedback"));
        }
        finally
        {
            Transport.BackoffUnit = TimeSpan.FromSeconds(1);
        }
    }

    [Fact]
    public async Task WaitsNoLongerThanItShould()
    {
        // A 503 asking for a day is dropped, not parked in the queue all that time.
        var ingest = new FakeIngest { Answer = (_, _) => (503, new Dictionary<string, string> { ["Retry-After"] = "86400" }) };
        var hub = ingest.Hub();
        hub.CaptureMessage("unavailable");
        Assert.True(await hub.FlushAsync(Wait));
        Assert.Single(ingest.Requests());

        // Retry-After: seconds or an HTTP date, a day at most; broken or past ones are no wait.
        var now = DateTimeOffset.UtcNow;
        static HttpResponseHeaders RetryAfter(string value)
        {
            var r = new HttpResponseMessage();
            r.Headers.TryAddWithoutValidation("Retry-After", value);
            return r.Headers;
        }
        Assert.Equal(TimeSpan.FromSeconds(30), Transport.RetryAfter(RetryAfter("30"), now));
        Assert.Equal(TimeSpan.FromDays(1), Transport.RetryAfter(RetryAfter("86400"), now));
        Assert.Equal(TimeSpan.FromDays(1), Transport.RetryAfter(RetryAfter("86401"), now));
        Assert.Equal(TimeSpan.FromDays(1), Transport.RetryAfter(RetryAfter("99999999999999999999999"), now));
        var date = Transport.RetryAfter(RetryAfter(now.AddSeconds(30).ToString("r", CultureInfo.InvariantCulture)), now);
        Assert.InRange(date, TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromDays(1), Transport.RetryAfter(RetryAfter(now.AddDays(3).ToString("r", CultureInfo.InvariantCulture)), now));
        foreach (var none in new[] { "-5", "soon", "", now.AddSeconds(-30).ToString("r", CultureInfo.InvariantCulture) })
        {
            Assert.Equal(TimeSpan.Zero, Transport.RetryAfter(RetryAfter(none), now));
        }
        Assert.Equal(TimeSpan.Zero, Transport.RetryAfter(new HttpResponseMessage().Headers, now));

        // Fixwire-Rate-Limits: past a day is a day, and categories the protocol doesn't name are let go.
        var transport = hub.Client!.Transport!;
        transport.Limit("99999999999999:error;" + string.Join(";", Enumerable.Range(0, 1000).Select(i => "c" + i)), now);
        transport.Limit("86401:span, 999999999999999999999999:feedback, -5:log, soon:file", now);
        var paused = Paused(transport);
        Assert.Equal(new[] { "", "error", "feedback", "span" }, paused.Keys.Cast<string>().Order()); // "": the 503 above paused all
        Assert.Equal(now.AddDays(1), (DateTimeOffset)paused["error"]!);
        Assert.Equal(now.AddDays(1), (DateTimeOffset)paused["span"]!);
        Assert.Equal(now.AddDays(1), (DateTimeOffset)paused["feedback"]!);
    }

    /// <summary>Waits until done says so, or the test's wait.</summary>
    private static async Task UntilAsync(Func<bool> done)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.Elapsed < Wait)
        {
            await Task.Delay(10);
        }
    }

    private static System.Collections.IDictionary Paused(Transport transport) =>
        (System.Collections.IDictionary)typeof(Transport).GetField("_paused", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(transport)!;

    [Fact]
    public async Task PausesWhereAnswersSay()
    {
        Transport.BackoffUnit = TimeSpan.FromMilliseconds(10);
        try
        {
            // A 5xx with Retry-After pauses all data for that long (here past the longest wait: the retry is dropped).
            var unavailable = new FakeIngest { Answer = (_, _) => (503, new Dictionary<string, string> { ["Retry-After"] = "86401" }) };
            var hub = unavailable.Hub();
            hub.CaptureMessage("unavailable");
            Assert.True(await hub.FlushAsync(Wait));
            var paused = Paused(hub.Client!.Transport!);
            Assert.Equal(new[] { "" }, paused.Keys.Cast<string>());
            Assert.InRange((DateTimeOffset)paused[""]! - DateTimeOffset.UtcNow, TimeSpan.FromDays(1) - Wait, TimeSpan.FromDays(1));

            // A 429 without Fixwire-Rate-Limits pauses all data for Retry-After, a minute at least.
            var limited = new FakeIngest { Answer = (_, _) => (429, new Dictionary<string, string> { ["Retry-After"] = "5" }) };
            var hub2 = limited.Hub();
            hub2.CaptureMessage("limited");
            await hub2.FlushAsync(TimeSpan.FromMilliseconds(500));
            paused = Paused(hub2.Client!.Transport!);
            Assert.InRange((DateTimeOffset)paused[""]! - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(60));

            // No answer or a 5xx: tried again 3 times, then dropped.
            var down = new FakeIngest { Answer = (_, _) => (500, new Dictionary<string, string>()) };
            var hub3 = down.Hub();
            hub3.CaptureMessage("down");
            Assert.True(await hub3.FlushAsync(Wait));
            Assert.Equal(4, down.Requests("/v1/logs").Count);
        }
        finally
        {
            Transport.BackoffUnit = TimeSpan.FromSeconds(1);
        }
    }

    [Fact]
    public async Task SendsARequestAtMostFourTimes()
    {
        // A 429's retry counts toward the same 4 sends as those after a 5xx; a fifth would succeed.
        Transport.BackoffUnit = TimeSpan.FromMilliseconds(10);
        try
        {
            var limited = new Dictionary<string, string> { ["Fixwire-Rate-Limits"] = "1:error" };
            var ingest = new FakeIngest
            {
                Answer = (n, _) => n switch
                {
                    0 or 2 => (429, limited),
                    1 or 3 => (503, new Dictionary<string, string>()),
                    _ => (200, new Dictionary<string, string>()),
                },
            };
            var hub = ingest.Hub();
            hub.CaptureMessage("limited");
            Assert.True(await hub.FlushAsync(Wait));
            Assert.Equal(4, ingest.Requests("/v1/logs").Count);
        }
        finally
        {
            Transport.BackoffUnit = TimeSpan.FromSeconds(1);
        }
    }

    [Fact]
    public async Task QueuesRetriesApart()
    {
        // MaxQueue requests wait to be sent, and as many wait for a retry: retries don't crowd new data out.
        Transport.BackoffUnit = TimeSpan.FromMinutes(1);
        try
        {
            var ingest = new FakeIngest { Answer = (_, _) => (503, new Dictionary<string, string>()) };
            var hub = ingest.Hub(o =>
            {
                o.MaxQueue = 2;
                o.ShutdownTimeout = TimeSpan.FromMilliseconds(100);
            });
            var transport = hub.Client!.Transport!;
            var body = new byte[] { (byte)'{', (byte)'}' };
            Assert.True(transport.Send("/v1/logs", Transport.Error, body));
            Assert.True(transport.Send("/v1/logs", Transport.Error, body));
            await UntilAsync(() => Field("_retrying") == 2);
            Assert.True(transport.Send("/v1/logs", Transport.Error, body)); // the queue is empty
            Assert.True(transport.Send("/v1/logs", Transport.Error, body));
            await UntilAsync(() => ingest.Requests().Count == 4 && Field("_pending") == 2);
            Assert.Equal(2, Field("_retrying")); // the retries of these two had no room: dropped
            Assert.Equal(2, Field("_pending"));
            hub.Client.Dispose();
            await UntilAsync(() => Field("_retrying") == 0);
            Assert.Equal(0, Field("_pending"));

            int Field(string name) => (int)typeof(Transport).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(transport)!;
        }
        finally
        {
            Transport.BackoffUnit = TimeSpan.FromSeconds(1);
        }
    }

    [Fact]
    public async Task FollowsNoRedirect()
    {
        // The DSN's host answers 307 to elsewhere: neither the key nor the event goes there.
        using var elsewhere = new TcpListener(IPAddress.Loopback, 0);
        elsewhere.Start();
        var redirected = elsewhere.AcceptTcpClientAsync();
        using var dsnHost = new TcpListener(IPAddress.Loopback, 0);
        dsnHost.Start();
        var to = "http://127.0.0.1:" + ((IPEndPoint)elsewhere.LocalEndpoint).Port + "/v1/logs";
        var answered = AnswerOnceAsync(dsnHost, "HTTP/1.1 307 Temporary Redirect\r\nLocation: " + to + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using (var client = new Client(new FixwireOptions
        {
            Dsn = "http://publickey@127.0.0.1:" + ((IPEndPoint)dsnHost.LocalEndpoint).Port,
            CaptureUnhandledExceptions = false,
        }))
        {
            new Hub(client).CaptureMessage("moved");
            Assert.True(await client.FlushAsync(Wait));
        }
        await answered;
        Assert.False(redirected.IsCompleted);
    }

    /// <summary>Reads one HTTP request (headers and body) and writes the answer.</summary>
    private static async Task AnswerOnceAsync(TcpListener listener, string answer)
    {
        using var connection = await listener.AcceptTcpClientAsync();
        var stream = connection.GetStream();
        var request = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var bodyAt = -1;
        var length = 0;
        while (bodyAt < 0 || request.Length < bodyAt + length)
        {
            var n = await stream.ReadAsync(buffer);
            if (n == 0)
            {
                break;
            }
            request.Write(buffer, 0, n);
            var head = Encoding.ASCII.GetString(request.GetBuffer(), 0, (int)request.Length);
            var end = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (bodyAt < 0 && end >= 0)
            {
                bodyAt = end + 4;
                var m = Regex.Match(head[..end], @"(?im)^content-length:\s*(\d+)");
                length = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            }
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes(answer));
    }

    [Fact]
    public async Task ReadsNoAnswerBody()
    {
        // Only the status and headers matter: a huge (here endless) body is never buffered.
        var body = new EndlessStream();
        var hub = new Hub(new Client(new FixwireOptions
        {
            Dsn = FakeIngest.Dsn,
            HttpMessageHandler = new Answering(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }),
            CaptureUnhandledExceptions = false,
        }));
        Assert.NotNull(hub.CaptureMessage("answered"));
        Assert.True(await hub.FlushAsync(Wait));
        Assert.Equal(0, body.BytesRead);
    }

    private sealed class Answering(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer());
    }

    /// <summary>A body without end; past a megabyte it fails, as a client buffering it would.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesRead;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Interlocked.Add(ref BytesRead, count) > 1 << 20)
            {
                throw new IOException("the body was read");
            }
            Array.Fill(buffer, (byte)'x', offset, count);
            return count;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class UnreadableException : Exception
    {
        public override string Message => throw new InvalidOperationException("no message");
    }

    private sealed class Unprintable
    {
        public override string ToString() => throw new InvalidOperationException("no string");
    }

    [Fact]
    public async Task CapturingNeverThrowsAtTheApp()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub();
        Assert.NotNull(hub.CaptureException(new UnreadableException()));

        // Null keys and breadcrumbs are ignored, not thrown back.
        hub.Scope.SetTag(null!, "x");
        hub.Scope.SetExtra(null!, 1);
        hub.Scope.SetContext(null!, null);
        hub.AddBreadcrumb(null!);
        using (var span = hub.SpanBuilder("s").WithAttribute(null!, 1).Start())
        {
            span.SetAttribute(null!, 1);
        }

        // Messages the error budget's fingerprint used to time out on (and throw at the app).
        var sw = Stopwatch.StartNew();
        Assert.NotNull(hub.CaptureMessage("q" + new string('@', 20_000)));
        Assert.NotNull(hub.CaptureMessage(string.Concat(Enumerable.Repeat("eyJ-", 20_000))));
        Assert.True(sw.ElapsedMilliseconds < 1000, "took " + sw.ElapsedMilliseconds + " ms");

        // One exception captured on many threads at once.
        var shared = new InvalidOperationException("shared");
        Parallel.For(0, 64, _ => hub.CaptureException(shared));
        Assert.True(hub.Client!.IsCaptured(shared));

        await hub.FlushAsync(Wait);
        var a = Kv(LogRecords(ingest.Requests("/v1/logs"))[0]["attributes"]);
        Assert.Equal(typeof(UnreadableException).FullName, a["exception.type"]);
        Assert.False(a.ContainsKey("exception.message"));
    }

    [Fact]
    public async Task BoundsWhatItSends()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub();
        var cycle = new Dictionary<string, object?>();
        for (var i = 0; i < 8; i++)
        {
            cycle["k" + i] = cycle; // walked to the depth limit: 8^10 values
        }
        hub.Scope.SetExtra("cycle", cycle);
        hub.Scope.SetExtra("endless", Forever());
        hub.Scope.SetExtra("unprintable", new Unprintable());
        hub.Scope.SetExtra("long", new string('x', 100_000));
        hub.Scope.SetExtra("wide", Enumerable.Range(0, 1000).ToDictionary(i => "k" + i, i => (object?)i));
        hub.Scope.SetExtra("deep", Nest(12));
        hub.Scope.SetExtra("deepList", NestList(12));
        hub.Scope.SetExtra("numbers", new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1.5 });
        var crumb = new Breadcrumb("upload", new string('z', 100_000));
        hub.AddBreadcrumb(crumb);
        Assert.Equal(1024 + Otlp.ReadAhead, crumb.Message!.Length); // kept no larger than an event reads
        Assert.NotNull(hub.CaptureMessage("bounded"));
        await hub.FlushAsync(Wait);
        var a = Kv(LogRecords(ingest.Requests("/v1/logs"))[0]["attributes"]);
        Assert.Equal("[Circular ~]", Map(a["cycle"])["k0"]);
        Assert.Equal(100, List(a["endless"]).Count);
        Assert.Equal(100, Map(a["wide"]).Count);
        Assert.Equal("[Unreadable]", a["unprintable"]);
        Assert.Equal(new string('x', 1021) + "...", a["long"]);
        Assert.Equal(new string('z', 1021) + "...", Map(List(a["fixwire.breadcrumbs"])[0])["message"]);
        Assert.Equal(new object?[] { "NaN", "Infinity", "-Infinity", 1.5 }, List(a["numbers"]));

        // Ten levels a value: the eleventh is "[Object]" or "[Array]".
        var level = a["deep"];
        for (var i = 1; i < 10; i++)
        {
            level = Map(level)["next"];
        }
        Assert.Equal("[Object]", Map(level)["next"]);
        var items = a["deepList"];
        for (var i = 1; i < 10; i++)
        {
            items = List(items)[0];
        }
        Assert.Equal("[Array]", List(items)[0]);

        static Dictionary<string, object?> Nest(int depth) =>
            new() { ["next"] = depth == 1 ? "end" : Nest(depth - 1) };
        static List<object?> NestList(int depth) =>
            new() { depth == 1 ? "end" : NestList(depth - 1) };

        static IEnumerable<int> Forever()
        {
            for (var i = 0; ; i++)
            {
                yield return i;
            }
        }
    }

    [Fact]
    public async Task CountsSessionsOfManyUsersWithinBounds()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub(o => o.Release = "shop@1.2.0");
        var at = DateTimeOffset.UtcNow;
        for (var i = 0; i < Sessions.MaxBuckets + 500; i++)
        {
            hub.Client!.Sessions!.Record("exited", Sessions.DeviceId(new User("user-" + i)), at);
        }
        await hub.FlushAsync(Wait);
        // 5,000 users apart and one count without them: two requests of at most 5,000 aggregates.
        var requests = ingest.Requests("/v1/sessions");
        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => Assert.True(List(r.Body["aggregates"]).Count <= Sessions.MaxBuckets));
        var aggregates = requests.SelectMany(r => List(r.Body["aggregates"])).Select(Map).ToList();
        Assert.Equal(Sessions.MaxBuckets + 1, aggregates.Count);
        Assert.Equal(Sessions.MaxBuckets + 500L, aggregates.Sum(x => (long)x["exited"]!));
        Assert.Equal(500L, aggregates.Single(x => !x.ContainsKey("did"))["exited"]);
    }

    [Fact]
    public async Task DropsRefusedRequests()
    {
        var ingest = new FakeIngest { Answer = (_, _) => (400, new Dictionary<string, string>()) };
        var hub = ingest.Hub();
        hub.CaptureMessage("bad");
        await hub.FlushAsync(Wait);
        Assert.Single(ingest.Requests());
    }

    [Fact]
    public async Task BudgetsCrashLoops()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub(o => o.ErrorBudget.PerIssueBurst = 3);
        var sent = Enumerable.Range(0, 20).Count(i => hub.CaptureMessage($"order {1000 + i} failed") != null);
        Assert.Equal(3, sent);
        Assert.NotNull(hub.CaptureMessage("another issue"));
        hub.Client!.Budget.Age(Budget.IssueOf(new FixwireEvent { Message = "order 1 failed" }), TimeSpan.FromMinutes(1));
        Assert.NotNull(hub.CaptureMessage("order 2000 failed"));
        await hub.FlushAsync(Wait);
        var recs = LogRecords(ingest.Requests("/v1/logs"));
        Assert.Equal(17L, Kv(recs[^1]["attributes"])["fixwire.suppressed"]);

        static FixwireEvent At(string function)
        {
            var x = new ExceptionValue { Type = "x" };
            x.Frames.Add(new Frame { Module = "Shop.App", Function = function, InApp = true });
            return new FixwireEvent { Exceptions = { x } };
        }
        Assert.NotEqual(Budget.IssueOf(At("A")), Budget.IssueOf(At("B")));
        Assert.Equal(
            Budget.IssueOf(new FixwireEvent { Message = "user ada@example.com: 3 retries" }),
            Budget.IssueOf(new FixwireEvent { Message = "user bob@example.org: 12 retries" }));
    }

    [Fact]
    public void BudgetForgetsTheLeastRecentlySeen()
    {
        // One event an issue: a remembered issue holds the next back, a forgotten one starts afresh.
        var budget = new Budget(new ErrorBudget { PerIssueBurst = 1, PerIssuePerMinute = 0, PerMinute = 1e9 });
        var now = DateTimeOffset.UtcNow;
        int Allow(int issue)
        {
            now = now.AddTicks(1);
            return budget.Allow($"issue {issue}", now);
        }
        const int Max = 1024;
        for (var i = 0; i < Max; i++)
        {
            Assert.Equal(0, Allow(i));
        }
        for (var i = 0; i < Max; i++)
        {
            Assert.Equal(-1, Allow(i));
        }
        // Seen again, 0 leaves 1 the least recently seen: the 1,025th issue forgets it, and only it.
        Assert.Equal(-1, Allow(0));
        Assert.Equal(0, Allow(Max));
        foreach (var i in Enumerable.Range(0, Max + 1).Where(i => i != 1))
        {
            Assert.Equal(-1, Allow(i));
        }
        Assert.Equal(0, Allow(1)); // forgotten: a fresh budget
        // That forgot 0, the least recently seen since; 0 forgets 2.
        Assert.Equal(0, Allow(0));
        Assert.Equal(0, Allow(2));
        Assert.Equal(-1, Allow(Max));
    }

    [Fact]
    public void BudgetForgetsInConstantTime()
    {
        // Past the issues remembered, each new one forgets the least recently seen without a scan.
        static TimeSpan Time(int count)
        {
            // Each issue is made as it comes: made beforehand, 200,000 would not fit the cache 100,000 fit.
            var budget = new Budget(new ErrorBudget());
            var now = DateTimeOffset.UtcNow;
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                budget.Allow($"issue {i}", now);
            }
            return sw.Elapsed;
        }
        Time(10_000); // warms up
        // The best of runs taken in turn, at least 5 and up to 20 for a quiet moment: a busy machine slows both alike.
        var (once, twice) = (TimeSpan.MaxValue, TimeSpan.MaxValue);
        for (var round = 1; round <= 20 && (round <= 5 || twice.Ticks >= once.Ticks * 3); round++)
        {
            once = TimeSpan.FromTicks(Math.Min(once.Ticks, Time(100_000).Ticks));
            twice = TimeSpan.FromTicks(Math.Min(twice.Ticks, Time(200_000).Ticks));
        }
        Assert.True(once < TimeSpan.FromMilliseconds(100), "100,000 issues took " + once.TotalMilliseconds + " ms");
        Assert.True(
            twice.Ticks < once.Ticks * 3,
            "200,000 issues took " + twice.TotalMilliseconds + " ms, 100,000 " + once.TotalMilliseconds + " ms");
    }

    [Fact]
    public async Task TracesOutgoingRequests()
    {
        var ingest = new FakeIngest();
        var hub = ingest.Hub(o =>
        {
            o.TracesSampleRate = 1;
            o.TracePropagationTargets.Add("api.internal");
        });
        var upstream = new Upstream();
        using var http = new HttpClient(new FixwireHttpMessageHandler(upstream));
        string trace;
        using (hub.Bind())
        using (var job = FixwireSdk.StartSpan("job", "task"))
        {
            trace = job.TraceId;
            await http.GetAsync(new Uri("https://api.internal/prices?sku=1"));
            await http.PostAsync(new Uri("https://api.internal/fail"), null);
            await http.GetAsync(new Uri("https://partner.example.com/hook?next=https://api.internal/"));
            Assert.Same(job, Span.Current);
            FixwireSdk.CaptureMessage("after");
        }
        await hub.FlushAsync(Wait);

        Assert.StartsWith("00-" + trace + "-", upstream.Traceparents["/prices"], StringComparison.Ordinal);
        Assert.Null(upstream.Traceparents["/hook"]);
        var spans = Spans(ingest.Requests("/v1/traces")).ToDictionary(s => (string)s["name"]!);
        var fail = spans["POST https://api.internal/fail"];
        Assert.Equal(503L, Kv(fail["attributes"])["http.response.status_code"]);
        Assert.Equal("api.internal", Kv(fail["attributes"])["server.address"]);
        Assert.Equal(2L, Map(fail["status"])["code"]);
        Assert.Equal(4, spans.Count);
        var crumbs = List(Kv(LogRecords(ingest.Requests("/v1/logs"))[0]["attributes"])["fixwire.breadcrumbs"]).Select(Map).ToList();
        Assert.Equal(3, crumbs.Count);
        Assert.Equal("error", crumbs[1]["level"]);
        Assert.Equal("https://api.internal/prices", Map(crumbs[0]["data"])["url"]);
    }

    /// <summary>A service the app calls: 503 under /fail, else 200; notes each path's traceparent.</summary>
    private sealed class Upstream : HttpMessageHandler
    {
        public Dictionary<string, string?> Traceparents { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Traceparents[path] = request.Headers.TryGetValues("traceparent", out var v) ? v.First() : null;
            return Task.FromResult(new HttpResponseMessage(path.StartsWith("/fail", StringComparison.Ordinal) ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK));
        }
    }

    [Fact]
    public void NamesFramesAsWritten()
    {
        Func<int> lambda = () => throw new InvalidOperationException("in a lambda");
        var e = Assert.Throws<InvalidOperationException>(() => lambda());
        var frames = Frames.Chain(e, "generic", true, new FixwireOptions())[0].Frames;
        Assert.Equal("Fixwire.Tests.FixwireTests", frames[^1].Module);
        Assert.StartsWith("<NamesFramesAsWritten>", frames[^1].Function, StringComparison.Ordinal);
        var o = new FixwireOptions();
        Assert.False(Frames.InApp("System.Collections.Generic.List`1", o));
        Assert.False(Frames.InApp("Microsoft.AspNetCore.Routing.EndpointMiddleware", o));
        Assert.True(Frames.InApp("Shop.Cart", o));
        o.InAppExclude.Add("Shop.Generated");
        Assert.False(Frames.InApp("Shop.Generated.Api", o));
    }

    [Fact]
    public void WritesJson()
    {
        var m = new Dictionary<string, object?>
        {
            ["s"] = "quote \" slash \\ newline \n tab \t nul \u0000 sep \u2028 é 😀",
            ["n"] = new List<object?> { 1, 2.5, 3.0, double.NaN, true },
            ["null"] = null,
            ["nested"] = new Dictionary<string, object?> { ["a"] = new[] { 1, 2 } },
        };
        Assert.Equal(
            "{\"s\":\"quote \\\" slash \\\\ newline \\n tab \\t nul \\u0000 sep \\u2028 é 😀\",\"n\":[1,2.5,3,null,true],\"null\":null,\"nested\":{\"a\":[1,2]}}",
            Fixwire.Internal.Json.Write(m));
    }
}
