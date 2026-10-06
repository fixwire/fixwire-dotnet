using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Fixwire;

/// <summary>
/// The error budget at work. The fingerprint is cheap and only drives the budget; the server's
/// grouping is the real one.
/// </summary>
internal sealed class Budget
{
    private const int MaxIssues = 1024;
    private const int TopFrames = 5;

    /// <summary>The start of a message the fingerprint reads.</summary>
    private const int MaxMessage = 1024;

    /// <summary>
    /// Parts of a message that change between occurrences. Linear: an address is looked for only
    /// where a word starts (<c>\S+@\S+</c> tried at every position is cubic on "@@@…").
    /// </summary>
    private static readonly Regex Variable = new(
        @"\b0x[0-9a-fA-F]+\b|\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b|"
            + @"\b[0-9a-fA-F]{16,}\b|[0-9]+(?:\.[0-9]+)?|(?<!\S)[^\s@]+@\S*\.\w+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private sealed class Bucket(double tokens, DateTimeOffset now)
    {
        public double Tokens = tokens;
        public DateTimeOffset Updated = now;
        public DateTimeOffset Seen = now;
        public int Suppressed;

        public bool Take(double burst, double perMinute, DateTimeOffset at)
        {
            Tokens = Math.Min(burst, Tokens + ((at - Updated).TotalMinutes * perMinute));
            Updated = at;
            if (Tokens >= 1)
            {
                Tokens--;
                return true;
            }
            return false;
        }
    }

    private readonly ErrorBudget _options;
    private readonly Bucket _all;
    private readonly Dictionary<string, Bucket> _issues = new();
    private readonly object _lock = new();

    public Budget(ErrorBudget options)
    {
        _options = options;
        _all = new Bucket(Math.Max(options.PerMinute, 1), DateTimeOffset.UtcNow);
    }

    /// <summary>Whether an event of the issue may be sent: -1 when not, else the occurrences held back since the last one sent.</summary>
    public int Allow(string issue, DateTimeOffset now)
    {
        if (!_options.Enabled)
        {
            return 0;
        }
        lock (_lock)
        {
            var burst = Math.Max(_options.PerIssueBurst, 1);
            if (!_issues.TryGetValue(issue, out var b))
            {
                if (_issues.Count >= MaxIssues)
                {
                    ForgetOldest();
                }
                b = new Bucket(burst, now);
                _issues[issue] = b;
            }
            b.Seen = now;
            var perMinute = Math.Max(_options.PerMinute, 1);
            if (b.Take(burst, Math.Max(_options.PerIssuePerMinute, 0), now) && _all.Take(perMinute, perMinute, now))
            {
                var held = b.Suppressed;
                b.Suppressed = 0;
                return held;
            }
            b.Suppressed++;
            return -1;
        }
    }

    /// <summary>For tests: makes the issue's bucket older.</summary>
    internal void Age(string issue, TimeSpan by)
    {
        lock (_lock)
        {
            if (_issues.TryGetValue(issue, out var b))
            {
                b.Updated -= by;
            }
        }
    }

    private void ForgetOldest()
    {
        string? oldest = null;
        var at = DateTimeOffset.MaxValue;
        foreach (var kv in _issues)
        {
            if (kv.Value.Seen < at)
            {
                oldest = kv.Key;
                at = kv.Value.Seen;
            }
        }
        if (oldest != null)
        {
            _issues.Remove(oldest);
        }
    }

    /// <summary>
    /// The event's fingerprint for the budget: its exception types and top in-app frames (or its
    /// message without the parts that vary), and its custom fingerprint.
    /// </summary>
    public static string IssueOf(FixwireEvent e)
    {
        var parts = new List<string>();
        if (e.Exceptions.Count > 0)
        {
            foreach (var x in e.Exceptions)
            {
                parts.Add(x.Type ?? "");
            }
            // The innermost exception threw: its frames say where.
            var thrower = e.Exceptions[e.Exceptions.Count - 1];
            var frames = thrower.Frames.Count > 0 ? thrower.Frames : e.Exceptions[0].Frames;
            var app = frames.Where(f => f.InApp).ToList();
            if (app.Count == 0)
            {
                app = frames.ToList();
            }
            foreach (var f in app.Skip(Math.Max(0, app.Count - TopFrames)))
            {
                parts.Add(f.Module + "|" + f.Function);
            }
            if (frames.Count == 0)
            {
                parts.Add(Template(e.Exceptions[0].Message));
            }
        }
        else
        {
            parts.Add(Template(e.Message));
        }
        if (e.Fingerprint.Count > 0)
        {
            parts.Add(string.Join("\u001f", e.Fingerprint));
        }
        return Fnv1a(string.Join("\u001e", parts));
    }

    /// <summary>The start of a message without the parts that vary; as it is if the regex times out.</summary>
    private static string Template(string? message)
    {
        var m = message ?? "";
        if (m.Length > MaxMessage)
        {
            m = m.Substring(0, MaxMessage);
        }
        try
        {
            return Variable.Replace(m, "<*>");
        }
        catch (RegexMatchTimeoutException)
        {
            return m; // a starved thread: an approximate fingerprint, never an exception at the app
        }
    }

    private static string Fnv1a(string s)
    {
        var h = 0xcbf29ce484222325UL;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h *= 0x100000001b3UL;
        }
        return h.ToString("x", CultureInfo.InvariantCulture);
    }
}
