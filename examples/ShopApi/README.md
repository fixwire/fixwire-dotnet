# Shop API (ASP.NET Core)

A small minimal API with Fixwire set up the way a production service would
be: one line of code and a configuration section.

```sh
Fixwire__Dsn=https://<key>@<host> dotnet run --project examples/ShopApi   # from the repository root
```

It reserves stock at an inventory service (`Inventory:Url`, default
`http://localhost:8081`; without one, orders fail at the reservation, and
that is reported too). Then (the port is in the startup log):

```sh
curl localhost:5000/products/sku_1                      # 200, with a database span
curl localhost:5000/products/nope                       # 404: not reported
curl -H 'X-User-Id: user-1' -H 'Content-Type: application/json' \
     -d '{"sku":"sku_1","card":"4242424242424242"}' localhost:5000/orders
curl -H 'X-User-Id: user-2' -H 'Content-Type: application/json' \
     -d '{"sku":"sku_1","card":"4000000000000002"}' localhost:5000/orders
curl localhost:5000/admin/report                        # an exception escapes: a crash, answered 500
```

What arrives in Fixwire:

- **The declined payment** as an error of `POST /orders`: the chain
  (`charging order …` caused by `PaymentException`), the user `user-2`, the
  `sku` tag, the order as context, and the breadcrumbs that led to it (the
  `order received` log line, the call to the inventory service). The
  customer got a 402; the error was handled.
- **A logged error**: `log.LogError("no stock for {Sku}", …)` is an event
  of its request, with its user and tags, when the inventory says sold out.
- **The crash** in `GET /admin/report` (`DivideByZeroException`): nothing
  in the app caught it, so it is reported as a crash and ASP.NET Core
  answers 500.
- **A trace per request**, named after its route (`GET /products/{id}`),
  with the database lookup and the call to the inventory service under it.
  The inventory service gets a `traceparent` header and continues the
  trace; other hosts get none.
- **Release health** for `shop-api@1.0.0`: each request is a session, ended
  well, with an error, or crashed.

How it is wired:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddFixwire();
```

```json
"Fixwire": {
  "Release": "shop-api@1.0.0",
  "TracesSampleRate": 1.0,
  "TracePropagationTargets": [ "http://localhost:8081" ]
}
```

In handlers, `FixwireSdk.SetUser`, `SetTag` and `CaptureException` act on
the current request only.
