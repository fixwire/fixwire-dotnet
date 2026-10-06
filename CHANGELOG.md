# Changelog

All notable changes to the Fixwire .NET SDK are listed here. Versions follow [Semantic
Versioning](https://semver.org); before 1.0, a minor version may change the
API.

## [0.1.0] - 2026-10-06

First release.

- `Fixwire` (netstandard2.0 and net8.0, no dependencies): errors with their inner exceptions, scopes and spans that follow `await`, request sessions, cron monitors, feedback and `HttpClient` tracing.
- `Fixwire.Extensions.Hosting`: setup from configuration, `ILogger` breadcrumbs and events, `IHttpClientFactory` clients traced.
- `Fixwire.AspNetCore`: each request as a scope, a session and a route-named span; exceptions the app's handlers catch still reported.
- On-device redaction with the server's rules; an error budget for crash loops.
- Examples run against a fake ingest in CI: an ASP.NET Core API and a worker-service cron job.
