# Fixwire for .NET

[![CI](https://github.com/fixwire/fixwire-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/fixwire/fixwire-dotnet/actions/workflows/ci.yml)

The Fixwire SDK for .NET: errors with their inner exceptions, traces,
release health, cron monitors and feedback. The core targets
`netstandard2.0` (.NET Framework 4.6.2+, Unity, Xamarin) and `net8.0`, and
depends on nothing.

| Package | For |
|---|---|
| `Fixwire` | Every app: errors, spans, sessions, check-ins, feedback, `HttpClient` tracing |
| `Fixwire.AspNetCore` | ASP.NET Core apps (.NET 8+): everything below, plus each request |
| `Fixwire.Extensions.Hosting` | Any .NET host (worker services): setup from configuration, `ILogger`, `IHttpClientFactory` |

## ASP.NET Core

```sh
dotnet add package Fixwire.AspNetCore
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddFixwire();
```

```json
// appsettings.json (the DSN can stay out of it: Fixwire__Dsn in the environment)
"Fixwire": {
  "Release": "shop@1.4.0",
  "TracesSampleRate": 0.2,
  "TracePropagationTargets": [ "https://inventory.internal" ]
}
```

That's all. Each request gets its own scope (what a handler sets stays with
it), exceptions that escape the app are reported as crashes, also those
`UseExceptionHandler` or the developer page catch, each request is counted
for release health and, with tracing on, is a server span named after its
route (`GET /items/{id}`) that continues the caller's trace. `ILogger`
records become breadcrumbs (from Information) and events (from Error), and
`IHttpClientFactory` clients are traced. What is left is sent when the app
stops.

## Worker services and console apps

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.AddFixwire(); // Fixwire.Extensions.Hosting
```

Without a host:

```csharp
using var fixwire = FixwireSdk.Init(o =>
{
    o.Dsn = "https://fw_pk_live_…@ingest.eu.fixwire.io";
    o.Release = "api@1.4.0";
});
```

Without a DSN (and without `FIXWIRE_DSN`) the SDK does nothing. `Init` never
throws: a malformed DSN, or an option out of range, is said on stderr and the
SDK stays off, so a typo in configuration can't stop the app. `Init` also
reports exceptions nothing caught (and unobserved task exceptions), and the
process's exit waits briefly for what is left to be sent.

**What's different**
- Secrets and personal data are masked on the device, with the same rules
  as the Fixwire server (`Redact = false` turns it off).
- A crash loop costs a few events and a count, not your quota
  (`ErrorBudget`).
- Captures never block: one background loop sends from a bounded queue,
  retries with backoff and honours rate limits, pausing only the kind of
  data a limit names.
- It speaks the Fixwire protocol: errors, messages and spans travel as
  OpenTelemetry's OTLP/HTTP (JSON), with structured stack traces,
  breadcrumbs and redaction on top.

## Errors

```csharp
try { await ChargeAsync(order); }
catch (PaymentException e) { FixwireSdk.CaptureException(e); }

FixwireSdk.ConfigureScope(s =>
{
    s.User = new User("user-1");
    s.SetTag("plan", "team");
});
FixwireSdk.AddBreadcrumb("cart", "checkout started");
FixwireSdk.CaptureMessage("disk usage above 90%", Level.Warning);
```

An exception is sent with its inner exceptions and their stacks; async
methods and lambdas are named as you wrote them (`CheckoutAsync`, not
`<CheckoutAsync>d__5.MoveNext`). Frames of .NET, ASP.NET Core and
well-known libraries are marked as not yours (`InAppInclude` and
`InAppExclude` adjust it). An exception you capture without throwing it
gets the stack where you captured it.

## Scopes and threads

The current scope and span flow with `await`, `Task.Run` and the thread
pool, so concurrent requests and tasks each keep their own. Give a job or a
message its own copy:

```csharp
using (Hub.Current.Clone().Bind())
{
    FixwireSdk.SetTag("order", order.Id);
    await HandleAsync(order);
}
```

## Tracing

```csharp
o.TracesSampleRate = 0.2;

using (var span = FixwireSdk.StartSpan("SELECT carts", "db.query"))
{
    …
}
```

A span without a parent in the process is sent with the spans under it
when it ends. `FixwireSdk.SpanBuilder(name).ContinueTrace(traceparent,
tracestate, baggage).Start()` continues a caller's trace; its sampling
decision holds. Outgoing requests become client spans and breadcrumbs:

```csharp
var http = new HttpClient(new FixwireHttpMessageHandler(new HttpClientHandler()));
```

Trace headers go only to `TracePropagationTargets`, compared with a URL
without its user info, query and fragment: a target with `://` is a URL
prefix (`https://api.example.com/v2`), one starting with `/` a path of
relative URLs, and any other a host, with a port if it has one, matching that
host and its subdomains (`example.com` matches `api.example.com`, not
`badexample.com` or `example.com.evil.net`).

## Cron jobs and feedback

```csharp
var schedule = MonitorConfig.Crontab("0 3 * * *");
schedule.Timezone = "Europe/Berlin";
await FixwireSdk.WithMonitorAsync("nightly-report", schedule, () => ReportAsync());

FixwireSdk.CaptureFeedback(new Feedback("Refunded the wrong order") { Score = -1, TraceId = runTraceId });
```

A negative score opens a `user_feedback` issue for the agent run.

## Options

| Option | Default | |
|---|---|---|
| `Dsn` | `FIXWIRE_DSN` | Where to send; nothing is sent without one |
| `Release`, `Environment` | `FIXWIRE_RELEASE`, `production` | Release health needs a release |
| `ServiceName` | `OTEL_SERVICE_NAME`, else `api` of `api@1.4.0` | |
| `SampleRate` | 1 | Share of errors sent |
| `TracesSampleRate` | 0 | Share of new traces kept |
| `TracePropagationTargets` | none | Hosts, URL prefixes and paths that receive trace headers |
| `BeforeSend`, `BeforeBreadcrumb` | | Change or drop events and breadcrumbs |
| `SendDefaultPii` | off | Send the user's IP address and identifying headers |
| `Redact`, `SensitiveKeys` | on, the server's keys | On-device masking |
| `ErrorBudget` | 10 per issue, then 1 a minute; 600 a minute | |
| `InAppInclude`, `InAppExclude` | all but .NET's and known libraries' | Which frames are your code |
| `MaxValueLength` | 1,024 | Bytes of UTF-8 a string keeps (cut on a character, `...` within); masked first |
| `MaxStackFrames` | 100 | Frames sent per exception, the newest |
| `MaxBreadcrumbs`, `MaxQueue` | 100, 100 | Breadcrumbs kept; requests waiting to be sent (and as many for a retry) |
| `CaptureUnhandledExceptions` | on | Report exceptions nothing caught |
| `ShutdownTimeout` | 2 s | How long the exit waits to send |

With `AddFixwire`, each option can come from the `Fixwire` configuration
section (`Fixwire:TracesSampleRate`, `Fixwire__Dsn`, …); `Fixwire:Logging`
sets the `ILogger` levels (`BreadcrumbLevel`, `EventLevel`).

## Examples

[examples](examples) holds real apps, run by `Examples.Tests` against a fake
ingest: an ASP.NET Core API ([ShopApi](examples/ShopApi)) and a worker-service
cron job ([NightlyReport](examples/NightlyReport)).

## Building

```sh
dotnet build && dotnet test                  # .NET 10 SDK, and the .NET 8 runtime
dotnet format Fixwire.slnx --verify-no-changes
cd smoke && dotnet run                       # Windows: the library on .NET Framework
```

## License

MIT.
