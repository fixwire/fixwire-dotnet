using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Fixwire.Internal;
using Fixwire.Redaction;

namespace Fixwire;

/// <summary>
/// Sends to one project. Most programs use the one <see cref="FixwireSdk.Init(Action{FixwireOptions})"/>
/// sets up, through <see cref="FixwireSdk"/> or a <see cref="Hub"/>.
/// </summary>
public sealed class Client : IDisposable
{
    /// <summary>The SDK's name and version, as <c>telemetry.sdk.*</c> say it.</summary>
    internal const string SdkName = "fixwire.dotnet";

    internal const string SdkVersion = "0.1.0";

    private readonly Transport? _transport;
    private readonly Redactor? _redactor;
    private readonly ConditionalWeakTable<Exception, object> _captured = new();

    /// <summary>A client for the options; without a DSN it is disabled and sends nothing.</summary>
    /// <param name="options">The options; defaults are filled in from the environment.</param>
    /// <exception cref="ArgumentException">The DSN is malformed.</exception>
    public Client(FixwireOptions options)
    {
        options.ApplyDefaults();
        Options = options;
        Budget = new Budget(options.ErrorBudget);
        _redactor = options.Redact
            ? Redactor.Create(options.SensitiveKeys == null ? null : new List<string>(options.SensitiveKeys))
            : null;
        if (FixwireOptions.Empty(options.Dsn))
        {
            return;
        }
        _transport = new Transport(Dsn.Parse(options.Dsn), options);
        Enabled = true;
        if (options.SessionsOn)
        {
            Sessions = new Sessions(this, options.SessionInterval);
        }
    }

    /// <summary>Whether the client sends: false without a DSN.</summary>
    public bool Enabled { get; }

    /// <summary>The client's options, defaults filled in.</summary>
    public FixwireOptions Options { get; }

    internal Sessions? Sessions { get; }

    internal Budget Budget { get; }

    internal Transport? Transport => _transport;

    /// <summary>Whether trace headers may go to a URL: it holds one of the trace propagation targets.</summary>
    /// <param name="url">The request's URL.</param>
    public bool ShouldPropagate(string url)
    {
        foreach (var t in Options.TracePropagationTargets)
        {
            if (!string.IsNullOrEmpty(t) && url.IndexOf(t, StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether an exception was captured already, so that an integration that sees it again (a log
    /// line of it) does not send it twice.
    /// </summary>
    /// <param name="exception">The exception.</param>
    public bool IsCaptured(Exception exception) => _captured.TryGetValue(exception, out _);

    /// <summary>Sends an event with what the scope knows: its id, or null when not sent.</summary>
    internal string? Capture(FixwireEvent e, Scope scope)
    {
        if (_transport == null)
        {
            return null;
        }
        scope.ApplyTo(e, Span.Current);
        // The session counts the error whether or not it is sent.
        if (e.Exceptions.Count > 0)
        {
            scope.MarkSession(!e.Exceptions[0].Handled);
        }
        else if (e.Level is Level.Error or Level.Fatal)
        {
            scope.MarkSession(false);
        }
        if (e.Exception != null)
        {
            _captured.Remove(e.Exception);
            _captured.Add(e.Exception, true);
        }
        var held = Budget.Allow(Budget.IssueOf(e), DateTimeOffset.UtcNow);
        if (held < 0)
        {
            _transport.Log("dropped an event: over the error budget");
            return null;
        }
        if (Options.SampleRate < 1 && NextDouble() >= Options.SampleRate)
        {
            return null;
        }
        e.Suppressed = held;
        e.EventId ??= Ids.New(16);
        if (e.Timestamp == default)
        {
            e.Timestamp = DateTimeOffset.UtcNow;
        }
        e.Level ??= e.Exceptions.Count == 0 ? Level.Info : Level.Error;
        if (!Options.SendDefaultPii)
        {
            if (e.User != null)
            {
                e.User.IpAddress = null;
            }
        }
        else if (e.Request?.ClientAddress is { } address)
        {
            e.User ??= new User();
            e.User.IpAddress ??= address;
        }
        if (Options.BeforeSend is { } before)
        {
            try
            {
                if (before(e) is not { } changed)
                {
                    return null;
                }
                e = changed;
            }
#pragma warning disable CA1031 // a failing callback sends the event as it was
            catch (Exception ex)
            {
                _transport.Log("BeforeSend failed, sending the event as it is: " + ex.Message);
            }
#pragma warning restore CA1031
        }
        byte[] body;
        try
        {
            body = Encode(Otlp.Logs(Options, new List<object?> { Otlp.EventRecord(e, _redactor) }));
        }
#pragma warning disable CA1031 // an event that can't be encoded is dropped, not thrown at the app
        catch (Exception ex)
        {
            _transport.Log("encoding an event: " + ex.Message);
            return null;
        }
#pragma warning restore CA1031
        return _transport.Send("/v1/logs", Transport.Error, body) ? e.EventId : null;
    }

    /// <summary>
    /// Reports a run of a scheduled job: <see cref="CheckInStatus.InProgress"/> when it starts, then
    /// <see cref="CheckInStatus.Ok"/> or <see cref="CheckInStatus.Error"/> with the returned id.
    /// </summary>
    /// <param name="checkIn">The check-in.</param>
    /// <returns>Its id, or null when it was not sent.</returns>
    public string? CaptureCheckIn(CheckIn checkIn)
    {
        if (_transport == null || string.IsNullOrWhiteSpace(checkIn.Monitor))
        {
            return null;
        }
        var id = checkIn.Id ?? Ids.New(16);
        var body = new Dictionary<string, object?>
        {
            ["sdk"] = Sdk(),
            ["check_in_id"] = id,
            ["status"] = CheckIn.Wire(checkIn.Status),
            ["environment"] = Options.Environment,
        };
        if (checkIn.Duration is { } d && d > TimeSpan.Zero)
        {
            body["duration"] = d.TotalSeconds;
        }
        if (checkIn.Config != null)
        {
            body["monitor_config"] = checkIn.Config.ToWire();
        }
        return SendJson("/v1/check-ins/" + Uri.EscapeDataString(checkIn.Monitor), Transport.CheckIn, body) ? id : null;
    }

    internal string? CaptureFeedback(Feedback f, Scope scope, Span? current)
    {
        var message = (f.Message ?? "").Trim();
        var score = double.IsNaN(f.Score) || double.IsInfinity(f.Score) ? 0 : Math.Max(-1, Math.Min(1, f.Score));
        if (message.Length == 0 && score == 0)
        {
            return null;
        }
        var user = scope.User;
        var id = Ids.New(16);
        var body = new Dictionary<string, object?>();
        void Put(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                body[key] = value;
            }
        }
        Put("message", message);
        if (score != 0)
        {
            body["score"] = score;
        }
        Put("name", f.Name ?? user?.Username);
        Put("email", f.Email ?? user?.Email);
        Put("url", f.Url);
        body = Otlp.Scrub(body, _redactor);
        body["sdk"] = Sdk();
        body["feedback_id"] = id;
        body["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        body["source"] = f.Source ?? "api";
        body["environment"] = Options.Environment;
        Put("trace_id", f.TraceId ?? (current ?? scope.Span)?.TraceId);
        Put("event_id", f.EventId);
        Put("release", Options.Release);
        return SendJson("/v1/feedback", Transport.Feedback, body) ? id : null;
    }

    internal void SendSpans(List<Span> spans)
    {
        if (_transport == null)
        {
            return;
        }
        try
        {
            _transport.Send("/v1/traces", Transport.Span, Encode(Otlp.Traces(Options, spans, _redactor)));
        }
#pragma warning disable CA1031 // spans that can't be encoded are dropped, not thrown at the app
        catch (Exception e)
        {
            _transport.Log("encoding spans: " + e.Message);
        }
#pragma warning restore CA1031
    }

    internal bool SendJson(string path, string category, Dictionary<string, object?> body) =>
        _transport?.Send(path, category, Encode(body)) ?? false;

    internal static Dictionary<string, object?> Sdk() => new() { ["name"] = SdkName, ["version"] = SdkVersion };

    private static byte[] Encode(object body) => Encoding.UTF8.GetBytes(Json.Write(body));

    private static double NextDouble()
    {
        var b = new byte[8];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(b);
        }
        return (BitConverter.ToUInt64(b, 0) >> 11) / (double)(1UL << 53);
    }

    /// <summary>Waits until what was captured is sent, or the timeout.</summary>
    /// <param name="timeout">The longest wait.</param>
    /// <returns>False when time ran out.</returns>
    public Task<bool> FlushAsync(TimeSpan timeout)
    {
        if (_transport == null)
        {
            return Task.FromResult(true);
        }
        Sessions?.Send();
        return _transport.FlushAsync(timeout);
    }

    /// <summary>Sends what is left (up to <see cref="FixwireOptions.ShutdownTimeout"/>) and stops.</summary>
    public void Dispose()
    {
        if (_transport == null)
        {
            return;
        }
        Sessions?.Dispose();
        FlushAsync(Options.ShutdownTimeout).GetAwaiter().GetResult();
        _transport.Dispose();
    }
}
