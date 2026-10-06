namespace Fixwire;

/// <summary>How a run of a scheduled job is going.</summary>
public enum CheckInStatus
{
    /// <summary>The run started.</summary>
    InProgress,

    /// <summary>The run ended well.</summary>
    Ok,

    /// <summary>The run failed.</summary>
    Error,
}

/// <summary>A run of a scheduled job, reported to its monitor.</summary>
public sealed class CheckIn
{
    /// <summary>A check-in.</summary>
    /// <param name="monitor">The monitor's slug, such as <c>nightly-report</c>.</param>
    /// <param name="status">How the run is going.</param>
    public CheckIn(string monitor, CheckInStatus status)
    {
        Monitor = monitor;
        Status = status;
    }

    /// <summary>The monitor's slug.</summary>
    public string Monitor { get; set; }

    /// <summary>How the run is going.</summary>
    public CheckInStatus Status { get; set; }

    /// <summary>Ties the end of a run to its start: the id the start returned. Made when null.</summary>
    public string? Id { get; set; }

    /// <summary>How long the run took.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Creates or updates the monitor.</summary>
    public MonitorConfig? Config { get; set; }

    internal static string Wire(CheckInStatus s) => s switch
    {
        CheckInStatus.InProgress => "in_progress",
        CheckInStatus.Ok => "ok",
        _ => "error",
    };
}

/// <summary>When a job runs, and how late or long it may be.</summary>
public sealed class MonitorConfig
{
    private readonly string _type;
    private readonly object _value;
    private readonly string? _unit;

    private MonitorConfig(string type, object value, string? unit)
    {
        _type = type;
        _value = value;
        _unit = unit;
    }

    /// <summary>A job that runs on a crontab.</summary>
    /// <param name="crontab">Such as <c>0 3 * * *</c>.</param>
    public static MonitorConfig Crontab(string crontab) => new("crontab", crontab, null);

    /// <summary>A job that runs every so many units.</summary>
    /// <param name="every">How many units.</param>
    /// <param name="unit"><c>minute</c>, <c>hour</c>, <c>day</c>, <c>week</c>, <c>month</c> or <c>year</c>.</param>
    public static MonitorConfig Interval(int every, string unit) => new("interval", every, unit);

    /// <summary>The minutes a check-in may be late.</summary>
    public int CheckInMargin { get; set; }

    /// <summary>The minutes a run may take.</summary>
    public int MaxRuntime { get; set; }

    /// <summary>The schedule's time zone, such as <c>Europe/Berlin</c>.</summary>
    public string? Timezone { get; set; }

    /// <summary>The monitor's config as sent, its strings cut to max bytes (not masked: it is the app's configuration).</summary>
    internal Dictionary<string, object?> ToWire(int max)
    {
        var schedule = new Dictionary<string, object?> { ["type"] = _type, ["value"] = _value is string s ? Otlp.Clip(s, max) : _value };
        if (_unit != null)
        {
            schedule["unit"] = Otlp.Clip(_unit, max);
        }
        var m = new Dictionary<string, object?> { ["schedule"] = schedule };
        if (CheckInMargin > 0)
        {
            m["checkin_margin"] = CheckInMargin;
        }
        if (MaxRuntime > 0)
        {
            m["max_runtime"] = MaxRuntime;
        }
        if (Timezone != null)
        {
            m["timezone"] = Otlp.Clip(Timezone, max);
        }
        return m;
    }
}

/// <summary>
/// What someone said about an error or an AI answer: a message, a score from -1 (bad) to 1 (good),
/// or both. A negative score on a trace opens a <c>user_feedback</c> issue for the agent run.
/// </summary>
public sealed class Feedback
{
    /// <summary>Empty feedback.</summary>
    public Feedback() { }

    /// <summary>Feedback with a message.</summary>
    /// <param name="message">What they said.</param>
    public Feedback(string message) => Message = message;

    /// <summary>What they said.</summary>
    public string? Message { get; set; }

    /// <summary>From -1 (bad) to 1 (good); 0 for none.</summary>
    public double Score { get; set; }

    /// <summary>The trace or agent run it is about; by default the current span's.</summary>
    public string? TraceId { get; set; }

    /// <summary>The error it is about, such as <see cref="FixwireSdk.LastEventId"/>.</summary>
    public string? EventId { get; set; }

    /// <summary>Their name; by default the scope's user's.</summary>
    public string? Name { get; set; }

    /// <summary>Their email; by default the scope's user's.</summary>
    public string? Email { get; set; }

    /// <summary>The page it was given on.</summary>
    public string? Url { get; set; }

    /// <summary>Where it came from: <c>api</c> (the default), <c>widget</c>, …</summary>
    public string? Source { get; set; }
}
