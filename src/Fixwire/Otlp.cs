using System.Collections;
using System.Globalization;
using Fixwire.Redaction;

namespace Fixwire;

/// <summary>Events and spans as OTLP JSON (sdks/PROTOCOL.md §3, §4).</summary>
internal static class Otlp
{
    private const int MaxDepth = 10;

    /// <summary>The characters a string keeps when sent.</summary>
    internal const int MaxString = 16 * 1024;

    /// <summary>
    /// The characters of a string redaction reads: twice what is kept, so that a secret the cut goes
    /// through is still found (and then cut off).
    /// </summary>
    internal const int MaxRead = 2 * MaxString;

    /// <summary>The items kept of a list or dictionary inside a value.</summary>
    private const int MaxItems = 1000;

    /// <summary>The resource every request carries: who sends, release, environment.</summary>
    public static Dictionary<string, object?> Resource(FixwireOptions o) => new()
    {
        ["attributes"] = Attributes(new Dictionary<string, object?>
        {
            ["service.name"] = o.ServiceName,
            ["service.version"] = o.Release,
            ["deployment.environment.name"] = o.Environment,
            ["host.name"] = o.ServerName,
            ["telemetry.sdk.name"] = Client.SdkName,
            ["telemetry.sdk.version"] = Client.SdkVersion,
            ["telemetry.sdk.language"] = "dotnet",
        }),
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
        var items = new List<object?>(spans.Count);
        foreach (var s in spans)
        {
            var m = s.Record();
            var attrs = (Dictionary<string, object?>)m["attributes"]!;
            attrs.TryGetValue("fixwire.op", out var op);
            attrs.Remove("fixwire.op");
            var plain = Scrub(PlainMap(attrs), redactor);
            plain["fixwire.op"] = op;
            m["attributes"] = Attributes(plain);
            m["name"] = Mask((string?)m["name"], redactor);
            if (m["status"] is Dictionary<string, object?> status && status.TryGetValue("message", out var message))
            {
                status["message"] = Mask((string?)message, redactor); // an exception's message
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

    /// <summary>An error or a message as a log record (sdks/PROTOCOL.md §4), redacted.</summary>
    public static Dictionary<string, object?> EventRecord(FixwireEvent e, Redactor? redactor)
    {
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
            record["body"] = Value(Mask(e.Message ?? "", redactor));
        }
        else
        {
            record["eventName"] = "exception";
            var outer = e.Exceptions[0];
            a["exception.type"] = outer.Type;
            a["exception.message"] = outer.Message;
            var chain = new List<object?>();
            var handled = true;
            foreach (var x in e.Exceptions)
            {
                var frames = new List<object?>();
                foreach (var f in x.Frames)
                {
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
                record["body"] = Value(Mask(e.Message, redactor));
            }
        }
        var plain = Scrub(PlainMap(a), redactor);
        plain["fixwire.event_id"] = e.EventId;
        record["attributes"] = Attributes(plain);
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

    /// <summary>A string masked, and cut to what is kept.</summary>
    public static string? Mask(string? s, Redactor? redactor) =>
        string.IsNullOrEmpty(s) ? s : Clip(redactor == null ? s! : redactor.Mask(Clip(s!, MaxRead)).Text!, MaxString);

    /// <summary>s cut to max characters, "..." ending it (a surrogate pair stays whole).</summary>
    internal static string Clip(string s, int max)
    {
        if (s.Length <= max)
        {
            return s;
        }
        var cut = max - 3;
        if (char.IsHighSurrogate(s[cut - 1]))
        {
            cut--;
        }
#if NET
        return string.Concat(s.AsSpan(0, cut), "...");
#else
        return s.Substring(0, cut) + "...";
#endif
    }

    public static Dictionary<string, object?> PlainMap(IDictionary m) => (Dictionary<string, object?>)Plain(m, 0)!;

    /// <summary>
    /// A value in JSON's own types: dictionaries with string keys, lists, strings, numbers, booleans,
    /// null. Bounded: strings to what redaction reads, containers in depth and (inside the value) in
    /// items; a container inside itself, and one whose enumeration throws, become a string.
    /// </summary>
    public static object? Plain(object? v, int depth) => Plain(v, depth, null);

    private static object? Plain(object? v, int depth, List<object>? path)
    {
        switch (v)
        {
            case null or bool:
                return v;
            case string s:
                return Clip(s, MaxRead);
            case int or long or short or byte or sbyte or uint or ushort or ulong or double or float or decimal:
                return v;
            case Enum en:
                return en.ToString();
            case DateTimeOffset dto:
                return dto.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            case DateTime dt:
                return dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            case IEnumerable items when depth <= MaxDepth:
                path ??= new List<object>();
                foreach (var outer in path)
                {
                    if (ReferenceEquals(outer, v))
                    {
                        return "[Circular ~]";
                    }
                }
                path.Add(v);
                try
                {
                    return Container(items, depth, path);
                }
#pragma warning disable CA1031 // a collection changed while read, a lazy sequence that fails, …
                catch (Exception)
                {
                    return "[Unreadable]";
                }
#pragma warning restore CA1031
                finally
                {
                    path.RemoveAt(path.Count - 1);
                }
            default:
                return Text(v); // Guid, Uri, deep values, …
        }
    }

    private static object Container(IEnumerable items, int depth, List<object> path)
    {
        var max = depth == 0 ? int.MaxValue : MaxItems; // the top is the attributes: each is kept
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
                map[Text(e.Key)] = Plain(e.Value, depth + 1, path);
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
            list.Add(Plain(item, depth + 1, path));
        }
        return list;
    }

    /// <summary>A value's string, cut to what redaction reads; "[Unreadable]" when its ToString throws.</summary>
    private static string Text(object v)
    {
        try
        {
            return Clip(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "", MaxRead);
        }
#pragma warning disable CA1031 // the app's ToString must not cost the event
        catch (Exception)
        {
            return "[Unreadable]";
        }
#pragma warning restore CA1031
    }

    /// <summary>OTLP key-values, empty values left out.</summary>
    public static List<object?> Attributes(IDictionary<string, object?> m)
    {
        var out_ = new List<object?>(m.Count);
        foreach (var kv in m)
        {
            if (IsEmpty(kv.Value))
            {
                continue;
            }
            out_.Add(new Dictionary<string, object?> { ["key"] = kv.Key, ["value"] = Value(kv.Value) });
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

    /// <summary>A value as an OTLP AnyValue.</summary>
    public static Dictionary<string, object?> Value(object? v)
    {
        var p = Plain(v, 0);
        switch (p)
        {
            case null:
                return new() { ["stringValue"] = "" };
            case string s:
                return new() { ["stringValue"] = Clip(s, MaxString) }; // read (and redacted) further, kept to here
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
                    ? new() { ["stringValue"] = Convert.ToString(p, CultureInfo.InvariantCulture) }
                    : new() { ["doubleValue"] = d };
            case List<object?> items:
                var values = new List<object?>(items.Count);
                foreach (var item in items)
                {
                    values.Add(Value(item));
                }
                return new() { ["arrayValue"] = new Dictionary<string, object?> { ["values"] = values } };
            default:
                return new()
                {
                    ["kvlistValue"] = new Dictionary<string, object?> { ["values"] = Attributes((Dictionary<string, object?>)p) },
                };
        }
    }
}
