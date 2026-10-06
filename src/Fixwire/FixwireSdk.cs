namespace Fixwire;

/// <summary>
/// The Fixwire SDK for .NET: errors, traces, release health, cron check-ins and feedback, sent over
/// Fixwire protocol v1 (OpenTelemetry's OTLP plus a few Fixwire endpoints).
/// <code>
/// using var fixwire = FixwireSdk.Init(o =>
/// {
///     o.Dsn = "https://fw_pk_live_…@ingest.eu.fixwire.io";
///     o.Release = "api@1.4.0";
/// });
/// try { Charge(order); }
/// catch (PaymentException e) { FixwireSdk.CaptureException(e); }
/// </code>
/// Without a DSN (and without <c>FIXWIRE_DSN</c>) the SDK does nothing. Captures never block:
/// a background loop sends, and the process's exit waits briefly for what is left.
/// </summary>
public static class FixwireSdk
{
    private static readonly object InitLock = new();
    private static bool _handlersInstalled;

    /// <summary>
    /// Sets the SDK up; disposing the result flushes and stops it. It never throws: a malformed DSN,
    /// or an option out of range, is said on stderr and the SDK stays off.
    /// </summary>
    /// <param name="configure">Sets the options.</param>
    public static IDisposable Init(Action<FixwireOptions> configure)
    {
        var o = new FixwireOptions();
        configure(o);
        return Init(o);
    }

    /// <summary>
    /// Sets the SDK up: the main hub gets a client for the options; a second call replaces the
    /// first's. It never throws: a malformed DSN, or an option out of range, is said on stderr and
    /// the SDK stays off.
    /// </summary>
    /// <param name="options">The options.</param>
    public static IDisposable Init(FixwireOptions options)
    {
        var client = new Client(options);
        Client? before;
        lock (InitLock)
        {
            before = Hub.Main.Client;
            Hub.Main.Client = client;
            if (client.Enabled && options.CaptureUnhandledExceptions && !_handlersInstalled)
            {
                _handlersInstalled = true;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            }
        }
        before?.Dispose();
        return new Hub.Restore(() => Close(client));
    }

    private static void Close(Client client)
    {
        lock (InitLock)
        {
            if (Hub.Main.Client == client)
            {
                Hub.Main.Client = null;
            }
        }
        client.Dispose();
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception e && Hub.Main.Client is { } client)
        {
            Hub.Current.CaptureException(e, "UnhandledException", handled: false, Level.Fatal);
            client.FlushAsync(client.Options.ShutdownTimeout).GetAwaiter().GetResult();
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        var e = args.Exception.InnerExceptions.Count == 1 ? args.Exception.InnerExceptions[0] : args.Exception;
        Hub.Main.CaptureException(e, "UnobservedTaskException", handled: false, Level.Error);
    }

    private static void OnProcessExit(object? sender, EventArgs args)
    {
        if (Hub.Main.Client is { } client)
        {
            client.FlushAsync(client.Options.ShutdownTimeout).GetAwaiter().GetResult();
        }
    }

    /// <summary>Whether the SDK sends (it was set up with a DSN).</summary>
    public static bool IsEnabled => Hub.Current.Client is { Enabled: true };

    /// <summary>The id of the last event sent, such as for a feedback form after a crash.</summary>
    public static string? LastEventId => Hub.LastEventId;

    /// <summary>Waits until what was captured is sent, or the timeout. Call it before a short-lived program exits on its own.</summary>
    /// <param name="timeout">The longest wait.</param>
    /// <returns>False when time ran out.</returns>
    public static Task<bool> FlushAsync(TimeSpan timeout) => Hub.Current.FlushAsync(timeout);

    /// <summary>Sends an exception and its inner exceptions.</summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The event's id, or null when it was not sent.</returns>
    public static string? CaptureException(Exception exception) => Hub.Current.CaptureException(exception);

    /// <summary>Sends a message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="level">Its level; by default the scope's, else info.</param>
    /// <returns>The event's id, or null when it was not sent.</returns>
    public static string? CaptureMessage(string message, Level? level = null) => Hub.Current.CaptureMessage(message, level);

    /// <summary>Sends an event as it is, with what the scope knows.</summary>
    /// <param name="e">The event.</param>
    /// <returns>Its id, or null when it was not sent.</returns>
    public static string? CaptureEvent(FixwireEvent e) => Hub.Current.CaptureEvent(e);

    /// <summary>Records something that happened.</summary>
    /// <param name="breadcrumb">The breadcrumb.</param>
    public static void AddBreadcrumb(Breadcrumb breadcrumb) => Hub.Current.AddBreadcrumb(breadcrumb);

    /// <summary>Records something that happened.</summary>
    /// <param name="category">What it is about, such as <c>cart</c>.</param>
    /// <param name="message">What happened.</param>
    public static void AddBreadcrumb(string category, string message) =>
        Hub.Current.AddBreadcrumb(new Breadcrumb(category, message));

    /// <summary>Changes the current scope.</summary>
    /// <param name="change">What to change.</param>
    public static void ConfigureScope(Action<Scope> change) => change(Hub.Current.Scope);

    /// <summary>Runs something with a copy of the current scope: what it sets there is gone afterwards.</summary>
    /// <param name="work">What to run.</param>
    public static void WithScope(Action<Scope> work) => Hub.Current.WithScope(work);

    /// <summary>Sets who the work is for.</summary>
    /// <param name="user">The user, or null.</param>
    public static void SetUser(User? user) => Hub.Current.Scope.User = user;

    /// <summary>Sets a searchable tag.</summary>
    /// <param name="key">The tag.</param>
    /// <param name="value">Its value.</param>
    public static void SetTag(string key, string? value) => Hub.Current.Scope.SetTag(key, value);

    /// <summary>Sets a named group of details.</summary>
    /// <param name="name">The group.</param>
    /// <param name="values">Its details.</param>
    public static void SetContext(string name, IDictionary<string, object?>? values) => Hub.Current.Scope.SetContext(name, values);

    /// <summary>Sets a detail sent with events.</summary>
    /// <param name="key">The detail.</param>
    /// <param name="value">Its value.</param>
    public static void SetExtra(string key, object? value) => Hub.Current.Scope.SetExtra(key, value);

    /// <summary>Starts a span under the current one (or a new trace), current until it is disposed.</summary>
    /// <param name="name">The span's name.</param>
    /// <param name="op">Its operation, such as <c>db.query</c>.</param>
    public static Span StartSpan(string name, string op) => Hub.Current.SpanBuilder(name).WithOp(op).Start();

    /// <summary>A span builder, for kinds, attributes, other parents and continued traces.</summary>
    /// <param name="name">The span's name.</param>
    public static SpanBuilder SpanBuilder(string name) => Hub.Current.SpanBuilder(name);

    /// <summary>Reports a run of a scheduled job by hand (see <see cref="WithMonitorAsync{T}"/>).</summary>
    /// <param name="checkIn">The check-in.</param>
    /// <returns>Its id, or null when it was not sent.</returns>
    public static string? CaptureCheckIn(CheckIn checkIn) => Hub.Current.Client?.CaptureCheckIn(checkIn);

    /// <summary>Runs a job as a run of a monitor: in progress, then ok, or error when it throws (the exception goes on).</summary>
    /// <param name="monitor">The monitor's slug.</param>
    /// <param name="config">Creates or updates the monitor; may be null.</param>
    /// <param name="job">The job.</param>
    /// <typeparam name="T">What the job returns.</typeparam>
    public static async Task<T> WithMonitorAsync<T>(string monitor, MonitorConfig? config, Func<Task<T>> job)
    {
        var id = CaptureCheckIn(new CheckIn(monitor, CheckInStatus.InProgress) { Config = config });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var status = CheckInStatus.Error;
        try
        {
            var result = await job().ConfigureAwait(false);
            status = CheckInStatus.Ok;
            return result;
        }
        finally
        {
            if (id != null)
            {
                CaptureCheckIn(new CheckIn(monitor, status) { Id = id, Duration = watch.Elapsed });
            }
        }
    }

    /// <summary>Runs a job as a run of a monitor: in progress, then ok, or error when it throws (the exception goes on).</summary>
    /// <param name="monitor">The monitor's slug.</param>
    /// <param name="config">Creates or updates the monitor; may be null.</param>
    /// <param name="job">The job.</param>
    public static Task WithMonitorAsync(string monitor, MonitorConfig? config, Func<Task> job) =>
        WithMonitorAsync<bool>(monitor, config, async () =>
        {
            await job().ConfigureAwait(false);
            return true;
        });

    /// <summary>Sends what someone said about an error or an AI answer.</summary>
    /// <param name="feedback">The feedback.</param>
    /// <returns>Its id, or null when it holds neither a message nor a score.</returns>
    public static string? CaptureFeedback(Feedback feedback) => Hub.Current.CaptureFeedback(feedback);
}
