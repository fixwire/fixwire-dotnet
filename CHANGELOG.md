# Changelog

All notable changes to the Fixwire .NET SDK are listed here. Versions follow [Semantic
Versioning](https://semver.org); before 1.0, a minor version may change the
API.

## [Unreleased]

- Capturing never throws at the app: not for an exception whose `Message` throws, one exception captured on several threads at once, or a message the error budget's fingerprint timed out on (`"@@@…"`; on the finalizer thread that ended the process).
- Redaction stays linear on hostile text: JWTs are found by a scanner (`"eyJ-eyJ-…"` was quadratic), and thousands of findings in one text no longer take seconds. Span status messages are redacted too.
- What is sent is bounded: strings are cut at 16 KB (redaction reads twice as far), collections inside a value at 1,000 items; a collection inside itself becomes `[Circular ~]`, a throwing `ToString` or enumeration `[Unreadable]`. An event over 1 MB goes without breadcrumbs, contexts and extras; spans over 5 MB go in several requests.
- The transport follows no redirects, never reads answer bodies, drops a request told to wait over 5 minutes instead of holding its queue place, reads `Retry-After` dates, and caps `Fixwire-Rate-Limits` at a day and at the protocol's categories.
- `tracestate` over 512 characters and `baggage` over 8,192, or with control characters, are not passed on; a propagation target in a URL's query no longer counts.
- `Fixwire.Extensions.Hosting`: a record logged while one is captured (by `BeforeSend`, `BeforeBreadcrumb`) is not captured again (it recursed until the stack overflowed), and logging never throws because of Fixwire.
- Release health keeps at most 5,000 users between sends (more count without their user); its timer can no longer end the process.

## [0.1.0] - 2026-10-06

First release.

- `Fixwire` (netstandard2.0 and net8.0, no dependencies): errors with their inner exceptions, scopes and spans that follow `await`, request sessions, cron monitors, feedback and `HttpClient` tracing.
- `Fixwire.Extensions.Hosting`: setup from configuration, `ILogger` breadcrumbs and events, `IHttpClientFactory` clients traced.
- `Fixwire.AspNetCore`: each request as a scope, a session and a route-named span; exceptions the app's handlers catch still reported.
- On-device redaction with the server's rules; an error budget for crash loops.
- Examples run against a fake ingest in CI: an ASP.NET Core API and a worker-service cron job.
