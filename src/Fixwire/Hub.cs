namespace Fixwire;

/// <summary>
/// Pairs a client with a scope. <see cref="FixwireSdk"/>'s methods use the current hub: the main
/// hub, unless a request or a job made a copy of its own current (<see cref="Clone"/>,
/// <see cref="Bind"/>). The current hub flows with <c>await</c>, <c>Task.Run</c> and the thread
/// pool.
/// </summary>
public sealed class Hub
{
    private static readonly AsyncLocal<Hub?> CurrentHub = new();
    private static volatile string? _lastEventId;
    private volatile Client? _client;

    /// <summary>A hub for a client and a scope.</summary>
    /// <param name="client">The client, or null.</param>
    /// <param name="scope">The scope, or null for an empty one.</param>
    public Hub(Client? client, Scope? scope = null)
    {
        _client = client;
        Scope = scope ?? new Scope();
    }

    /// <summary>The hub work uses when none was made current for it.</summary>
    public static Hub Main { get; internal set; } = new(null);

    /// <summary>The current hub: the one made current for this work, else the main hub.</summary>
    public static Hub Current => CurrentHub.Value ?? Main;

    /// <summary>The hub's client, or null before <see cref="FixwireSdk.Init(Action{FixwireOptions})"/>.</summary>
    public Client? Client
    {
        get => _client;
        set => _client = value;
    }

    /// <summary>The hub's scope.</summary>
    public Scope Scope { get; }

    internal static string? LastEventId => _lastEventId;

    /// <summary>A hub with the same client and a copy of the scope, for work that runs apart.</summary>
    public Hub Clone() => new(_client, Scope.Clone());

    /// <summary>
    /// Makes this the current hub for the work that follows (and what it awaits or starts) until
    /// the returned value is disposed:
    /// <code>
    /// using (Hub.Current.Clone().Bind()) { await HandleAsync(job); }
    /// </code>
    /// </summary>
    public IDisposable Bind()
    {
        var previous = CurrentHub.Value;
        CurrentHub.Value = this;
        return new Restore(() => CurrentHub.Value = previous);
    }

    /// <summary>Runs something with a copy of the scope: what it sets there is gone afterwards.</summary>
    /// <param name="work">What to run, given the copy.</param>
    public void WithScope(Action<Scope> work)
    {
        var hub = Clone();
        using (hub.Bind())
        {
            work(hub.Scope);
        }
    }

    /// <summary>Sends an exception and its inner exceptions.</summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The event's id, or null when it was not sent.</returns>
    public string? CaptureException(Exception exception) =>
        CaptureException(exception, "generic", handled: true);

    /// <summary>Sends an exception as an integration caught it.</summary>
    /// <param name="exception">The exception.</param>
    /// <param name="mechanism">How it was caught, such as <c>aspnetcore</c>.</param>
    /// <param name="handled">False for a crash: nothing else handled it.</param>
    /// <param name="level">The level; by default error, fatal when not handled.</param>
    /// <returns>The event's id, or null when it was not sent.</returns>
    public string? CaptureException(Exception exception, string mechanism, bool handled, Level? level = null)
    {
        var c = _client;
        if (exception == null || c == null || !c.Enabled)
        {
            return null;
        }
        FixwireEvent e;
        try
        {
            e = new FixwireEvent
            {
                Exception = exception,
                Exceptions = Frames.Chain(exception, mechanism, handled, c.Options),
                Level = level ?? (handled ? null : Fixwire.Level.Fatal),
            };
        }
#pragma warning disable CA1031 // capturing must never throw at the app (the middleware rethrows the app's exception)
        catch (Exception ex)
        {
            c.Transport?.Log("reading an exception: " + ex.Message);
            return null;
        }
#pragma warning restore CA1031
        return Remember(c.Capture(e, Scope));
    }

    /// <summary>Sends a message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="level">Its level; by default the scope's, else info.</param>
    /// <returns>The event's id, or null when it was not sent.</returns>
    public string? CaptureMessage(string message, Level? level = null)
    {
        var c = _client;
        if (c == null || !c.Enabled)
        {
            return null;
        }
        return Remember(c.Capture(new FixwireEvent { Message = message, Level = level }, Scope));
    }

    /// <summary>Sends an event as it is, with what the scope knows.</summary>
    /// <param name="e">The event.</param>
    /// <returns>Its id, or null when it was not sent.</returns>
    public string? CaptureEvent(FixwireEvent e)
    {
        var c = _client;
        return e == null || c == null || !c.Enabled ? null : Remember(c.Capture(e, Scope));
    }

    private static string? Remember(string? id)
    {
        if (id != null)
        {
            _lastEventId = id;
        }
        return id;
    }

    /// <summary>Records something that happened.</summary>
    /// <param name="breadcrumb">The breadcrumb.</param>
    public void AddBreadcrumb(Breadcrumb breadcrumb)
    {
        var c = _client;
        var max = 100;
        if (c != null)
        {
            max = c.Options.MaxBreadcrumbs;
            if (c.Options.BeforeBreadcrumb is { } before)
            {
                try
                {
                    breadcrumb = before(breadcrumb)!;
                }
#pragma warning disable CA1031 // a failing callback keeps the breadcrumb as it was
                catch (Exception)
                {
                }
#pragma warning restore CA1031
                if (breadcrumb == null)
                {
                    return;
                }
            }
        }
        Scope.AddBreadcrumb(breadcrumb, max);
    }

    /// <summary>Sends what someone said about an error or an AI answer.</summary>
    /// <param name="feedback">The feedback.</param>
    /// <returns>Its id, or null when it holds neither a message nor a score.</returns>
    public string? CaptureFeedback(Feedback feedback)
    {
        var c = _client;
        return c == null || !c.Enabled ? null : c.CaptureFeedback(feedback, Scope, Span.Current);
    }

    /// <summary>
    /// Starts the session of the request the scope serves, for release health; dispose of the
    /// returned value when the request ends. HTTP integrations do this.
    /// </summary>
    public IDisposable StartRequestSession()
    {
        var c = _client;
        if (c?.Sessions == null)
        {
            return new Restore(() => { });
        }
        var rs = new RequestSession();
        Scope.Session = rs;
        var scope = Scope;
        return new Restore(() => c.Sessions.Record(rs.Status, Sessions.DeviceId(scope.User), DateTimeOffset.UtcNow));
    }

    /// <summary>A span builder; the span starts under the current one.</summary>
    /// <param name="name">The span's name.</param>
    public SpanBuilder SpanBuilder(string name) => new(name, this);

    /// <summary>Waits until what was captured is sent, or the timeout.</summary>
    /// <param name="timeout">The longest wait.</param>
    /// <returns>False when time ran out.</returns>
    public Task<bool> FlushAsync(TimeSpan timeout) =>
        _client?.FlushAsync(timeout) ?? Task.FromResult(true);

    /// <summary>Runs an action once, when disposed.</summary>
    internal sealed class Restore(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
