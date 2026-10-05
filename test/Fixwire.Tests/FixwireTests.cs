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
        var hub = ingest.Hub();
        hub.Scope.SetExtra("password", "hunter2hunter2");
        hub.Scope.SetExtra("note", "card 4111 1111 1111 1111 declined");
        hub.CaptureException(new InvalidOperationException("mail to ada@example.com bounced"));
        hub.CaptureFeedback(new Feedback("call me at ada@example.com"));
        await hub.FlushAsync(Wait);
        var a = Kv(LogRecords(ingest.Requests("/v1/logs"))[0]["attributes"]);
        Assert.Equal("[Filtered]", a["password"]);
        Assert.Equal("card [REDACTED:credit_card] declined", a["note"]);
        Assert.Equal("mail to [REDACTED:email] bounced", a["exception.message"]);
        Assert.Equal("call me at [REDACTED:email]", ingest.Requests("/v1/feedback")[0].Body["message"]);

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
            await http.GetAsync(new Uri("https://partner.example.com/hook"));
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
