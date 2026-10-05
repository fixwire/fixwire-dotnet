# Examples

Real apps, each with its own README. `Examples.Tests` starts each against a
fake ingest and checks what Fixwire receives, so they keep working
(`dotnet test` in `sdks/dotnet` runs it).

| Example | Shows |
|---|---|
| [ShopApi](ShopApi) | ASP.NET Core minimal API: `builder.AddFixwire()` and `appsettings.json`, nothing else; per-request users and tags; a handled error with context; an exception that escapes reported as a crash (answered 500); 404s not reported; a database span; a traced `IHttpClientFactory` call to another service, with trace headers sent only to it; `ILogger` breadcrumbs and events; release health |
| [NightlyReport](NightlyReport) | A worker-service cron job: check-ins to a monitor (created from the first one), `ILogger` errors as events, one scope per account, carrying on after a failure, a summary warning, a trace for the run, flushed when the host stops |
