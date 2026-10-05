using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Fixwire.Tests.FakeIngest;

namespace Fixwire.Tests;

public class AspNetCoreTests
{
    /// <summary>The inventory service: sold out of sku_2; notes the traceparent of each call.</summary>
    private sealed class Inventory : HttpMessageHandler
    {
        public List<string?> Traceparents { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Traceparents)
            {
                Traceparents.Add(request.Headers.TryGetValues("traceparent", out var v) ? v.First() : null);
            }
            var soldOut = request.RequestUri!.Query.Contains("sku_2", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(soldOut ? HttpStatusCode.Conflict : HttpStatusCode.Created));
        }
    }

    private static async Task<(WebApplication App, HttpClient Http)> StartAsync(
        FakeIngest ingest, Inventory inventory, Action<WebApplication> routes, bool exceptionHandler = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration["Fixwire:Dsn"] = FakeIngest.Dsn;
        builder.Configuration["Fixwire:Release"] = "shop@1.2.0";
        builder.Configuration["Fixwire:TracesSampleRate"] = "1";
        builder.Configuration["Fixwire:TracePropagationTargets:0"] = "inventory.test";
        builder.Configuration["Fixwire:InAppInclude:0"] = "Fixwire.Tests";
        builder.AddFixwire(o =>
        {
            o.HttpMessageHandler = ingest;
            o.CaptureUnhandledExceptions = false;
        });
        builder.Services.AddHttpClient("inventory", c => c.BaseAddress = new Uri("http://inventory.test"))
            .ConfigurePrimaryHttpMessageHandler(() => inventory);
        var app = builder.Build();
        if (exceptionHandler)
        {
            app.UseExceptionHandler(e => e.Run(ctx =>
            {
                ctx.Response.StatusCode = 500;
                return ctx.Response.WriteAsync("sorry");
            }));
        }
        routes(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new HttpClient { BaseAddress = new Uri(address) });
    }

    [Fact]
    public async Task ReportsWhatHappensInRequests()
    {
        var ingest = new FakeIngest();
        var inventory = new Inventory();
        var (app, http) = await StartAsync(ingest, inventory, app =>
        {
            app.MapGet("/items/{id}", (string id, ILogger<AspNetCoreTests> log) =>
            {
                FixwireSdk.SetTag("item", id);
                log.LogInformation("looking up {Id}", id);
                FixwireSdk.CaptureException(new InvalidOperationException("price missing"));
                return Results.Accepted();
            });
            app.MapPost("/orders", async (string sku, IHttpClientFactory clients, ILogger<AspNetCoreTests> log) =>
            {
                var res = await clients.CreateClient("inventory").PostAsync(new Uri("/reservations?sku=" + sku, UriKind.Relative), null);
                if (!res.IsSuccessStatusCode)
                {
                    log.LogError("no stock for {Sku}", sku);
                    return Results.Conflict();
                }
                return Results.Created();
            });
            app.MapPost("/checkout", () =>
            {
                throw new InvalidOperationException("out of stock");
            });
        });
        await using (app)
        {
            var get = new HttpRequestMessage(HttpMethod.Get, "/items/42?ref=mail");
            get.Headers.Add("traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
            get.Headers.Add("Authorization", "Bearer secret");
            Assert.Equal(HttpStatusCode.Accepted, (await http.SendAsync(get)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await http.PostAsync(new Uri("/orders?sku=sku_1", UriKind.Relative), null)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync(new Uri("/orders?sku=sku_2", UriKind.Relative), null)).StatusCode);
            Assert.Equal(HttpStatusCode.InternalServerError, (await http.PostAsync(new Uri("/checkout", UriKind.Relative), null)).StatusCode);
            Assert.True(await FixwireSdk.FlushAsync(TimeSpan.FromSeconds(5)));
            await app.StopAsync();
        }

        var events = LogRecords(ingest.Requests("/v1/logs")).Select(r => (Record: r, A: Kv(r["attributes"]))).ToList();
        Assert.True(events.Count == 3, string.Join(" | ", events.Select(e => $"{e.A.GetValueOrDefault("fixwire.transaction")} {e.A.GetValueOrDefault("exception.message")} {e.A.GetValueOrDefault("logger")}")));
        var item = events.Single(e => (string?)e.A["fixwire.transaction"] == "GET /items/{id}");
        Assert.Equal("http://127.0.0.1:" + http.BaseAddress!.Port + "/items/42", item.A["url.full"]);
        Assert.Equal("ref=mail", item.A["url.query"]);
        Assert.Equal("42", Map(item.A["fixwire.tags"])["item"]);
        Assert.False(item.A.ContainsKey("http.request.header.authorization"));
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", item.Record["traceId"]);
        Assert.Contains(List(item.A["fixwire.breadcrumbs"]).Select(Map), c => (string?)c["message"] == "looking up 42");

        var stock = events.Single(e => (string?)e.A.GetValueOrDefault("logger") == typeof(AspNetCoreTests).FullName);
        Assert.Equal("POST /orders", stock.A["fixwire.transaction"]);
        Assert.Contains(List(stock.A["fixwire.breadcrumbs"]).Select(Map), c => (string?)c["category"] == "http");

        var crash = events.Single(e => (string?)e.A["fixwire.transaction"] == "POST /checkout");
        Assert.Equal(false, crash.A["fixwire.handled"]);
        Assert.Equal("aspnetcore", Map(Map(List(crash.A["fixwire.exceptions"])[0])["mechanism"])["type"]);

        var spans = Spans(ingest.Requests("/v1/traces"));
        var itemSpan = spans.Single(s => (string?)s["name"] == "GET /items/{id}");
        Assert.Equal("00f067aa0ba902b7", itemSpan["parentSpanId"]);
        Assert.Equal(202L, Kv(itemSpan["attributes"])["http.response.status_code"]);
        Assert.Equal("/items/{id}", Kv(itemSpan["attributes"])["http.route"]);
        Assert.Equal(2L, Map(spans.Single(s => (string?)s["name"] == "POST /checkout")["status"])["code"]);
        var orders = spans.Where(s => (string?)s["name"] == "POST /orders").ToList();
        Assert.Equal(2, orders.Count);
        var calls = spans.Where(s => ((string?)s["name"])!.StartsWith("POST http://inventory.test/reservations", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Contains(c["parentSpanId"], orders.Select(o => o["spanId"])));
        Assert.All(inventory.Traceparents, tp => Assert.StartsWith("00-", tp, StringComparison.Ordinal));

        var aggregates = ingest.Requests("/v1/sessions").SelectMany(r => List(r.Body["aggregates"])).Select(Map).ToList();
        Assert.Equal(1L, aggregates.Sum(x => (long)x["exited"]!));
        Assert.Equal(2L, aggregates.Sum(x => (long)x["errored"]!));
        Assert.Equal(1L, aggregates.Sum(x => (long)x["crashed"]!));
    }

    [Fact]
    public async Task ReportsExceptionsTheExceptionHandlerCatches()
    {
        var ingest = new FakeIngest();
        var (app, http) = await StartAsync(
            ingest,
            new Inventory(),
            app => app.MapGet("/report", int () => throw new DivideByZeroException()),
            exceptionHandler: true);
        await using (app)
        {
            var res = await http.GetAsync(new Uri("/report", UriKind.Relative));
            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
            Assert.Equal("sorry", await res.Content.ReadAsStringAsync());
            await FixwireSdk.FlushAsync(TimeSpan.FromSeconds(5));
            await app.StopAsync();
        }
        var a = Kv(Assert.Single(LogRecords(ingest.Requests("/v1/logs")))["attributes"]);
        Assert.Equal("System.DivideByZeroException", a["exception.type"]);
        Assert.Equal(false, a["fixwire.handled"]);
        Assert.Equal("GET /report", a["fixwire.transaction"]);
    }
}
