// A cron job reporting to Fixwire: check-ins tell its monitor when it ran and how it ended, a
// failing account is logged (and so reported) and the job carries on, and a summary warning goes
// out at the end.
//
//   Fixwire__Dsn=https://<key>@<host> dotnet run --project examples/NightlyReport
using Fixwire;

var host = NightlyReport.Build(args);
var result = host.Services.GetRequiredService<JobResult>(); // the host is disposed once it has run
await host.RunAsync();
return result.ExitCode;

/// <summary>The job's host, built apart from running it so that its test can run it too.</summary>
public static class NightlyReport
{
    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        // Fixwire from the "Fixwire" section of appsettings.json (and Fixwire__Dsn and the like);
        // flushed when the host stops.
        builder.AddFixwire();
        builder.Services.AddSingleton<JobResult>();
        builder.Services.AddHostedService<ReportJob>();
        return builder.Build();
    }
}

/// <summary>Builds the report of each account, once, then stops the host.</summary>
public sealed class ReportJob(ILogger<ReportJob> log, IHostApplicationLifetime lifetime, JobResult result) : BackgroundService
{
    public static readonly string[] Accounts = ["acme", "globex", "initech"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // The first check-in creates the monitor: every night at 3, in Berlin.
            var monitor = MonitorConfig.Crontab("0 3 * * *");
            monitor.Timezone = "Europe/Berlin";
            monitor.CheckInMargin = 10;
            monitor.MaxRuntime = 30;
            await FixwireSdk.WithMonitorAsync("nightly-report", monitor, () => ReportAllAsync(stoppingToken));
        }
        catch (InvalidOperationException e)
        {
            log.LogInformation("the job failed: {Reason}", e.Message);
            result.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    private async Task ReportAllAsync(CancellationToken stoppingToken)
    {
        var failed = 0;
        using (FixwireSdk.StartSpan("nightly-report", "task"))
        {
            foreach (var account in Accounts)
            {
                using var span = FixwireSdk.StartSpan("report " + account, "task");
                try
                {
                    await ReportAsync(account, stoppingToken);
                }
                catch (NoInvoicesException e)
                {
                    failed++;
                    span.SetError(e);
                    // One scope per account, so its tag doesn't stay on the next.
                    FixwireSdk.WithScope(scope =>
                    {
                        scope.SetTag("account", account);
                        log.LogError(e, "the report for {Account} failed", account);
                    });
                }
            }
        }
        if (failed > 0)
        {
            FixwireSdk.CaptureMessage($"nightly report: {failed} of {Accounts.Length} accounts failed", Level.Warning);
            throw new InvalidOperationException($"{failed} accounts failed");
        }
    }

    private async Task ReportAsync(string account, CancellationToken stoppingToken)
    {
        log.LogInformation("building the report for {Account}", account); // a breadcrumb
        await Task.Delay(5, stoppingToken);
        if (account == "globex")
        {
            throw new NoInvoicesException(account);
        }
    }
}

/// <summary>How the run ended: the program's exit code.</summary>
public sealed class JobResult
{
    public int ExitCode { get; set; }
}

/// <summary>What an account without invoices gives.</summary>
public sealed class NoInvoicesException(string account) : Exception("no invoices for " + account);
