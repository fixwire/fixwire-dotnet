# Nightly report (a worker-service cron job)

```sh
Fixwire__Dsn=https://<key>@<host> dotnet run --project examples/NightlyReport   # from the repository root
```

The job builds a report per account. One account (`globex`) has no
invoices: the job logs the failure, carries on with the others, sends a
summary warning and exits 1.

What arrives in Fixwire:

- **Check-ins** for the `nightly-report` monitor: `in_progress` when the
  run starts and `error` when it ends, with its duration. The first
  check-in creates the monitor (every night at 3, Berlin time, 10 minutes'
  margin, 30 minutes at most), so Fixwire also notices a night the job does
  not run at all.
- **The failure**, from `log.LogError(e, …)` with its exception
  (`NoInvoicesException`), tagged `account: globex`, with the `building the
  report for globex` log line as a breadcrumb.
- **The summary warning**, without the account's tag: each account had its
  own scope (`FixwireSdk.WithScope`).
- **A trace for the run**, with a span per account; the failure is linked
  to it.

How it is wired:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.AddFixwire(); // the "Fixwire" section of appsettings.json; flushed when the host stops
builder.Services.AddHostedService<ReportJob>();
```

```csharp
var schedule = MonitorConfig.Crontab("0 3 * * *");
schedule.Timezone = "Europe/Berlin";
await FixwireSdk.WithMonitorAsync("nightly-report", schedule, () => ReportAllAsync(stoppingToken));
```
