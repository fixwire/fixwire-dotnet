# Changelog

All notable changes to the Fixwire .NET SDK are listed here. Versions follow [Semantic
Versioning](https://semver.org); before 1.0, a minor version may change the
API.

## [Unreleased]

- The error budget forgets the least recently seen of its 1,024 issues in constant time, reusing its memory: a new issue no longer scans them all.

## [0.1.1] - 2026-10-06

- `Init` (and `new Client`, `AddFixwire`) never throws: a malformed DSN (it threw `ArgumentException`), a `Timeout` over 24 days or a `SessionInterval` over 49 days is said on stderr, debug or not, and the SDK stays off.
- Capturing never throws at the app: not for an exception whose `Message` throws, one exception captured on several threads at once, or a message the error budget's fingerprint timed out on (`"@@@…"`; on the finalizer thread that ended the process).
- Redaction stays linear on hostile text: JWTs are found by a scanner (`"eyJ-eyJ-…"` was quadratic), and thousands of findings in one text no longer take seconds. Span status messages are redacted too.
- What is sent is bounded, with the numbers every Fixwire SDK uses: strings are at most `MaxValueLength` (new, default 1,024) bytes of UTF-8, cut on a character with `...` within the limit, and masked before the cut over 16 kB more, so a private key or JWT the cut goes through is still found. The app's configuration (release, environment, monitor slugs and schedules) is cut too, in sessions, check-ins and feedback as well, but never masked. Values are 10 levels deep, 100 items wide and 10,000 lists and dictionaries read at most; a collection inside itself becomes `[Circular ~]`, one deeper (or past the 10,000) `[Object]` or `[Array]`, a throwing `ToString` or enumeration `[Unreadable]`, NaN and infinities `"NaN"`, `"Infinity"`, `"-Infinity"`.
- An event over 1 MB goes without its breadcrumbs, then without its contexts, else is dropped (extras are no longer shed). Spans go in requests of at most 100 and 5 MB; a span keeps 128 attributes. A sessions request holds at most 5,000 aggregates.
- `MaxStackFrames` (new, default 100): the newest frames of each exception; an event made by hand is held to 10 exceptions and these frames too.
- The transport follows no redirects, never reads answer bodies, drops a request told to wait over 5 minutes instead of holding its queue place, and reads `Retry-After` as seconds or a date, a day at most (larger ones, even past what .NET's parser takes, are a day), as `Fixwire-Rate-Limits` too, at the protocol's categories. A 5xx with `Retry-After` pauses all data for that long. Retries wait about 1 s, 2 s, 4 s (they waited twice as long); `MaxQueue` requests wait to be sent and as many for a retry, so retries no longer crowd new data out.
- `Dispose` returns within `ShutdownTimeout` (it could wait a second more for the sender). A null tag, extra, context or attribute key, or a null breadcrumb, is ignored rather than thrown back; a failing `BeforeBreadcrumb` is said in the debug log.
- `Fixwire.Extensions.Hosting` reads `MaxValueLength` and `MaxStackFrames` from configuration.
- Trace context: a `traceparent` is used only when strictly well formed (version `00`, lower-case hex, nothing after the flags); `tracestate` over 512 bytes and `baggage` over 8,192 bytes (they counted characters), or with a control character, are dropped whole.
- `TracePropagationTargets` match hosts, not substrings: a URL without its user info, query and fragment matches a target with `://` it starts with, a `/` target as a relative URL's path, or any other target as its host (with a port if given) or a subdomain of it: `example.com` matches `api.example.com`, not `badexample.com` or `example.com.evil.net`.
- Redaction follows the server's new `secret_assignment`: a name may end a longer one (`access_token`, `client_secret`, `csrfToken`, `PHPSESSID`, `X-Amz-Signature`), with secret and private keys, credentials, session ids, signatures and an OAuth `code` in a query or fragment; the pattern stays linear (atomic groups). Keys that mask alike are numbered in linear time. Text redaction fails on is sent as `[Filtered]` (an event was dropped), span operations are masked too, and feedback is masked before it is cut.
- `Fixwire.Extensions.Hosting`: a record logged while one is captured (by `BeforeSend`, `BeforeBreadcrumb`) is not captured again (it recursed until the stack overflowed), and logging never throws because of Fixwire.
- Release health keeps at most 5,000 users between sends (more count without their user); its timer can no longer end the process.

## [0.1.0] - 2026-10-06

First release.

- `Fixwire` (netstandard2.0 and net8.0, no dependencies): errors with their inner exceptions, scopes and spans that follow `await`, request sessions, cron monitors, feedback and `HttpClient` tracing.
- `Fixwire.Extensions.Hosting`: setup from configuration, `ILogger` breadcrumbs and events, `IHttpClientFactory` clients traced.
- `Fixwire.AspNetCore`: each request as a scope, a session and a route-named span; exceptions the app's handlers catch still reported.
- On-device redaction with the server's rules; an error budget for crash loops.
- Examples run against a fake ingest in CI: an ASP.NET Core API and a worker-service cron job.
