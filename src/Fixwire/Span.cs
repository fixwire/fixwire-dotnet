using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace Fixwire;

/// <summary>OpenTelemetry's span kinds.</summary>
public enum SpanKind
{
    /// <summary>Work inside the process.</summary>
    Internal = 1,

    /// <summary>Serving a request.</summary>
    Server = 2,

    /// <summary>Calling another service.</summary>
    Client = 3,

    /// <summary>Sending a message.</summary>
    Producer = 4,

    /// <summary>Handling a message.</summary>
    Consumer = 5,
}

/// <summary>
/// A timed piece of work in a trace. A span without a parent in the process (a request, a job) is a
/// segment: it is sent with the spans under it when it ends. Use it with <c>using</c>:
/// <code>
/// using (var span = FixwireSdk.StartSpan("SELECT carts", "db.query")) { … }
/// </code>
/// While open it is the current span where it was started (and in what that awaits or starts):
/// errors captured there link to it, and spans started there are its children.
/// </summary>
public sealed class Span : IDisposable
{
    private static readonly AsyncLocal<Span?> CurrentSpan = new();

    /// <summary>The spans a segment keeps until it is sent.</summary>
    internal const int MaxChildren = 1000;

    /// <summary>The longest <c>tracestate</c> and <c>baggage</c> passed on (the W3C specs' limits).</summary>
    internal const int MaxTracestate = 512;

    internal const int MaxBaggage = 8192;

    private readonly object _lock = new();
    private readonly Dictionary<string, object?> _attributes;
    private readonly long _startTicks = Stopwatch.GetTimestamp();
    private readonly Span _segment;
    private readonly Client? _client;
    private readonly bool _remoteParent;
    private readonly bool _current;
    private readonly Span? _previous;
    private List<Span> _children = new();
    private bool _sent;
    private bool _failed;
    private string? _statusMessage;
    private DateTimeOffset _end;

    internal Span(SpanBuilder b, Span? parent, bool current)
    {
        Name = b.Name;
        Op = b.Op;
        _client = b.Hub.Client;
        _attributes = new Dictionary<string, object?>(b.Attributes);
        SpanId = Ids.New(8);
        Start = DateTimeOffset.UtcNow;
        var continued = b.Traceparent == null ? null : ParseTraceparent(b.Traceparent);
        if (continued != null)
        {
            TraceId = continued.Value.TraceId;
            ParentSpanId = continued.Value.ParentId;
            Sampled = continued.Value.Sampled;
            _remoteParent = true;
            Tracestate = Passable(b.Tracestate, MaxTracestate);
            Baggage = Passable(b.Baggage, MaxBaggage);
            _segment = this;
        }
        else if (parent != null)
        {
            TraceId = parent.TraceId;
            ParentSpanId = parent.SpanId;
            Sampled = parent.Sampled;
            Tracestate = parent.Tracestate;
            Baggage = parent.Baggage;
            _segment = parent._segment;
        }
        else
        {
            TraceId = Ids.New(16);
            Sampled = Sample(TraceId, _client?.Options.TracesSampleRate ?? 0);
            _segment = this;
        }
        Kind = b.Kind ?? KindOf(Op);
        _current = current;
        if (current)
        {
            _previous = CurrentSpan.Value;
            CurrentSpan.Value = this;
        }
    }

    /// <summary>The current span where this runs, or null.</summary>
    public static Span? Current => CurrentSpan.Value;

    /// <summary>The trace's id: 32 hex characters.</summary>
    public string TraceId { get; }

    /// <summary>The span's id: 16 hex characters.</summary>
    public string SpanId { get; }

    /// <summary>The parent's id, or null for the trace's first span.</summary>
    public string? ParentSpanId { get; }

    /// <summary>The span's name; it may be renamed, such as after the route a request matched.</summary>
    public string Name { get; set; }

    /// <summary>The operation, such as <c>http.server</c> or <c>db.query</c>.</summary>
    public string? Op { get; }

    /// <summary>The span's kind.</summary>
    public SpanKind Kind { get; }

    /// <summary>Whether the trace is kept; an unsampled span still carries the trace to the services it calls.</summary>
    public bool Sampled { get; }

    /// <summary>When it started.</summary>
    public DateTimeOffset Start { get; }

    /// <summary>The caller's <c>tracestate</c>, passed on.</summary>
    public string? Tracestate { get; }

    /// <summary>The caller's <c>baggage</c>, passed on.</summary>
    public string? Baggage { get; }

    internal string SegmentName => _segment.Name;

    /// <summary>The W3C <c>traceparent</c> header that continues this span's trace in a service it calls.</summary>
    public string Traceparent => "00-" + TraceId + "-" + SpanId + (Sampled ? "-01" : "-00");

    /// <summary>Sets an attribute (OpenTelemetry's semantic conventions).</summary>
    /// <param name="key">The attribute.</param>
    /// <param name="value">Its value.</param>
    public void SetAttribute(string key, object? value)
    {
        lock (_lock)
        {
            _attributes[key] = value;
        }
    }

    /// <summary>Marks the span failed.</summary>
    /// <param name="exception">What failed, or null.</param>
    public void SetError(Exception? exception)
    {
        lock (_lock)
        {
            _failed = true;
            if (exception != null)
            {
                _statusMessage = Frames.MessageOf(exception);
                _attributes["error.type"] = exception.GetType().FullName;
            }
        }
    }

    /// <summary>Marks the span failed.</summary>
    /// <param name="message">What failed.</param>
    public void SetError(string message)
    {
        lock (_lock)
        {
            _failed = true;
            _statusMessage = message;
        }
    }

    /// <summary>
    /// Ends the span. A segment is sent with the spans finished under it; a span finishing after
    /// its segment was sent goes alone.
    /// </summary>
    public void Finish()
    {
        lock (_lock)
        {
            if (_end != default)
            {
                return;
            }
            var elapsed = (Stopwatch.GetTimestamp() - _startTicks) * TimeSpan.TicksPerSecond / Stopwatch.Frequency;
            _end = Start + TimeSpan.FromTicks(Math.Max(elapsed, 0));
        }
        if (!Sampled || _client == null || !_client.Enabled)
        {
            return;
        }
        List<Span>? send = null;
        lock (_segment._lock)
        {
            if (_segment == this)
            {
                send = _children;
                send.Add(this);
                _children = new List<Span>();
                _sent = true;
            }
            else if (_segment._sent)
            {
                send = new List<Span> { this };
            }
            else if (_segment._children.Count < MaxChildren)
            {
                _segment._children.Add(this);
            }
        }
        if (send != null)
        {
            _client.SendSpans(send);
        }
    }

    /// <summary>Finishes the span and makes the span before it current again.</summary>
    public void Dispose()
    {
        Finish();
        if (_current && CurrentSpan.Value == this)
        {
            CurrentSpan.Value = _previous;
        }
    }

    internal static SpanKind KindOf(string? op)
    {
        if (op == null)
        {
            return SpanKind.Internal;
        }
        if (op == "http.server" || op.EndsWith(".server", StringComparison.Ordinal))
        {
            return SpanKind.Server;
        }
        if (op == "http.client" || op.StartsWith("db", StringComparison.Ordinal) || op.EndsWith(".client", StringComparison.Ordinal))
        {
            return SpanKind.Client;
        }
        if (op.EndsWith(".publish", StringComparison.Ordinal))
        {
            return SpanKind.Producer;
        }
        return op.EndsWith(".process", StringComparison.Ordinal) ? SpanKind.Consumer : SpanKind.Internal;
    }

    /// <summary>
    /// Decides a new trace the way every Fixwire SDK does: kept when its id's last 56 bits, as a
    /// fraction of 2^56, are at least <c>1 - rate</c>.
    /// </summary>
    internal static bool Sample(string traceId, double rate)
    {
        if (rate <= 0)
        {
            return false;
        }
        if (rate >= 1)
        {
            return true;
        }
        if (traceId.Length < 14
            || !long.TryParse(traceId.Substring(traceId.Length - 14), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var n))
        {
            return false;
        }
        return n / (double)(1L << 56) >= 1 - rate;
    }

    /// <summary>Reads <c>00-&lt;trace id&gt;-&lt;parent id&gt;-&lt;flags&gt;</c>; null when malformed.</summary>
    internal static (string TraceId, string ParentId, bool Sampled)? ParseTraceparent(string header)
    {
        var p = header.Trim().Split('-');
        if (p.Length < 4 || p[0].Length != 2 || p[0].Equals("ff", StringComparison.OrdinalIgnoreCase)
            || p[1].Length != 32 || p[2].Length != 16 || p[3].Length != 2)
        {
            return null;
        }
        if (!IsHex(p[0]) || !IsHex(p[1]) || !IsHex(p[2]) || !IsHex(p[3]) || p[1].Trim('0').Length == 0 || p[2].Trim('0').Length == 0)
        {
            return null;
        }
        var flags = int.Parse(p[3], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return (p[1].ToLowerInvariant(), p[2].ToLowerInvariant(), (flags & 1) == 1);
    }

    /// <summary>A caller's header to pass on to the services this one calls: null when too long or not one line of text.</summary>
    private static string? Passable(string? value, int max)
    {
        if (value == null || value.Length > max)
        {
            return null;
        }
        foreach (var c in value)
        {
            if ((c < ' ' && c != '\t') || c == '\u007f')
            {
                return null;
            }
        }
        return value;
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The span as the protocol sends it, attributes still plain.</summary>
    internal Dictionary<string, object?> Record()
    {
        lock (_lock)
        {
            var m = new Dictionary<string, object?>
            {
                ["traceId"] = TraceId,
                ["spanId"] = SpanId,
            };
            if (ParentSpanId != null)
            {
                m["parentSpanId"] = ParentSpanId;
            }
            m["name"] = Name;
            m["kind"] = (int)Kind;
            m["startTimeUnixNano"] = Nanos(Start);
            m["endTimeUnixNano"] = Nanos(_end);
            var attrs = new Dictionary<string, object?>(_attributes) { ["fixwire.op"] = Op };
            m["attributes"] = attrs;
            var status = new Dictionary<string, object?> { ["code"] = _failed ? 2 : 1 };
            if (_failed && _statusMessage != null)
            {
                status["message"] = _statusMessage;
            }
            m["status"] = status;
            m["flags"] = 0x100 | (_remoteParent ? 0x200 : 0) | (Sampled ? 1 : 0);
            return m;
        }
    }

    private static readonly long UnixEpochTicks = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    internal static string Nanos(DateTimeOffset t) =>
        ((t.UtcTicks - UnixEpochTicks) * 100).ToString(CultureInfo.InvariantCulture);
}

/// <summary>Starts spans: kinds, attributes, other parents, continued traces.</summary>
public sealed class SpanBuilder
{
    private Span? _parent;
    private bool _noParent;

    internal SpanBuilder(string name, Hub hub)
    {
        Name = name;
        Hub = hub;
    }

    internal string Name { get; }

    internal Hub Hub { get; }

    internal string? Op { get; private set; }

    internal SpanKind? Kind { get; private set; }

    internal Dictionary<string, object?> Attributes { get; } = new();

    internal string? Traceparent { get; private set; }

    internal string? Tracestate { get; private set; }

    internal string? Baggage { get; private set; }

    /// <summary>The span's operation: <c>http.server</c>, <c>db.query</c>, <c>task</c>, …</summary>
    /// <param name="op">The operation.</param>
    public SpanBuilder WithOp(string op)
    {
        Op = op;
        return this;
    }

    /// <summary>The span's kind; by default, what the operation implies.</summary>
    /// <param name="kind">The kind.</param>
    public SpanBuilder WithKind(SpanKind kind)
    {
        Kind = kind;
        return this;
    }

    /// <summary>Sets an attribute (OpenTelemetry's semantic conventions).</summary>
    /// <param name="key">The attribute.</param>
    /// <param name="value">Its value.</param>
    public SpanBuilder WithAttribute(string key, object? value)
    {
        Attributes[key] = value;
        return this;
    }

    /// <summary>Starts the span under this one rather than the current span; null starts a new trace.</summary>
    /// <param name="parent">The parent.</param>
    public SpanBuilder WithParent(Span? parent)
    {
        _parent = parent;
        _noParent = parent == null;
        return this;
    }

    /// <summary>
    /// Continues a caller's trace from its W3C headers; a malformed <c>traceparent</c> starts a new
    /// trace. The caller's sampling decision holds.
    /// </summary>
    /// <param name="traceparent">The <c>traceparent</c> header.</param>
    /// <param name="tracestate">The <c>tracestate</c> header, or null.</param>
    /// <param name="baggage">The <c>baggage</c> header, or null.</param>
    public SpanBuilder ContinueTrace(string? traceparent, string? tracestate = null, string? baggage = null)
    {
        Traceparent = traceparent;
        Tracestate = tracestate;
        Baggage = baggage;
        return this;
    }

    /// <summary>Starts the span and makes it the current one where it runs until it is disposed.</summary>
    public Span Start() => new(this, ParentSpan(), current: true);

    /// <summary>Starts the span without making it current: for work with no spans under it, such as an outgoing request.</summary>
    public Span StartDetached() => new(this, ParentSpan(), current: false);

    private Span? ParentSpan() => _noParent ? null : _parent ?? Span.Current ?? Hub.Scope.Span;
}

/// <summary>Random ids in hex.</summary>
internal static class Ids
{
    private const string Hex = "0123456789abcdef";

    /// <summary>One generator for every id (safe across threads): making one per span is costly on .NET Framework.</summary>
    public static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

    public static string New(int bytes)
    {
        var b = new byte[bytes];
        Rng.GetBytes(b);
        var allZero = true;
        var c = new char[bytes * 2];
        for (var i = 0; i < bytes; i++)
        {
            c[2 * i] = Hex[b[i] >> 4];
            c[(2 * i) + 1] = Hex[b[i] & 0xf];
            allZero &= b[i] == 0;
        }
        if (allZero)
        {
            c[c.Length - 1] = '1'; // all zeros is not a valid id
        }
        return new string(c);
    }
}
