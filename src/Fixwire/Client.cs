using System.Runtime.CompilerServices;
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

    internal const string SdkVersion = "0.1.2";

    /// <summary>The largest error or message the ingest takes, and the largest request of spans (fixwire-protocol §3, §4).</summary>
    internal const int MaxEventBytes = 1 << 20;

    internal const int MaxSpanBytes = 5 << 20;

    /// <summary>The spans a request holds at most.</summary>
    internal const int MaxSpansPerRequest = 100;

    private static readonly char[] QueryOrFragment = ['?', '#'];

    private readonly Transport? _transport;
    private readonly Redactor? _redactor;
    private readonly ConditionalWeakTable<Exception, object> _captured = new();

    /// <summary>
    /// A client for the options; without a DSN it is disabled and sends nothing. It never throws: a
    /// malformed DSN, or an option out of range, is said on stderr and the client is disabled, so
    /// that a typo in configuration can't stop the app from starting.
    /// </summary>
    /// <param name="options">The options; defaults are filled in from the environment.</param>
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
        if (options.Broken(out var dsn) is { } broken)
        {
            Warn(broken + "; Fixwire is off");
            return;
        }
        _transport = new Transport(dsn!, options);
        Enabled = true;
        if (options.SessionsOn)
        {
            Sessions = new Sessions(this, options.SessionInterval);
        }
    }

    /// <summary>Says something on stderr whether or not <see cref="FixwireOptions.Debug"/> is on.</summary>
    private static void Warn(string message)
    {
        try
        {
            Console.Error.WriteLine("fixwire: " + message);
        }
#pragma warning disable CA1031 // a closed or broken stderr must not stop the app
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    /// <summary>Whether the client sends: false without a DSN.</summary>
    public bool Enabled { get; }

    /// <summary>The client's options, defaults filled in.</summary>
    public FixwireOptions Options { get; }

    internal Sessions? Sessions { get; }

    internal Budget Budget { get; }

    internal Transport? Transport => _transport;

    /// <summary>
    /// Whether trace headers may go to a URL: it matches one of the trace propagation targets (see
    /// <see cref="FixwireOptions.TracePropagationTargets"/>), compared without its user info, query
    /// and fragment.
    /// </summary>
    /// <param name="url">The request's URL.</param>
    public bool ShouldPropagate(string url)
    {
        if (string.IsNullOrEmpty(url) || Options.TracePropagationTargets is not { Count: > 0 } targets)
        {
            return false;
        }
        var compared = Compared(url);
        var absolute = SchemeEnd(compared) > 0;
        Uri? uri = null;
        foreach (var t in targets)
        {
            if (string.IsNullOrEmpty(t))
            {
                continue;
            }
            if (t.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                if (compared.StartsWith(t, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (t[0] == '/')
            {
                // A path on the same origin: only a relative URL is known to be on it.
                if (!absolute && compared.StartsWith(t, StringComparison.Ordinal) && !compared.StartsWith("//", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (absolute && (uri != null || Uri.TryCreate(compared, UriKind.Absolute, out uri)) && IsHost(uri, t))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A URL as trace propagation targets see it: without its query, fragment and user info.</summary>
    internal static string Compared(string url)
    {
        var end = url.IndexOfAny(QueryOrFragment);
        if (end >= 0)
        {
            url = url.Substring(0, end);
        }
        var scheme = SchemeEnd(url);
        if (scheme > 0)
        {
            var host = scheme + 3;
            var path = url.IndexOf('/', host);
            var authority = (path < 0 ? url.Length : path) - host;
            var at = authority > 0 ? url.LastIndexOf('@', host + authority - 1, authority) : -1;
            if (at >= 0)
            {
#if NET
                url = string.Concat(url.AsSpan(0, host), url.AsSpan(at + 1));
#else
                url = url.Substring(0, host) + url.Substring(at + 1);
#endif
            }
        }
        return url;
    }

    /// <summary>Where an absolute URL's "://" is; -1 for a relative one (a path that holds "://" is no scheme).</summary>
    private static int SchemeEnd(string url)
    {
        var i = url.IndexOf("://", StringComparison.Ordinal);
        return i > 0 && url.IndexOf('/') == i + 1 ? i : -1;
    }

    /// <summary>Whether a URL's host is the target's (<c>host</c> or <c>host:port</c>) or one of its subdomains, in any case.</summary>
    private static bool IsHost(Uri uri, string target)
    {
        var host = target;
        var colon = target.LastIndexOf(':');
        if (colon > 0 && (target.IndexOf(':') == colon || target[colon - 1] == ']'))
        {
            if (!int.TryParse(target.Substring(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) || uri.Port != port)
            {
                return false;
            }
            host = target.Substring(0, colon);
        }
        var h = uri.Host;
        return h.Equals(host, StringComparison.OrdinalIgnoreCase)
            || (h.Length > host.Length + 1 && h[h.Length - host.Length - 1] == '.' && h.EndsWith(host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether an exception was captured already, so that an integration that sees it again (a log
    /// line of it) does not send it twice.
    /// </summary>
    /// <param name="exception">The exception.</param>
    public bool IsCaptured(Exception exception) => _captured.TryGetValue(exception, out _);

    /// <summary>Sends an event with what the scope knows: its id, or null when not sent. Never throws.</summary>
    internal string? Capture(FixwireEvent e, Scope scope)
    {
        try
        {
            return Send(e, scope);
        }
#pragma warning disable CA1031 // capturing must never throw at the app (nor on the finalizer thread)
        catch (Exception ex)
        {
            _transport?.Log("capturing an event: " + ex.Message);
            return null;
        }
#pragma warning restore CA1031
    }

    private string? Send(FixwireEvent e, Scope scope)
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
            _captured.GetValue(e.Exception, _ => true); // atomic: threads capturing one exception don't collide
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
            // Too large for the ingest: without what came before it, then without its contexts (this
            // SDK sends no frame variables), or not at all.
            body = EncodeEvent(e);
            if (body.Length > MaxEventBytes && e.Breadcrumbs.Count > 0)
            {
                e.Breadcrumbs = new List<Breadcrumb>();
                body = EncodeEvent(e);
            }
            if (body.Length > MaxEventBytes && e.Contexts.Count > 0)
            {
                e.Contexts = new Dictionary<string, IDictionary<string, object?>>();
                body = EncodeEvent(e);
            }
        }
#pragma warning disable CA1031 // an event that can't be encoded is dropped, not thrown at the app
        catch (Exception ex)
        {
            _transport.Log("encoding an event: " + ex.Message);
            return null;
        }
#pragma warning restore CA1031
        if (body.Length > MaxEventBytes)
        {
            _transport.Log("dropped an event: larger than the ingest takes");
            return null;
        }
        return _transport.Send("/v1/logs", Transport.Error, body) ? e.EventId : null;
    }

    private byte[] EncodeEvent(FixwireEvent e) =>
        Encode(Otlp.Logs(Options, new List<object?> { Otlp.EventRecord(e, Options, _redactor) }));

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
        // The app's configuration (slug, schedule, environment): cut, not masked.
        var max = Options.MaxValueLength;
        var id = Otlp.Clip(checkIn.Id ?? Ids.New(16), max);
        var body = new Dictionary<string, object?>
        {
            ["sdk"] = Sdk(),
            ["check_in_id"] = id,
            ["status"] = CheckIn.Wire(checkIn.Status),
            ["environment"] = Otlp.Clip(Options.Environment!, max),
        };
        if (checkIn.Duration is { } d && d > TimeSpan.Zero)
        {
            body["duration"] = d.TotalSeconds;
        }
        if (checkIn.Config != null)
        {
            body["monitor_config"] = checkIn.Config.ToWire(max);
        }
        return SendJson("/v1/check-ins/" + Uri.EscapeDataString(Otlp.Clip(checkIn.Monitor, max)), Transport.CheckIn, body) ? id : null;
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
        var max = Options.MaxValueLength;
        var body = new Dictionary<string, object?>();
        void Put(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                body[key] = Otlp.Clip(value!, max + Otlp.ReadAhead); // what a user typed may be any size
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
        // What they said is masked (reading past the cut); ids and the app's configuration
        // (release, environment) are sent as given. Then every string is cut.
        body = Otlp.Scrub(body, _redactor);
        body["sdk"] = Sdk();
        body["feedback_id"] = id;
        body["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        body["source"] = f.Source ?? "api";
        body["environment"] = Options.Environment;
        Put("trace_id", f.TraceId ?? (current ?? scope.Span)?.TraceId);
        Put("event_id", f.EventId);
        Put("release", Options.Release);
        foreach (var key in body.Keys.ToList())
        {
            if (body[key] is string s)
            {
                body[key] = Otlp.Clip(s, max);
            }
        }
        return SendJson("/v1/feedback", Transport.Feedback, body) ? id : null;
    }

    internal void SendSpans(List<Span> spans)
    {
        if (_transport == null)
        {
            return;
        }
        if (spans.Count > MaxSpansPerRequest)
        {
            for (var i = 0; i < spans.Count; i += MaxSpansPerRequest)
            {
                SendSpans(spans.GetRange(i, Math.Min(MaxSpansPerRequest, spans.Count - i)));
            }
            return;
        }
        try
        {
            var body = Encode(Otlp.Traces(Options, spans, _redactor));
            if (body.Length > MaxSpanBytes)
            {
                // Too large for one request: in halves, and a span too large alone is dropped.
                if (spans.Count > 1)
                {
                    SendSpans(spans.GetRange(0, spans.Count / 2));
                    SendSpans(spans.GetRange(spans.Count / 2, spans.Count - (spans.Count / 2)));
                }
                else
                {
                    _transport.Log("dropped a span: larger than the ingest takes");
                }
                return;
            }
            _transport.Send("/v1/traces", Transport.Span, body);
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
        Ids.Rng.GetBytes(b);
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

    /// <summary>Sends what is left and stops, within <see cref="FixwireOptions.ShutdownTimeout"/>.</summary>
    public void Dispose()
    {
        if (_transport == null)
        {
            return;
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Sessions?.Dispose();
        FlushAsync(Options.ShutdownTimeout).GetAwaiter().GetResult();
        var left = Options.ShutdownTimeout - watch.Elapsed;
        _transport.Close(left > TimeSpan.Zero ? left : TimeSpan.Zero);
    }
}
