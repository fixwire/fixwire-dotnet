using System.Collections;
using System.Globalization;
using Fixwire.Redaction;

namespace Fixwire;

/// <summary>Events and spans as OTLP JSON (fixwire-protocol §3, §4), within the bounds of §13.</summary>
internal static class Otlp
{
    private const int MaxDepth = 10;

    /// <summary>
    /// The bytes of UTF-8 redaction reads past what a string keeps, so that a secret the cut goes
    /// through is still found (and then cut off).
    /// </summary>
    internal const int ReadAhead = 16 * 1024;

    /// <summary>The items kept of a list or dictionary inside a value.</summary>
    private const int MaxItems = 100;

    /// <summary>The lists and dictionaries read of one value: shared ones can't make it exponential.</summary>
    private const int MaxObjects = 10_000;

    /// <summary>The resource every request carries: who sends, release, environment.</summary>
    public static Dictionary<string, object?> Resource(FixwireOptions o) => new()
    {
        ["attributes"] = Attributes(
            new Dictionary<string, object?>
            {
                ["service.name"] = o.ServiceName,
                ["service.version"] = o.Release,
                ["deployment.environment.name"] = o.Environment,
                ["host.name"] = o.ServerName,
                ["telemetry.sdk.name"] = Client.SdkName,
                ["telemetry.sdk.version"] = Client.SdkVersion,
                ["telemetry.sdk.language"] = "dotnet",
            },
            o.MaxValueLength),
    };

    private static Dictionary<string, object?> ScopeInfo() => new()
    {
        ["name"] = Client.SdkName,
        ["version"] = Client.SdkVersion,
    };

    /// <summary>An OTLP logs export of records.</summary>
    public static Dictionary<string, object?> Logs(FixwireOptions o, List<object?> records) => new()
    {
        ["resourceLogs"] = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["resource"] = Resource(o),
                ["scopeLogs"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["scope"] = ScopeInfo(), ["logRecords"] = records },
                },
            },
        },
    };

    /// <summary>An OTLP traces export of spans, redacted.</summary>
    public static Dictionary<string, object?> Traces(FixwireOptions o, List<Span> spans, Redactor? redactor)
    {
        var max = o.MaxValueLength;
        var items = new List<object?>(spans.Count);
        foreach (var s in spans)
        {
            var m = s.Record();
            var plain = Scrub(PlainMap((Dictionary<string, object?>)m["attributes"]!, max), redactor);
            m["attributes"] = Attributes(plain, max);
            m["name"] = Mask((string?)m["name"], redactor, max);
            if (m["status"] is Dictionary<string, object?> status && status.TryGetValue("message", out var message))
            {
                status["message"] = Mask((string?)message, redactor, max); // an exception's message
            }
            items.Add(m);
        }
        return new Dictionary<string, object?>
        {
            ["resourceSpans"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["resource"] = Resource(o),
                    ["scopeSpans"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["scope"] = ScopeInfo(), ["spans"] = items },
                    },
                },
            },
        };
    }

    /// <summary>An error or a message as a log record (fixwire-protocol §4), redacted.</summary>
    public static Dictionary<string, object?> EventRecord(FixwireEvent e, FixwireOptions o, Redactor? redactor)
    {
        var max = o.MaxValueLength;
        var a = new Dictionary<string, object?>
        {
            ["fixwire.tags"] = e.Tags,
            ["fixwire.transaction"] = e.Transaction,
            ["fixwire.fingerprint"] = e.Fingerprint,
        };
        if (e.Suppressed > 0)
        {
            a["fixwire.suppressed"] = e.Suppressed;
        }
        if (e.User is { } u)
        {
            a["user.id"] = u.Id;
            a["user.email"] = u.Email;
            a["user.name"] = u.Username;
            a["client.address"] = u.IpAddress;
        }
        a["fixwire.contexts"] = e.Contexts;
        foreach (var x in e.Extra)
        {
            if (!a.ContainsKey(x.Key))
            {
                a[x.Key] = x.Value;
            }
        }
        if (e.Breadcrumbs.Count > 0)
        {
            var crumbs = new List<object?>();
            foreach (var b in e.Breadcrumbs)
            {
                crumbs.Add(new Dictionary<string, object?>
                {
                    ["timestamp"] = b.Timestamp.ToUnixTimeMilliseconds() / 1000.0,
                    ["type"] = b.Type,
                    ["category"] = b.Category,
                    ["message"] = b.Message,
                    ["level"] = b.Level?.WireName(),
                    ["data"] = b.Data,
                });
            }
            a["fixwire.breadcrumbs"] = crumbs;
        }
        if (e.Request is { } r)
        {
            a["http.request.method"] = r.Method;
            a["url.full"] = r.Url;
            a["url.query"] = r.Query;
            a["http.route"] = r.CurrentRoute();
            foreach (var h in r.Headers)
            {
                var name = h.Key.ToLowerInvariant();
                a[name == "user-agent" ? "user_agent.original" : "http.request.header." + name] = h.Value;
            }
        }
        var level = e.Level ?? Level.Error;
        var record = new Dictionary<string, object?>
        {
            ["timeUnixNano"] = Span.Nanos(e.Timestamp),
            ["severityNumber"] = level.Severity(),
            ["severityText"] = level.WireName().ToUpperInvariant(),
        };
        if (e.TraceId != null)
        {
            record["traceId"] = e.TraceId;
            record["spanId"] = e.SpanId;
        }
        if (e.Exceptions.Count == 0)
        {
            record["eventName"] = "fixwire.message";
            record["body"] = Value(Mask(e.Message ?? "", redactor, max), max);
        }
        else
        {
            record["eventName"] = "exception";
            var outer = e.Exceptions[0];
            a["exception.type"] = outer.Type;
            a["exception.message"] = outer.Message;
            var chain = new List<object?>();
            var handled = true;
            // An event made by hand (or changed by BeforeSend) is held to the bounds too.
            foreach (var x in e.Exceptions.Take(Frames.MaxChain))
            {
                var frames = new List<object?>();
                for (var i = Math.Max(0, x.Frames.Count - o.MaxStackFrames); i < x.Frames.Count; i++) // the newest
                {
                    var f = x.Frames[i];
                    var fm = new Dictionary<string, object?>
                    {
                        ["function"] = f.Function,
                        ["module"] = f.Module,
                        ["file"] = f.File,
                    };
                    if (f.Line > 0)
                    {
                        fm["line"] = f.Line;
                    }
                    if (f.Column > 0)
                    {
                        fm["column"] = f.Column;
                    }
                    fm["in_app"] = f.InApp;
                    frames.Add(fm);
                }
                chain.Add(new Dictionary<string, object?>
                {
                    ["type"] = x.Type,
                    ["message"] = x.Message,
                    ["module"] = x.Module,
                    ["mechanism"] = new Dictionary<string, object?> { ["type"] = x.Mechanism, ["handled"] = x.Handled },
                    ["frames"] = frames,
                });
                handled &= x.Handled;
            }
            a["fixwire.exceptions"] = chain;
            if (!handled)
            {
                a["fixwire.handled"] = false;
            }
            if (!string.IsNullOrEmpty(e.Message))
            {
                record["body"] = Value(Mask(e.Message, redactor, max), max);
            }
        }
        var plain = Scrub(PlainMap(a, max), redactor);
        plain["fixwire.event_id"] = e.EventId;
        record["attributes"] = Attributes(plain, max);
        return record;
    }

    public static Dictionary<string, object?> Scrub(Dictionary<string, object?> m, Redactor? redactor)
    {
        if (redactor == null)
        {
            return m;
        }
        var count = 0;
        return redactor.Walk(m, ref count) as Dictionary<string, object?> ?? new Dictionary<string, object?>();
    }

    /// <summary>
    /// A string masked, then cut to max bytes: redaction reads <see cref="ReadAhead"/> past the
    /// cut, so a secret the cut goes through (a private key, a JWT) is still found.
    /// </summary>
    public static string? Mask(string? s, Redactor? redactor, int max) =>
        string.IsNullOrEmpty(s) ? s : Clip(redactor == null ? s! : redactor.Mask(Clip(s!, max + ReadAhead)).Text!, max);

    /// <summary>
    /// s cut to at most max bytes of UTF-8, "..." ending it within them, on a character's boundary
    /// (a surrogate pair stays whole; a lone surrogate counts as the U+FFFD written for it). Reads
    /// no further into s than max.
    /// </summary>
    internal static string Clip(string s, int max)
    {
        if (s.Length * 3L <= max)
        {
            return s; // fits, whatever its characters
        }
        var keep = Math.Max(max - 3, 0);
        var bytes = 0;
        var cut = -1;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var pair = char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
            var n = c < 0x80 ? 1 : c < 0x800 ? 2 : pair ? 4 : 3;
            if (cut < 0 && bytes + n > keep)
            {
                cut = i; // what comes before it, and "...", fit
            }
            bytes += n;
            if (bytes > max)
            {
#if NET
                return string.Concat(s.AsSpan(0, cut), "...");
#else
                return s.Substring(0, cut) + "...";
#endif
            }
            if (pair)
            {
                i++;
            }
        }
        return s;
    }

    /// <summary>A dictionary in JSON's own types (see <see cref="Plain"/>), strings read to what redaction reads.</summary>
    public static Dictionary<string, object?> PlainMap(IDictionary m, int max) =>
        (Dictionary<string, object?>)Plain(m, 0, new Walk(max + ReadAhead))!;

    /// <summary>What one walk has read: the containers it is inside, and how many more it may read.</summary>
    private sealed class Walk(int maxRead)
    {
        public readonly int MaxRead = maxRead;
        public readonly List<object> Path = new();
        public int Objects = MaxObjects;
    }

    /// <summary>
    /// A value in JSON's own types: dictionaries with string keys, lists, strings, numbers, booleans,
    /// null. Bounded: strings to what redaction reads, containers to 10 levels and (inside the value)
    /// 100 items, 10,000 of them read per value. A container inside itself is "[Circular ~]", one
    /// deeper than the limit (or past what a value may read) "[Object]" or "[Array]", one whose
    /// enumeration throws "[Unreadable]".
    /// </summary>
    private static object? Plain(object? v, int depth, Walk w)
    {
        switch (v)
        {
            case null or bool:
                return v;
            case string s:
                return Clip(s, w.MaxRead);
            case int or long or short or byte or sbyte or uint or ushort or ulong or double or float or decimal:
                return v;
            case Enum en:
                return en.ToString();
            case DateTimeOffset dto:
                return dto.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            case DateTime dt:
                return dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            case IEnumerable items:
                foreach (var outer in w.Path)
                {
                    if (ReferenceEquals(outer, v))
                    {
                        return "[Circular ~]";
                    }
                }
                if (depth > MaxDepth || w.Objects-- <= 0)
                {
                    return v is IDictionary ? "[Object]" : "[Array]";
                }
                w.Path.Add(v);
                try
                {
                    return Container(items, depth, w);
                }
#pragma warning disable CA1031 // a collection changed while read, a lazy sequence that fails, …
                catch (Exception)
                {
                    return "[Unreadable]";
                }
#pragma warning restore CA1031
                finally
                {
                    w.Path.RemoveAt(w.Path.Count - 1);
                }
            default:
                return Text(v, w.MaxRead); // Guid, Uri, …
        }
    }

    private static object Container(IEnumerable items, int depth, Walk w)
    {
        var max = depth == 0 ? int.MaxValue : MaxItems; // the top is the attributes: each is kept, and a value of its own
        var n = 0;
        if (items is IDictionary d)
        {
            var map = new Dictionary<string, object?>();
            foreach (DictionaryEntry e in d)
            {
                if (n++ == max)
                {
                    break;
                }
                if (depth == 0)
                {
                    w.Objects = MaxObjects;
                }
                map[Text(e.Key, w.MaxRead)] = Plain(e.Value, depth + 1, w);
            }
            return map;
        }
        var list = new List<object?>();
        foreach (var item in items)
        {
            if (n++ == max)
            {
                break; // an endless sequence ends here
            }
            if (depth == 0)
            {
                w.Objects = MaxObjects;
            }
            list.Add(Plain(item, depth + 1, w));
        }
        return list;
    }

    /// <summary>A value's string, cut to what redaction reads; "[Unreadable]" when its ToString throws.</summary>
    private static string Text(object v, int maxRead)
    {
        try
        {
            return Clip(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "", maxRead);
        }
#pragma warning disable CA1031 // the app's ToString must not cost the event
        catch (Exception)
        {
            return "[Unreadable]";
        }
#pragma warning restore CA1031
    }

    /// <summary>OTLP key-values of a dictionary in JSON's own types, empty values left out, strings cut to max bytes.</summary>
    public static List<object?> Attributes(IDictionary<string, object?> m, int max)
    {
        var out_ = new List<object?>(m.Count);
        foreach (var kv in m)
        {
            if (IsEmpty(kv.Value))
            {
                continue;
            }
            out_.Add(new Dictionary<string, object?> { ["key"] = Clip(kv.Key, max), ["value"] = AnyValue(kv.Value, max) });
        }
        return out_;
    }

    private static bool IsEmpty(object? v) => v switch
    {
        null => true,
        string s => s.Length == 0,
        ICollection c => c.Count == 0,
        _ => false,
    };

    /// <summary>A value as an OTLP AnyValue, strings cut to max bytes.</summary>
    public static Dictionary<string, object?> Value(object? v, int max) => AnyValue(Plain(v, 1, new Walk(max + ReadAhead)), max);

    /// <summary>A value in JSON's own types as an OTLP AnyValue, strings cut to max bytes.</summary>
    private static Dictionary<string, object?> AnyValue(object? p, int max)
    {
        switch (p)
        {
            case null:
                return new() { ["stringValue"] = "" };
            case string s:
                return new() { ["stringValue"] = Clip(s, max) }; // read (and redacted) further, kept to here
            case bool b:
                return new() { ["boolValue"] = b };
            case int or long or short or byte or sbyte or uint or ushort:
                return new() { ["intValue"] = Convert.ToString(p, CultureInfo.InvariantCulture) };
            case ulong ul:
                return ul <= long.MaxValue
                    ? new() { ["intValue"] = ul.ToString(CultureInfo.InvariantCulture) }
                    : new() { ["stringValue"] = ul.ToString(CultureInfo.InvariantCulture) };
            case double or float or decimal:
                var d = Convert.ToDouble(p, CultureInfo.InvariantCulture);
                return double.IsNaN(d) || double.IsInfinity(d)
                    ? new() { ["stringValue"] = Convert.ToString(p, CultureInfo.InvariantCulture) } // "NaN", "Infinity", "-Infinity"
                    : new() { ["doubleValue"] = d };
            case List<object?> items:
                var values = new List<object?>(items.Count);
                foreach (var item in items)
                {
                    values.Add(AnyValue(item, max));
                }
                return new() { ["arrayValue"] = new Dictionary<string, object?> { ["values"] = values } };
            default:
                return new()
                {
                    ["kvlistValue"] = new Dictionary<string, object?> { ["values"] = Attributes((Dictionary<string, object?>)p, max) },
                };
        }
    }
}
