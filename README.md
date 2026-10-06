<div align="center">

_Bugs reach production. Fixwire finds them first: errors, traces, logs and
AI agent runs in one place, an AI debugger on every plan, and your data
kept in Europe._

[![Discord](https://img.shields.io/badge/Discord-join%20us-5865F2?logo=discord&logoColor=white)](https://fixwire.io/discord)
[![Slack](https://img.shields.io/badge/Slack-community-4A154B?logo=slack&logoColor=white)](https://fixwire.io/slack)
[![X](https://img.shields.io/badge/X-follow%20us-000000?logo=x&logoColor=white)](https://fixwire.io/x)
[![Release](https://img.shields.io/github/v/release/fixwire/fixwire-dotnet?label=release)](https://github.com/fixwire/fixwire-dotnet/releases)
[![.NET](https://img.shields.io/badge/.NET-netstandard2.0%20%7C%20net8.0%20%7C%20net10.0-blue?logo=dotnet&logoColor=white)](https://github.com/fixwire/fixwire-dotnet/blob/main/.github/workflows/ci.yml)
[![CI](https://github.com/fixwire/fixwire-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/fixwire/fixwire-dotnet/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/fixwire/fixwire-dotnet/blob/main/LICENSE)

<br/>

</div>

# Fixwire SDK for .NET

Welcome to the official .NET SDK for **[Fixwire](https://fixwire.io)**. It
captures errors with their inner exceptions, crashes, traces, `ILogger`
records, release health, cron monitors and user feedback from your
ASP.NET Core apps, worker services and any other .NET program.

## 📦 Getting started

### Prerequisites

- A Fixwire account and project: sign up at
  [fixwire.io](https://fixwire.io).
- For `Fixwire` and `Fixwire.Extensions.Hosting`: .NET 8 or newer, or any
  runtime `netstandard2.0` reaches (.NET Framework 4.6.2+, Unity,
  Xamarin). For `Fixwire.AspNetCore`: .NET 8 or newer. CI tests on .NET 8
  and .NET 10 (Linux, macOS, Windows), and runs the core on .NET
  Framework 4.8.

### Installation

Pick the package for your kind of app; each brings what it builds on.

```sh
dotnet add package Fixwire.AspNetCore          # ASP.NET Core apps
dotnet add package Fixwire.Extensions.Hosting  # worker services and other .NET hosts
dotnet add package Fixwire                     # any other app: the core alone
```

| Package | For |
|---|---|
| `Fixwire` | Every app: errors, spans, sessions, check-ins, feedback, `HttpClient` tracing. No dependencies |
| `Fixwire.Extensions.Hosting` | Any .NET host (worker services): setup from configuration, `ILogger`, `IHttpClientFactory` |
| `Fixwire.AspNetCore` | ASP.NET Core apps (.NET 8+): everything `Fixwire.Extensions.Hosting` does, plus each request |

### Basic configuration

Call `FixwireSdk.Init` once, when the program starts:

```csharp
using Fixwire;

using var fixwire = FixwireSdk.Init(o =>
{
    o.Dsn = "https://fw_pk_live_…@ingest.eu.fixwire.io";
    o.Release = "api@1.4.0";
    o.Environment = "production";
    o.TracesSampleRate = 0.2; // keep a fifth of new traces
    // o.SendDefaultPii = true; // also send users' IP addresses and identifying headers
    // o.Redact = false;        // stop masking secrets and personal data on the device
});
```

The DSN is your project's publishable key and the ingest host:
`https://<publishable key>@<host>`. Without the `Dsn` option the SDK reads
`FIXWIRE_DSN`; without either it does nothing, so the same code runs in
tests and on your laptop. `FIXWIRE_RELEASE` and `FIXWIRE_ENVIRONMENT` work
the same way. `Init` never throws: a malformed DSN, or an option out of
range, is said on stderr and the SDK stays off, so a typo in configuration
can't stop the app. `Init` also reports exceptions nothing caught (and
unobserved task exceptions); disposing its result, or the process's exit,
waits briefly for what is left to be sent.

In ASP.NET Core and worker services, `builder.AddFixwire()` does all of
this from configuration; see
[Integrations](https://github.com/fixwire/fixwire-dotnet#-integrations).

### Quick usage example

```csharp
FixwireSdk.CaptureMessage("Hello Fixwire!"); // a message event, with the scope's user, tags and breadcrumbs

try
{
    await ChargeAsync(order);
}
catch (PaymentException e)
{
    FixwireSdk.CaptureException(e); // an issue: the exception, its inner exceptions and their stacks
}
```

Add who and what the work is for, and what happened before an error:

```csharp
FixwireSdk.ConfigureScope(s =>
{
    s.User = new User("user-1");
    s.SetTag("plan", "team");
});
FixwireSdk.AddBreadcrumb("cart", "checkout started");
FixwireSdk.CaptureMessage("disk usage above 90%", Level.Warning);
```

## ✨ Why Fixwire

- **Secrets stay on the device.** Secrets and personal data are masked
  before anything is sent, with the same rules as the Fixwire server
  (`Redact = false` turns it off).
- **A crash loop costs a few events and a count, not your quota.** Each
  issue sends a burst, then a few a minute; what is held back is counted
  (`ErrorBudget`).
- **It never gets in your app's way.** `Init` never throws, and captures
  never throw or block: one background loop sends from a bounded queue,
  retries with backoff and honours rate limits, pausing only the kind of
  data a limit names. Memory and time stay bounded, a failing
  `BeforeSend` or a `Message` or `ToString` that throws is caught, and the
  exit waits at most `ShutdownTimeout`.
- **OpenTelemetry-native.** It speaks the Fixwire protocol
  (OpenTelemetry's OTLP/HTTP plus a few small JSON endpoints): errors,
  messages and spans travel as OTLP/HTTP JSON, with structured stack
  traces, breadcrumbs and redaction on top.
- **Trace headers only where you allow.** Outgoing requests carry them
  only to your `TracePropagationTargets`; none by default.
- **Your data stays in Europe.** Fixwire is hosted in Europe.
- **No dependencies, ready for AOT.** The core depends on nothing and
  reaches .NET Framework through `netstandard2.0`; the packages are
  trimming- and AOT-safe (configuration is read without reflection), and
  the current scope and span flow with `await`.

## 🧩 Integrations

| Integration | What it does | How to use |
|---|---|---|
| ASP.NET Core | Each request gets its own scope, a release-health session and (with tracing on) a server span named after its route; exceptions that escape are reported as crashes, also those `UseExceptionHandler` or the developer page catch | `builder.AddFixwire()` with `Fixwire.AspNetCore` |
| .NET hosts (worker services) | Setup from the `Fixwire` configuration section, started at once so start-up errors are caught, flushed when the host stops | `builder.AddFixwire()` with `Fixwire.Extensions.Hosting` |
| `ILogger` | Records from `Information` become breadcrumbs; from `Error` they are sent as events, with their exception | Comes with `AddFixwire` |
| `IHttpClientFactory` | Every client's requests become client spans and breadcrumbs; trace headers go to your `TracePropagationTargets` only | Comes with `AddFixwire` |
| `HttpClient` | The same for a client you make yourself | `new HttpClient(new FixwireHttpMessageHandler(new HttpClientHandler()))` |
| Unhandled exceptions | Exceptions nothing caught are reported as fatal crashes and sent before the process ends; unobserved task exceptions too | On by default (`CaptureUnhandledExceptions`) |
| Tracing | Spans with W3C trace context, sent with the segment they belong to | `FixwireSdk.StartSpan("SELECT carts", "db.query")` |
| Cron monitors | Check-ins that tell a monitor when a job ran, how long it took and how it ended | `FixwireSdk.WithMonitorAsync("nightly-report", schedule, job)` |
| Feedback | What users say about an error or an AI answer | `FixwireSdk.CaptureFeedback(new Feedback("…"))` |

### ASP.NET Core

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddFixwire();
```

```json
{
  "Fixwire": {
    "Release": "shop@1.4.0",
    "TracesSampleRate": 0.2,
    "TracePropagationTargets": [ "https://inventory.internal" ]
  }
}
```

That's all; the DSN can stay out of `appsettings.json` (`Fixwire__Dsn` in
the environment). Each request gets its own scope, so what a handler sets
(`FixwireSdk.SetUser`, `SetTag`) stays with it. Exceptions that escape the
app are reported as crashes, also those `UseExceptionHandler` or the
developer page catch; a 404 the app answers is not reported. Each request is counted for release health and, with tracing on,
is a server span named after its route (`GET /items/{id}`) that continues
the caller's trace. `ILogger` records and `IHttpClientFactory` clients are
covered as in the table above. What is left is sent when the app stops.

### Worker services and other hosts

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.AddFixwire(o => o.Release = "worker@1.4.0"); // Fixwire.Extensions.Hosting
```

`AddFixwire` reads the `Fixwire` configuration section, then runs your
delegate, starts the SDK at once and flushes it when the host stops. It
never throws either: a malformed DSN is said on stderr and the app runs
without Fixwire.

### Logging with `ILogger`

With `AddFixwire`, `ILogger` records from `Information` become
breadcrumbs, and records from `Error` are sent as events: with the
exception you logged, or the message. Set the levels in the
`Fixwire:Logging` section:

```json
{
  "Fixwire": {
    "Logging": { "BreadcrumbLevel": "Debug", "EventLevel": "Critical" }
  }
}
```

An exception sent already (where it was caught, or by the ASP.NET Core
integration) is not sent again when it is logged.

### `HttpClient`

`IHttpClientFactory` clients are traced with `AddFixwire`. Wrap a client
you make yourself:

```csharp
var http = new HttpClient(new FixwireHttpMessageHandler(new HttpClientHandler()));
```

Outgoing requests become client spans and breadcrumbs, and carry trace
headers only to your `TracePropagationTargets`; headers the app set itself
win.

### Errors and crashes

An exception is sent with its inner exceptions and their stacks; async
methods and lambdas are named as you wrote them (`CheckoutAsync`, not
`<CheckoutAsync>d__5.MoveNext`). Frames of .NET, ASP.NET Core and
well-known libraries are marked as not yours (`InAppInclude` and
`InAppExclude` adjust it). An exception you capture without throwing it
gets the stack where you captured it. `FixwireSdk.LastEventId` is the id of
the last event sent, for a feedback form after a crash.

### Scopes and threads

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

`FixwireSdk.WithScope(s => …)` does the same for a block of synchronous
work.

### Tracing

Set `TracesSampleRate` above 0, then time the work that matters:

```csharp
using (var span = FixwireSdk.StartSpan("SELECT carts", "db.query"))
{
    span.SetAttribute("db.system", "postgresql");
    carts = await LoadCartsAsync();
}
```

A span without a parent in the process (a request, a job) is sent with the
spans under it when it ends. To continue a caller's trace from its W3C
headers (its sampling decision holds):

```csharp
using var span = FixwireSdk.SpanBuilder("handle order")
    .ContinueTrace(traceparent, tracestate, baggage)
    .Start();
```

### Cron monitors

```csharp
var schedule = MonitorConfig.Crontab("0 3 * * *");
schedule.Timezone = "Europe/Berlin";
await FixwireSdk.WithMonitorAsync("nightly-report", schedule, () => ReportAsync());
```

The run is checked in as in progress, then ok, or error when the job
throws (the exception goes on). The first check-in creates or updates the
monitor, so Fixwire also notices a night the job does not run at all.
`FixwireSdk.CaptureCheckIn` reports a run by hand.

### Feedback

`CaptureFeedback` sends what someone said about an error or an AI answer:
a message, a score from -1 (bad) to 1 (good), or both. A negative score on
a trace opens a `user_feedback` issue for the agent run:

```csharp
FixwireSdk.CaptureFeedback(new Feedback("Refunded the wrong order") { Score = -1, TraceId = runTraceId });
```

## ⚙️ Configuration

| Option | Default | What it does |
|---|---|---|
| `Dsn` | `FIXWIRE_DSN` | Where to send; nothing is sent without one |
| `Release` | `FIXWIRE_RELEASE` | The app's version (`api@1.4.0`, a commit SHA); release health needs one |
| `Environment` | `FIXWIRE_ENVIRONMENT`, else `production` | Where the app runs |
| `ServerName` | the machine's name | Names the machine |
| `ServiceName` | `OTEL_SERVICE_NAME`, else `api` of `api@1.4.0` | Names the service |
| `SampleRate` | 1 | Share of errors and messages sent |
| `TracesSampleRate` | 0 | Share of new traces kept; 0 turns tracing off |
| `TracePropagationTargets` | none | Hosts, URL prefixes and paths whose requests carry trace headers |
| `BeforeSend`, `BeforeBreadcrumb` | | Change an event or a breadcrumb, or drop it by returning `null` |
| `ErrorBudget` | 10 per issue, then 1 a minute; 600 a minute in all | Bounds the events sent; `Enabled = false` sends every one |
| `MaxBreadcrumbs` | 100 | Breadcrumbs kept per scope |
| `MaxValueLength` | 1,024 | Bytes of UTF-8 per string sent, cut on a character and ending in `...` within the limit (masked before the cut) |
| `MaxStackFrames` | 100 | Frames sent per exception, the newest kept |
| `SendDefaultPii` | off | Send the user's IP address and request headers that may identify them |
| `Redact` | on | Mask secrets and personal data on the device |
| `SensitiveKeys` | the server's (`password`, `token`, `cookie`, …) | Key fragments whose values are filtered whole; replaces the defaults |
| `InAppInclude`, `InAppExclude` | all but .NET's and well-known libraries' | Namespace prefixes that are your code, or not |
| `MaxQueue` | 100 | Requests waiting to be sent, and as many waiting for a retry; past them, new data is dropped |
| `Timeout` | 10 s | The timeout of a request to Fixwire |
| `HttpMessageHandler` | a handler of its own | Sends to Fixwire (for proxies and tests); the SDK's own follows no redirect, so the key goes to the DSN's host only |
| `AutoSessionTracking` | on, with a release | Counts requests for release health |
| `SessionInterval` | 1 minute | How often release health is sent |
| `CaptureUnhandledExceptions` | on | Report exceptions nothing caught, and unobserved task exceptions |
| `ShutdownTimeout` | 2 s | How long disposing, or the process's exit, waits to send |
| `Debug` | off | Logs what the SDK does to stderr |

### From configuration

With `AddFixwire`, these options come from the `Fixwire` configuration
section (`appsettings.json`, or the environment as `Fixwire__Dsn`,
`Fixwire__TracesSampleRate`, …): `Dsn`, `Release`, `Environment`,
`ServerName`, `ServiceName`, `SampleRate`, `TracesSampleRate`,
`SendDefaultPii`, `Redact`, `Debug`, `AutoSessionTracking`,
`CaptureUnhandledExceptions`, `MaxBreadcrumbs`, `MaxValueLength`,
`MaxStackFrames`, and the lists `TracePropagationTargets`, `InAppInclude`
and `InAppExclude` (a JSON array or a comma-separated string).
`Fixwire:Logging` sets the `ILogger` levels (`BreadcrumbLevel`,
`EventLevel`). Set the others, and callbacks, in the delegate:
`builder.AddFixwire(o => …)` runs after the configuration is read.

### Trace propagation targets

Trace headers go only to `TracePropagationTargets`, compared with the URL
without its user info, query and fragment:

- a target with `://` matches the URLs that start with it
  (`https://api.example.com/v2`);
- a target starting with `/` matches relative URLs whose path starts with
  it;
- any other is a host, with a port if it has one (`example.com`,
  `localhost:5000`), and matches that host and its subdomains in any case:
  `example.com` matches `api.example.com`, not `badexample.com` or
  `example.com.evil.net`.

### Before send

`BeforeSend` sees every error and message after the scope is applied, and
may change it or drop it:

```csharp
o.BeforeSend = e =>
{
    if (e.Transaction == "GET /healthz")
    {
        return null; // never report the health check
    }
    if (e.User != null)
    {
        e.User.Email = null;
    }
    return e;
};
```

If `BeforeSend` throws, the event is sent as it was; if `BeforeBreadcrumb`
throws, the breadcrumb is kept as it was. Either is said in the debug log,
and never reaches your app.

### Sampling

`SampleRate` keeps a share of errors and messages. `TracesSampleRate`
decides each new trace from its trace id, the same way in every Fixwire
SDK, so the services of one trace agree. A trace continued from a caller
follows the caller's decision.

### Error budget

Each issue may send 10 events at once (`PerIssueBurst`), then 1 a minute
(`PerIssuePerMinute`), within 600 a minute across issues (`PerMinute`).
Occurrences held back are counted and ride on the issue's next event, so
issue counts stay right. Set `ErrorBudget.Enabled = false` to send every
event.

### Redaction

Secrets (cloud and API keys, tokens, JWTs, private keys, passwords in
URLs, `password=…`-style assignments) and personal data (emails, card
numbers, IBANs, phone numbers, national ID numbers) are masked on the
device, with the same rules as the Fixwire server: in messages,
attributes, span names and status messages, breadcrumbs, feedback, URLs
and their queries, and the keys of dictionaries. Redaction runs before a
string is cut, over the part kept and the next 16 kB, so a secret the cut
goes through is still masked; a value redaction fails on is sent as
`[Filtered]`. The values of sensitive keys (`SensitiveKeys`) are filtered
whole. Your app's own configuration (release, environment, service and
server names, a monitor's slug and schedule) is cut to `MaxValueLength`
but never masked: `api@1.2.3.example` stays a release. Request headers
that may identify someone (`Authorization`, `Cookie`, `X-Forwarded-For`,
…) are sent only with `SendDefaultPii`.

### Limits

Values (contexts, extras, attributes, breadcrumb data) are walked 10
levels deep and 100 items wide, 10,000 lists and dictionaries at most; a
collection inside itself becomes `[Circular ~]`. An event over 1 MB leaves
out its breadcrumbs, then its contexts. A span keeps 128 attributes, a
segment 1,000 child spans; spans go in requests of at most 100 and 5 MB.
A request is sent at most 4 times, and one told to wait over 5 minutes is
dropped. A caller's `traceparent` is used only when it is well formed, and
its `tracestate` (512 bytes) and `baggage` (8,192 bytes) are passed on only
within W3C's limits.

## 🧪 Examples

Real apps, run by their tests against a fake ingest, so they keep working:

- [ShopApi](https://github.com/fixwire/fixwire-dotnet/tree/main/examples/ShopApi):
  an ASP.NET Core minimal API set up with `builder.AddFixwire()` and
  `appsettings.json` alone: per-request users and tags, a handled error
  with context, a crash, a database span, a traced call to another
  service, `ILogger` breadcrumbs and events, release health.
- [NightlyReport](https://github.com/fixwire/fixwire-dotnet/tree/main/examples/NightlyReport):
  a worker-service cron job with check-ins to a monitor, `ILogger` errors
  as events, one scope per account, a trace for the run, flushed when the
  host stops.

## 📚 Documentation

The full guide lives in this README and the examples.

- [Configuration](https://github.com/fixwire/fixwire-dotnet#%EF%B8%8F-configuration)
- [Examples](https://github.com/fixwire/fixwire-dotnet/tree/main/examples)
- [Changelog](https://github.com/fixwire/fixwire-dotnet/blob/main/CHANGELOG.md)
- [Security policy](https://github.com/fixwire/fixwire-dotnet/blob/main/SECURITY.md)
- [Contributing guide](https://github.com/fixwire/fixwire-dotnet/blob/main/CONTRIBUTING.md)

## 🚧 Coming from another error tracker?

The API follows the shape most error-tracking SDKs share: `Init`,
`CaptureException`, `CaptureMessage`, `SetUser`, `SetTag`,
`AddBreadcrumb`, `StartSpan`, scopes and hubs. Moving over is mostly a
change of package and DSN. As .NET has it, options are set in a delegate,
`Init` returns an `IDisposable` that flushes, hosts use
`builder.AddFixwire()` with the `Fixwire` configuration section, and the
current hub and span flow with `await` (`Hub.Current`, `Bind`).

## 🙌 Want to contribute?

We'd love your help, from a typo fix to a new integration. Read the
[contributing guide](https://github.com/fixwire/fixwire-dotnet/blob/main/CONTRIBUTING.md),
then pick one of the
[open issues](https://github.com/fixwire/fixwire-dotnet/issues) or a
[good first issue](https://github.com/fixwire/fixwire-dotnet/issues?q=is%3Aopen+label%3A%22good+first+issue%22).

## 🛟 Need help?

- Questions: ask on [Discord](https://fixwire.io/discord) or
  [Slack](https://fixwire.io/slack).
- Bugs: open a [GitHub issue](https://github.com/fixwire/fixwire-dotnet/issues).

Found a security issue? Please don't open an issue; follow the
[security policy](https://github.com/fixwire/fixwire-dotnet/blob/main/SECURITY.md).

## 🔗 Resources

- [Website](https://fixwire.io)
- [Pricing](https://fixwire.io/pricing)
- [Discord](https://fixwire.io/discord)
- [Slack](https://fixwire.io/slack)
- [X](https://fixwire.io/x)
- [Changelog](https://github.com/fixwire/fixwire-dotnet/blob/main/CHANGELOG.md)
- [Examples](https://github.com/fixwire/fixwire-dotnet/tree/main/examples)
- [Security policy](https://github.com/fixwire/fixwire-dotnet/blob/main/SECURITY.md)

## 📃 License

The SDK is open source under the MIT license; see
[LICENSE](https://github.com/fixwire/fixwire-dotnet/blob/main/LICENSE).

## 😘 Contributors

Thanks to everyone who helps make Fixwire better!

<a href="https://github.com/fixwire/fixwire-dotnet/graphs/contributors"><img src="https://contrib.rocks/image?repo=fixwire/fixwire-dotnet" alt="Contributors" /></a>
