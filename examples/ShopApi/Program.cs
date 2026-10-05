// A small JSON API reporting to Fixwire: each request gets its own scope and trace, an exception
// that escapes is reported as a crash, a declined payment is reported with the order as context,
// the call to the inventory service is traced, and log records become breadcrumbs.
//
//   Fixwire__Dsn=https://<key>@<host> dotnet run --project examples/ShopApi
using Fixwire;

ShopApi.Build(args).Run();

/// <summary>The app, built apart from running it so that its test can start it too.</summary>
public static class ShopApi
{
    private static readonly Dictionary<string, Product> Products = new()
    {
        ["sku_1"] = new("sku_1", "Mug", 1200),
        ["sku_2"] = new("sku_2", "Poster", 2500),
    };

    private static int _orders;

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Fixwire from the "Fixwire" section of appsettings.json (and Fixwire__Dsn and the like):
        // each request's scope, crashes, release health and server span, ILogger records,
        // IHttpClientFactory clients.
        builder.AddFixwire();

        builder.Services.AddHttpClient("inventory", c =>
            c.BaseAddress = new Uri(builder.Configuration["Inventory:Url"] ?? "http://localhost:8081"));

        var app = builder.Build();
        app.MapGet("/products/{id}", GetProduct);
        app.MapPost("/orders", CreateOrderAsync);
        app.MapGet("/admin/report", Report);
        return app;
    }

    private static IResult GetProduct(string id)
    {
        // A span for the lookup, under the request's.
        using var span = FixwireSdk.StartSpan("SELECT products", "db.query");
        span.SetAttribute("db.system", "postgresql");
        // An unknown product is a 404: nothing escapes, so nothing is reported.
        return Products.TryGetValue(id, out var p) ? Results.Ok(p) : Results.NotFound();
    }

    private static async Task<IResult> CreateOrderAsync(
        OrderRequest order, HttpRequest request, IHttpClientFactory clients, ILogger<Program> log)
    {
        if (request.Headers["X-User-Id"].FirstOrDefault() is { } userId)
        {
            FixwireSdk.SetUser(new User(userId)); // this request's scope only
        }
        FixwireSdk.SetTag("sku", order.Sku);
        log.LogInformation("order received for {Sku}", order.Sku); // a breadcrumb

        // A traced call to the inventory service, carrying the trace.
        var reserved = await clients.CreateClient("inventory")
            .PostAsync(new Uri("/reservations?sku=" + order.Sku, UriKind.Relative), null);
        if (!reserved.IsSuccessStatusCode)
        {
            log.LogError("no stock for {Sku}", order.Sku); // an event, in this request
            return Results.Conflict(new { error = "out of stock" });
        }

        var orderId = "ord_" + Interlocked.Increment(ref _orders);
        try
        {
            Charge(order.Card);
        }
        catch (PaymentException e)
        {
            // Handled: the customer gets an answer, Fixwire gets the error with the order.
            FixwireSdk.WithScope(scope =>
            {
                scope.SetContext("order", new Dictionary<string, object?> { ["id"] = orderId, ["sku"] = order.Sku });
                FixwireSdk.CaptureException(new InvalidOperationException("charging order " + orderId, e));
            });
            return Results.Json(new { error = "payment declined" }, statusCode: StatusCodes.Status402PaymentRequired);
        }
        return Results.Created("/orders/" + orderId, new { id = orderId });
    }

    private static IResult Report()
    {
        int[] cents = []; // today's orders: none yet
        // A bug: with no orders this divides by zero. The exception escapes the app; Fixwire
        // reports it as a crash and ASP.NET Core answers 500.
        return Results.Ok(new { averageCents = cents.Sum() / cents.Length });
    }

    private static void Charge(string card)
    {
        if (card == "4000000000000002") // the test card that is always declined
        {
            throw new PaymentException("card_declined");
        }
    }
}

public sealed record Product(string Id, string Name, int PriceCents);

public sealed record OrderRequest(string Sku, string Card);

/// <summary>What the payment provider answers with.</summary>
public sealed class PaymentException(string code) : Exception("payment declined: " + code);
