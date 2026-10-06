namespace Fixwire;

/// <summary>
/// What is known about the work under way (the user, tags, contexts, breadcrumbs, the request),
/// added to every event captured with it. A request gets its own copy (see <see cref="Hub.Clone"/>),
/// so what it sets stays with it. Safe for use from several threads.
/// </summary>
public sealed class Scope
{
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private User? _user;
    private readonly Dictionary<string, string> _tags = new();
    private readonly Dictionary<string, IDictionary<string, object?>> _contexts = new();
    private readonly Dictionary<string, object?> _extra = new();
    private readonly Queue<Breadcrumb> _breadcrumbs = new();
    private Level? _level;
    private List<string> _fingerprint = new();
    private string? _transaction;
    private Request? _request;
    private Span? _span;
    internal RequestSession? Session;

    /// <summary>A copy, for work that runs apart.</summary>
    public Scope Clone()
    {
        lock (_lock)
        {
            var s = new Scope
            {
                _user = _user?.Clone(),
                _level = _level,
                _fingerprint = new List<string>(_fingerprint),
                _transaction = _transaction,
                _request = _request,
                _span = _span,
                Session = Session,
            };
            foreach (var t in _tags)
            {
                s._tags[t.Key] = t.Value;
            }
            foreach (var c in _contexts)
            {
                s._contexts[c.Key] = new Dictionary<string, object?>(c.Value);
            }
            foreach (var x in _extra)
            {
                s._extra[x.Key] = x.Value;
            }
            foreach (var b in _breadcrumbs)
            {
                s._breadcrumbs.Enqueue(b);
            }
            return s;
        }
    }

    /// <summary>Who the work is for; null forgets them.</summary>
    public User? User
    {
        get
        {
            lock (_lock)
            {
                return _user?.Clone();
            }
        }
        set
        {
            lock (_lock)
            {
                _user = value?.Clone();
            }
        }
    }

    /// <summary>Sets a searchable tag; a null value removes it.</summary>
    /// <param name="key">The tag.</param>
    /// <param name="value">Its value.</param>
    public void SetTag(string key, string? value)
    {
        if (key == null)
        {
            return; // nothing to set it under
        }
        lock (_lock)
        {
            if (value == null)
            {
                _tags.Remove(key);
            }
            else
            {
                _tags[key] = value;
            }
        }
    }

    /// <summary>Sets a named group of details, such as an order's id and items; null removes it.</summary>
    /// <param name="name">The group.</param>
    /// <param name="values">Its details.</param>
    public void SetContext(string name, IDictionary<string, object?>? values)
    {
        if (name == null)
        {
            return; // nothing to set it under
        }
        lock (_lock)
        {
            if (values == null)
            {
                _contexts.Remove(name);
            }
            else
            {
                _contexts[name] = new Dictionary<string, object?>(values);
            }
        }
    }

    /// <summary>Sets a detail sent with events; null removes it.</summary>
    /// <param name="key">The detail.</param>
    /// <param name="value">Its value.</param>
    public void SetExtra(string key, object? value)
    {
        if (key == null)
        {
            return; // nothing to set it under
        }
        lock (_lock)
        {
            if (value == null)
            {
                _extra.Remove(key);
            }
            else
            {
                _extra[key] = value;
            }
        }
    }

    /// <summary>The level of the events captured with the scope; null for the default.</summary>
    public Level? Level
    {
        get
        {
            lock (_lock)
            {
                return _level;
            }
        }
        set
        {
            lock (_lock)
            {
                _level = value;
            }
        }
    }

    /// <summary>Groups the events captured with the scope; <c>{{ default }}</c> stands for Fixwire's own grouping.</summary>
    /// <param name="fingerprint">The fingerprint.</param>
    public void SetFingerprint(params string[] fingerprint)
    {
        lock (_lock)
        {
            _fingerprint = new List<string>(fingerprint);
        }
    }

    /// <summary>The route or task the work is for, such as <c>GET /items/{id}</c>.</summary>
    public string? Transaction
    {
        get
        {
            lock (_lock)
            {
                return _transaction;
            }
        }
        set
        {
            lock (_lock)
            {
                _transaction = value;
            }
        }
    }

    /// <summary>The HTTP request the work serves.</summary>
    public Request? Request
    {
        get
        {
            lock (_lock)
            {
                return _request;
            }
        }
        set
        {
            lock (_lock)
            {
                _request = value;
            }
        }
    }

    /// <summary>
    /// A span events captured with the scope belong to when no span is current (spans started with
    /// <see cref="SpanBuilder.Start"/> are current where they run).
    /// </summary>
    public Span? Span
    {
        get
        {
            lock (_lock)
            {
                return _span;
            }
        }
        set
        {
            lock (_lock)
            {
                _span = value;
            }
        }
    }

    /// <summary>Records something that happened; past <paramref name="max"/>, the oldest go.</summary>
    /// <param name="breadcrumb">The breadcrumb.</param>
    /// <param name="max">The breadcrumbs kept.</param>
    public void AddBreadcrumb(Breadcrumb breadcrumb, int max = 100) =>
        AddBreadcrumb(breadcrumb, max, FixwireOptions.DefaultMaxValueLength);

    internal void AddBreadcrumb(Breadcrumb? breadcrumb, int max, int maxValueLength)
    {
        if (max <= 0 || breadcrumb == null)
        {
            return;
        }
        if (breadcrumb.Timestamp == default)
        {
            breadcrumb.Timestamp = DateTimeOffset.UtcNow;
        }
        if (breadcrumb.Message is { } message)
        {
            breadcrumb.Message = Otlp.Clip(message, maxValueLength + Otlp.ReadAhead); // kept for a while: no more than an event reads of it
        }
        lock (_lock)
        {
            _breadcrumbs.Enqueue(breadcrumb);
            while (_breadcrumbs.Count > max)
            {
                _breadcrumbs.Dequeue();
            }
        }
    }

    /// <summary>Forgets the breadcrumbs.</summary>
    public void ClearBreadcrumbs()
    {
        lock (_lock)
        {
            _breadcrumbs.Clear();
        }
    }

    /// <summary>Adds what the scope knows to an event; the event's own details win.</summary>
    internal void ApplyTo(FixwireEvent e, Span? current)
    {
        lock (_lock)
        {
            e.User ??= _user?.Clone();
            foreach (var t in _tags)
            {
                if (!e.Tags.ContainsKey(t.Key))
                {
                    e.Tags[t.Key] = t.Value;
                }
            }
            foreach (var c in _contexts)
            {
                if (!e.Contexts.ContainsKey(c.Key))
                {
                    e.Contexts[c.Key] = new Dictionary<string, object?>(c.Value);
                }
            }
            foreach (var x in _extra)
            {
                if (!e.Extra.ContainsKey(x.Key))
                {
                    e.Extra[x.Key] = x.Value;
                }
            }
            if (e.Breadcrumbs.Count == 0)
            {
                e.Breadcrumbs = new List<Breadcrumb>(_breadcrumbs);
            }
            e.Level ??= _level;
            if (e.Fingerprint.Count == 0)
            {
                e.Fingerprint = new List<string>(_fingerprint);
            }
            e.Request ??= _request;
            e.Transaction ??= _transaction;
        }
        var route = e.Request?.CurrentRoute();
        if (e.Transaction == null && route != null)
        {
            e.Transaction = (e.Request!.Method == null ? "" : e.Request.Method + " ") + route;
        }
        var span = current ?? Span;
        e.Transaction ??= span?.SegmentName;
        if (e.TraceId == null && span != null)
        {
            e.TraceId = span.TraceId;
            e.SpanId = span.SpanId;
        }
    }

    internal void MarkSession(bool crashed)
    {
        RequestSession? rs;
        lock (_lock)
        {
            rs = Session;
        }
        rs?.Mark(crashed);
    }
}
