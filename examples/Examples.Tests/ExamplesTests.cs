using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fixwire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Examples.Tests;

/// <summary>The examples are real apps: run each against a fake ingest and check what Fixwire receives.</summary>
public class ExamplesTests
{
    /// <summary>A server on a free port, answering with a handler; its base URL.</summary>
    private static async Task<(WebApplication App, string Url)> ServeAsync(RequestDelegate handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(handler);
        await app.StartAsync();
        return (app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
    }

    /// <summary>A fake Fixwire: keeps each request's decoded body by path.</summary>
    private sealed class Ingest
    {
        public List<(string Path, JsonElement Body)> Received { get; } = new();

        public async Task Handle(HttpContext ctx)
        {
            Stream body = ctx.Request.Body;
            if (ctx.Request.Headers.ContentEncoding == "gzip")
            {
                body = new GZipStream(body, CompressionMode.Decompress);
            }
            var doc = await JsonDocument.ParseAsync(body);
            lock (Received)
            {
                Received.Add((Uri.UnescapeDataString(ctx.Request.Path.Value!), doc.RootElement.Clone()));
            }
            await ctx.Response.WriteAsync("{}");
        }

        public List<JsonElement> Bodies(string path)
        {
            lock (Received)
            {
                return Received.Where(r => r.Path == path).Select(r => r.Body).ToList();
            }
        }

        /// <summary>Errors and messages: each log record's attributes, read.</summary>
        public List<Dictionary<string, JsonElement>> Events() =>
            Bodies("/v1/logs").SelectMany(b => b.GetProperty("resourceLogs").EnumerateArray())
                .SelectMany(rl => rl.GetProperty("scopeLogs").EnumerateArray())
                .SelectMany(sl => sl.GetProperty("logRecords").EnumerateArray())
                .Select(r => Attributes(r.GetProperty("attributes")))
                .ToList();

        public List<JsonElement> Spans() =>
            Bodies("/v1/traces").SelectMany(b => b.GetProperty("resourceSpans").EnumerateArray())
                .SelectMany(rs => rs.GetProperty("scopeSpans").EnumerateArray())
                .SelectMany(ss => ss.GetProperty("spans").EnumerateArray())
                .ToList();

        public static Dictionary<string, JsonElement> Attributes(JsonElement list) =>
            list.EnumerateArray().ToDictionary(a => a.GetProperty("key").GetString()!, a => a.GetProperty("value"));

        public (long Exited, long Errored, long Crashed) Sessions()
        {
            var aggregates = Bodies("/v1/sessions").SelectMany(b => b.GetProperty("aggregates").EnumerateArray()).ToList();
            return (aggregates.Sum(a => a.GetProperty("exited").GetInt64()),
                aggregates.Sum(a => a.GetProperty("errored").GetInt64()),
                aggregates.Sum(a => a.GetProperty("crashed").GetInt64()));
        }
    }

    private static string Text(JsonElement v) => v.GetProperty("stringValue").GetString()!;

    [Fact]
    public async Task ShopApiReportsWhatHappensInRequests()
    {
        var ingest = new Ingest();
        var (ingestApp, ingestUrl) = await ServeAsync(ingest.Handle);
        var traceparents = new List<string?>();
        var (inventoryApp, inventoryUrl) = await ServeAsync(ctx =>
        {
            lock (traceparents)
            {
                traceparents.Add(ctx.Request.Headers["traceparent"].FirstOrDefault());
            }
            ctx.Response.StatusCode = ctx.Request.Query["sku"] == "sku_2" ? 409 : 201; // sku_2 is sold out
            return Task.CompletedTask;
        });
        var app = global::ShopApi.Build([
            "--urls=http://127.0.0.1:0",
            "--Fixwire:Dsn=" + ingestUrl.Replace("http://", "http://publickey@", StringComparison.Ordinal),
            "--Fixwire:TracePropagationTargets:0=" + inventoryUrl,
            "--Inventory:Url=" + inventoryUrl,
        ]);
        await app.StartAsync();
        var http = new HttpClient
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()),
        };
        async Task<HttpStatusCode> Order(string user, string sku, string card)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = JsonContent.Create(new { sku, card }) };
            request.Headers.Add("X-User-Id", user);
            return (await http.SendAsync(request)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(new Uri("/products/sku_1", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(new Uri("/products/nope", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, await Order("user-1", "sku_1", "4242424242424242"));
        Assert.Equal(HttpStatusCode.PaymentRequired, await Order("user-2", "sku_1", "4000000000000002"));
        Assert.Equal(HttpStatusCode.Conflict, await Order("user-3", "sku_2", "4242424242424242"));
        Assert.Equal(HttpStatusCode.InternalServerError, (await http.GetAsync(new Uri("/admin/report", UriKind.Relative))).StatusCode);
        await app.StopAsync(); // flushes
        await app.DisposeAsync();

        // Three: the declined payment (handled), the logged stock error, the crash. No 404.
        var events = ingest.Events();
        Assert.Equal(3, events.Count);
        var payment = events.Single(e => e.TryGetValue("user.id", out var u) && Text(u) == "user-2");
        Assert.Equal("POST /orders", Text(payment["fixwire.transaction"]));
        Assert.StartsWith("charging order ord_", Text(payment["exception.message"]), StringComparison.Ordinal);
        Assert.Contains("PaymentException", payment["fixwire.exceptions"].ToString(), StringComparison.Ordinal);
        Assert.Contains("order received for sku_1", payment["fixwire.breadcrumbs"].ToString(), StringComparison.Ordinal);
        var stock = events.Single(e => e.TryGetValue("user.id", out var u) && Text(u) == "user-3");
        Assert.Equal("Program", Text(stock["logger"]));
        var crash = events.Single(e => Text(e["fixwire.transaction"]) == "GET /admin/report");
        Assert.False(crash["fixwire.handled"].GetProperty("boolValue").GetBoolean());
        Assert.Equal("System.DivideByZeroException", Text(crash["exception.type"]));

        // A server span per request named after its route; the lookups and inventory calls under them.
        var names = ingest.Spans().GroupBy(s => s.GetProperty("name").GetString()!).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(2, names["GET /products/{id}"]);
        Assert.Equal(3, names["POST /orders"]);
        Assert.Equal(1, names["GET /admin/report"]);
        Assert.Equal(2, names["SELECT products"]);
        Assert.Equal(3, names[$"POST {inventoryUrl}/reservations"]);
        Assert.All(traceparents, tp => Assert.StartsWith("00-", tp, StringComparison.Ordinal));

        // Release health: 3 requests ended well, 2 with an error, 1 crashed.
        Assert.Equal((3, 2, 1), ingest.Sessions());

        await inventoryApp.StopAsync();
        await ingestApp.StopAsync();
    }

    [Fact]
    public async Task NightlyReportReportsTheRunAndTheFailedAccount()
    {
        var ingest = new Ingest();
        var (ingestApp, ingestUrl) = await ServeAsync(ingest.Handle);
        var host = global::NightlyReport.Build(["--Fixwire:Dsn=" + ingestUrl.Replace("http://", "http://publickey@", StringComparison.Ordinal)]);
        var result = host.Services.GetRequiredService<JobResult>();
        await host.RunAsync();
        Assert.Equal(1, result.ExitCode); // one of three accounts failed

        var checkIns = ingest.Bodies("/v1/check-ins/nightly-report");
        Assert.Equal(2, checkIns.Count);
        Assert.Equal("in_progress", checkIns[0].GetProperty("status").GetString());
        Assert.Equal("error", checkIns[1].GetProperty("status").GetString());
        Assert.Equal(checkIns[0].GetProperty("check_in_id").GetString(), checkIns[1].GetProperty("check_in_id").GetString());
        Assert.Equal("0 3 * * *", checkIns[0].GetProperty("monitor_config").GetProperty("schedule").GetProperty("value").GetString());

        var events = ingest.Events();
        Assert.Equal(2, events.Count);
        var failure = events[0];
        Assert.Equal("NoInvoicesException", Text(failure["exception.type"]));
        Assert.Contains("\"globex\"", failure["fixwire.tags"].ToString(), StringComparison.Ordinal);
        Assert.Contains("building the report for globex", failure["fixwire.breadcrumbs"].ToString(), StringComparison.Ordinal);
        Assert.False(events[1].ContainsKey("fixwire.tags"), "the account's tag stayed");

        var spans = ingest.Spans();
        Assert.Equal(4, spans.Count);
        Assert.Single(spans.Select(s => s.GetProperty("traceId").GetString()).Distinct());
        await ingestApp.StopAsync();
    }
}
