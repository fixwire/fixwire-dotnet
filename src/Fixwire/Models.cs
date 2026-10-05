namespace Fixwire;

/// <summary>An event's or a breadcrumb's severity.</summary>
public enum Level
{
    /// <summary>Details for debugging.</summary>
    Debug,

    /// <summary>Something worth knowing.</summary>
    Info,

    /// <summary>Something that may become a problem.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Error,

    /// <summary>Something failed and the program cannot go on.</summary>
    Fatal,
}

internal static class Levels
{
    public static string WireName(this Level l) => l switch
    {
        Level.Debug => "debug",
        Level.Info => "info",
        Level.Warning => "warning",
        Level.Error => "error",
        _ => "fatal",
    };

    /// <summary>OpenTelemetry's severity number.</summary>
    public static int Severity(this Level l) => l switch
    {
        Level.Debug => 5,
        Level.Info => 9,
        Level.Warning => 13,
        Level.Error => 17,
        _ => 21,
    };
}

/// <summary>Who the work is for.</summary>
public sealed class User
{
    /// <summary>A user without details.</summary>
    public User() { }

    /// <summary>A user by id.</summary>
    /// <param name="id">The user's id in your app.</param>
    public User(string id) => Id = id;

    /// <summary>The user's id in your app.</summary>
    public string? Id { get; set; }

    /// <summary>The user's email address.</summary>
    public string? Email { get; set; }

    /// <summary>The user's name.</summary>
    public string? Username { get; set; }

    /// <summary>The user's IP address; sent only with <see cref="FixwireOptions.SendDefaultPii"/>.</summary>
    public string? IpAddress { get; set; }

    internal User Clone() => new() { Id = Id, Email = Email, Username = Username, IpAddress = IpAddress };
}

/// <summary>Something that happened before an error: a log line, a request, a click.</summary>
public sealed class Breadcrumb
{
    /// <summary>An empty breadcrumb; the time is set when it is added.</summary>
    public Breadcrumb() { }

    /// <summary>A breadcrumb with a message.</summary>
    /// <param name="category">What it is about, such as <c>cart</c> or <c>http</c>.</param>
    /// <param name="message">What happened.</param>
    public Breadcrumb(string? category, string? message)
    {
        Category = category;
        Message = message;
    }

    /// <summary>When it happened; set when it is added.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Its type, such as <c>log</c> or <c>http</c>.</summary>
    public string? Type { get; set; }

    /// <summary>What it is about.</summary>
    public string? Category { get; set; }

    /// <summary>What happened.</summary>
    public string? Message { get; set; }

    /// <summary>How serious it was.</summary>
    public Level? Level { get; set; }

    /// <summary>Details.</summary>
    public IDictionary<string, object?> Data { get; set; } = new Dictionary<string, object?>();
}

/// <summary>The HTTP request an event happened in.</summary>
public sealed class Request
{
    /// <summary>The method, such as <c>GET</c>.</summary>
    public string? Method { get; set; }

    /// <summary>The URL without its query.</summary>
    public string? Url { get; set; }

    /// <summary>The query string, without the <c>?</c>.</summary>
    public string? Query { get; set; }

    /// <summary>The headers that may be sent.</summary>
    public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The route the request matched, such as <c>/items/{id}</c>; names the transaction.</summary>
    public string? Route { get; set; }

    /// <summary>Reads the route when an event needs it, for frameworks that match it later.</summary>
    public Func<string?>? RouteProvider { get; set; }

    /// <summary>The client's address; sent only with <see cref="FixwireOptions.SendDefaultPii"/>.</summary>
    public string? ClientAddress { get; set; }

    internal string? CurrentRoute()
    {
        if (Route != null || RouteProvider == null)
        {
            return Route;
        }
        try
        {
            return RouteProvider();
        }
#pragma warning disable CA1031 // the route is a detail; never fail an event for it
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Whether a header may identify someone or hold a secret; such headers are sent only with
    /// <see cref="FixwireOptions.SendDefaultPii"/>.
    /// </summary>
    /// <param name="name">The header's name.</param>
    public static bool IsSensitiveHeader(string name) =>
        name.ToUpperInvariant() is "AUTHORIZATION" or "PROXY-AUTHORIZATION" or "COOKIE" or "SET-COOKIE"
            or "X-FORWARDED-FOR" or "X-REAL-IP" or "X-API-KEY";
}

/// <summary>One call in a stack trace.</summary>
public sealed class Frame
{
    /// <summary>The method, such as <c>CheckoutAsync</c>.</summary>
    public string? Function { get; set; }

    /// <summary>The type, such as <c>Shop.Cart</c>.</summary>
    public string? Module { get; set; }

    /// <summary>The source file, when the PDB says it.</summary>
    public string? File { get; set; }

    /// <summary>The line, or 0 when not known.</summary>
    public int Line { get; set; }

    /// <summary>The column, or 0 when not known.</summary>
    public int Column { get; set; }

    /// <summary>Whether the frame is the app's code rather than a library's.</summary>
    public bool InApp { get; set; }
}

/// <summary>One exception of an event's chain: the one caught, then its inner exceptions.</summary>
public sealed class ExceptionValue
{
    /// <summary>The exception's type, such as <c>System.InvalidOperationException</c>.</summary>
    public string? Type { get; set; }

    /// <summary>Its message.</summary>
    public string? Message { get; set; }

    /// <summary>The namespace of its type.</summary>
    public string? Module { get; set; }

    /// <summary>How it was caught: <c>generic</c>, <c>UnhandledException</c>, <c>aspnetcore</c>, <c>logging</c>, <c>chained</c> (an inner exception), …</summary>
    public string Mechanism { get; set; } = "generic";

    /// <summary>False for a crash: nothing handled the exception.</summary>
    public bool Handled { get; set; } = true;

    /// <summary>The stack, the oldest call first.</summary>
    public IList<Frame> Frames { get; set; } = new List<Frame>();
}

/// <summary>
/// An error or a message, as it is sent. <see cref="FixwireOptions.BeforeSend"/> sees it after the
/// scope's details are added.
/// </summary>
public sealed class FixwireEvent
{
    /// <summary>The event's id: 32 hex characters, made when it is captured.</summary>
    public string? EventId { get; set; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>How serious it is; by default error for exceptions, info for messages.</summary>
    public Level? Level { get; set; }

    /// <summary>The message.</summary>
    public string? Message { get; set; }

    /// <summary>The chain of exceptions, the outermost first; empty for a message.</summary>
    public IList<ExceptionValue> Exceptions { get; set; } = new List<ExceptionValue>();

    /// <summary>Searchable tags.</summary>
    public IDictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();

    /// <summary>Named groups of details.</summary>
    public IDictionary<string, IDictionary<string, object?>> Contexts { get; set; } =
        new Dictionary<string, IDictionary<string, object?>>();

    /// <summary>Other details.</summary>
    public IDictionary<string, object?> Extra { get; set; } = new Dictionary<string, object?>();

    /// <summary>Who the work was for.</summary>
    public User? User { get; set; }

    /// <summary>What happened before, oldest first.</summary>
    public IList<Breadcrumb> Breadcrumbs { get; set; } = new List<Breadcrumb>();

    /// <summary>A custom grouping; <c>{{ default }}</c> stands for Fixwire's own.</summary>
    public IList<string> Fingerprint { get; set; } = new List<string>();

    /// <summary>The route or task it happened in.</summary>
    public string? Transaction { get; set; }

    /// <summary>The HTTP request it happened in.</summary>
    public Request? Request { get; set; }

    /// <summary>The trace it happened in.</summary>
    public string? TraceId { get; set; }

    /// <summary>The span it happened in.</summary>
    public string? SpanId { get; set; }

    /// <summary>The exception it was made from, for <see cref="FixwireOptions.BeforeSend"/>.</summary>
    public Exception? Exception { get; internal set; }

    internal int Suppressed { get; set; }
}
