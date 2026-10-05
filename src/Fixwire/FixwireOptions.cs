namespace Fixwire;

/// <summary>
/// The SDK's options. Only the DSN is needed; without one (and without <c>FIXWIRE_DSN</c>) the SDK
/// does nothing. Set them before <see cref="FixwireSdk.Init(Action{FixwireOptions})"/>.
/// </summary>
public sealed class FixwireOptions
{
    /// <summary>The project's DSN, <c>https://&lt;key&gt;@&lt;host&gt;</c>; <c>FIXWIRE_DSN</c> when not set.</summary>
    public string? Dsn { get; set; }

    /// <summary>The app's version, such as <c>api@1.4.0</c>; <c>FIXWIRE_RELEASE</c> when not set. Release health needs one.</summary>
    public string? Release { get; set; }

    /// <summary>Where the app runs; <c>FIXWIRE_ENVIRONMENT</c>, else <c>production</c>.</summary>
    public string? Environment { get; set; }

    /// <summary>The machine's name; its host name when not set.</summary>
    public string? ServerName { get; set; }

    /// <summary>The service's name: <c>OTEL_SERVICE_NAME</c> when not set, else the name in a <c>name@version</c> release.</summary>
    public string? ServiceName { get; set; }

    /// <summary>The share of errors and messages sent (default 1).</summary>
    public double SampleRate { get; set; } = 1;

    /// <summary>The share of new traces kept (default 0: no tracing). Traces continued from a caller follow its decision.</summary>
    public double TracesSampleRate { get; set; }

    /// <summary>The URLs outgoing requests carry trace headers to: those holding one of these strings (default none).</summary>
    public IList<string> TracePropagationTargets { get; set; } = new List<string>();

    /// <summary>Changes an event before it is sent, or drops it by returning null.</summary>
    public Func<FixwireEvent, FixwireEvent?>? BeforeSend { get; set; }

    /// <summary>Changes a breadcrumb before it is kept, or drops it by returning null.</summary>
    public Func<Breadcrumb, Breadcrumb?>? BeforeBreadcrumb { get; set; }

    /// <summary>The breadcrumbs kept per scope (default 100).</summary>
    public int MaxBreadcrumbs { get; set; } = 100;

    /// <summary>Whether to send the user's IP address and identifying request headers (off by default).</summary>
    public bool SendDefaultPii { get; set; }

    /// <summary>Whether secrets and personal data are masked on the device, as the Fixwire server does (on).</summary>
    public bool Redact { get; set; } = true;

    /// <summary>The key fragments whose values are filtered whole; null for the server's.</summary>
    public IList<string>? SensitiveKeys { get; set; }

    /// <summary>Bounds the events sent per issue and per minute, so that a crash loop costs a few events and a count.</summary>
    public ErrorBudget ErrorBudget { get; set; } = new();

    /// <summary>
    /// Namespace prefixes of your code. Frames of .NET, ASP.NET Core and well-known libraries are
    /// not your code; others are, unless <see cref="InAppExclude"/> names them.
    /// </summary>
    public IList<string> InAppInclude { get; set; } = new List<string>();

    /// <summary>Namespace prefixes that are not your code.</summary>
    public IList<string> InAppExclude { get; set; } = new List<string>();

    /// <summary>The requests waiting to be sent (default 100); past it, new ones are dropped.</summary>
    public int MaxQueue { get; set; } = 100;

    /// <summary>The timeout of a request to Fixwire (default 10 s).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether the SDK logs what it does to stderr.</summary>
    public bool Debug { get; set; }

    /// <summary>Whether requests are counted for release health (on; needs a release).</summary>
    public bool AutoSessionTracking { get; set; } = true;

    /// <summary>How often request sessions are sent (default a minute).</summary>
    public TimeSpan SessionInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Whether exceptions nothing caught (and unobserved task exceptions) are reported (on).</summary>
    public bool CaptureUnhandledExceptions { get; set; } = true;

    /// <summary>How long the process's exit waits for what is left to be sent (default 2 s).</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Sends the requests to Fixwire; for tests and proxies (default: a handler of its own).</summary>
    public HttpMessageHandler? HttpMessageHandler { get; set; }

    internal void ApplyDefaults()
    {
        Dsn = Or(Dsn, "FIXWIRE_DSN");
        Release = Or(Release, "FIXWIRE_RELEASE");
        Environment = Or(Environment, "FIXWIRE_ENVIRONMENT") ?? "production";
        ServerName = Empty(ServerName) ? System.Environment.MachineName : ServerName;
        ServiceName = Or(ServiceName, "OTEL_SERVICE_NAME");
        if (Empty(ServiceName) && Release != null && Release.IndexOf('@') > 0)
        {
            ServiceName = Release.Substring(0, Release.IndexOf('@')); // "api" of "api@1.4.0"
        }
        if (!(SampleRate > 0 && SampleRate <= 1))
        {
            SampleRate = 1;
        }
        TracesSampleRate = TracesSampleRate is > 0 ? Math.Min(TracesSampleRate, 1) : 0;
        MaxBreadcrumbs = Math.Max(MaxBreadcrumbs, 0);
        if (MaxQueue <= 0)
        {
            MaxQueue = 100;
        }
        if (Timeout <= TimeSpan.Zero)
        {
            Timeout = TimeSpan.FromSeconds(10);
        }
        if (SessionInterval <= TimeSpan.Zero)
        {
            SessionInterval = TimeSpan.FromMinutes(1);
        }
    }

    internal bool SessionsOn => AutoSessionTracking && !Empty(Release);

    internal static bool Empty(string? s) => string.IsNullOrWhiteSpace(s);

    private static string? Or(string? value, string variable) =>
        Empty(value) ? NullIfEmpty(System.Environment.GetEnvironmentVariable(variable)) : value;

    private static string? NullIfEmpty(string? s) => Empty(s) ? null : s;
}

/// <summary>
/// Bounds the errors and messages sent, so that a crash loop costs a few events and a count, not
/// the quota. Each issue may send a burst, then so many a minute, within a budget for all of them;
/// occurrences held back are counted on the issue's next event.
/// </summary>
public sealed class ErrorBudget
{
    /// <summary>Events of one issue sent at once (default 10).</summary>
    public int PerIssueBurst { get; set; } = 10;

    /// <summary>Events of one issue sent a minute after its burst (default 1).</summary>
    public double PerIssuePerMinute { get; set; } = 1;

    /// <summary>Events a minute across issues (default 600).</summary>
    public double PerMinute { get; set; } = 600;

    /// <summary>Whether the budget applies (on); off sends every event.</summary>
    public bool Enabled { get; set; } = true;
}
