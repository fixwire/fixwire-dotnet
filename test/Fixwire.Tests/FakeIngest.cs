using System.IO.Compression;
using System.Net;
using System.Text.Json;

namespace Fixwire.Tests;

/// <summary>A fake Fixwire behind the SDK's HTTP handler: records each request's decoded body.</summary>
public sealed class FakeIngest : HttpMessageHandler
{
    public sealed record Received(string Path, string? Authorization, string? Encoding, string? UserAgent, Dictionary<string, object?> Body);

    private readonly List<Received> _received = new();

    /// <summary>The status and headers of the n-th answer (by default 200).</summary>
    public Func<int, string, (int Status, Dictionary<string, string> Headers)> Answer { get; set; } =
        (_, _) => (200, new Dictionary<string, string>());

    public const string Dsn = "http://publickey@ingest.test";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var raw = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        Stream body = new MemoryStream(raw);
        if (request.Content.Headers.ContentEncoding.Contains("gzip"))
        {
            body = new GZipStream(body, CompressionMode.Decompress);
        }
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        int n;
        var path = request.RequestUri!.AbsolutePath;
        lock (_received)
        {
            n = _received.Count;
            _received.Add(new Received(
                Uri.UnescapeDataString(request.RequestUri.AbsolutePath),
                request.Headers.Authorization?.ToString(),
                string.Join(",", request.Content.Headers.ContentEncoding),
                request.Headers.UserAgent.ToString(),
                (Dictionary<string, object?>)Plain(doc.RootElement)!));
        }
        var (status, headers) = Answer(n, path);
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{}") };
        foreach (var h in headers)
        {
            response.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        return response;
    }

    public List<Received> Requests(string path = "")
    {
        lock (_received)
        {
            return _received.Where(r => path.Length == 0 || r.Path == path).ToList();
        }
    }

    /// <summary>A hub whose client sends here.</summary>
    public Hub Hub(Action<FixwireOptions>? configure = null)
    {
        var o = new FixwireOptions
        {
            Dsn = Dsn,
            ServiceName = "shop",
            HttpMessageHandler = this,
            CaptureUnhandledExceptions = false,
        };
        configure?.Invoke(o);
        return new Hub(new Client(o));
    }

    public static object? Plain(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Plain(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(Plain).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    // OTLP readers.

    public static List<Dictionary<string, object?>> LogRecords(IEnumerable<Received> requests) =>
        requests.SelectMany(r => List(r.Body["resourceLogs"])
            .SelectMany(rl => List(Map(rl)["scopeLogs"]))
            .SelectMany(sl => List(Map(sl)["logRecords"]))
            .Select(Map)).ToList();

    public static List<Dictionary<string, object?>> Spans(IEnumerable<Received> requests) =>
        requests.SelectMany(r => List(r.Body["resourceSpans"])
            .SelectMany(rs => List(Map(rs)["scopeSpans"]))
            .SelectMany(ss => List(Map(ss)["spans"]))
            .Select(Map)).ToList();

    public static Dictionary<string, object?> Resource(Received r)
    {
        var key = r.Body.ContainsKey("resourceLogs") ? "resourceLogs" : "resourceSpans";
        return Kv(Map(Map(List(r.Body[key])[0])["resource"])["attributes"]);
    }

    /// <summary>OTLP key-values as plain values.</summary>
    public static Dictionary<string, object?> Kv(object? list) =>
        List(list).Select(Map).ToDictionary(m => (string)m["key"]!, m => AnyValue(Map(m["value"])));

    public static object? AnyValue(Dictionary<string, object?> v)
    {
        if (v.TryGetValue("stringValue", out var s))
        {
            return s;
        }
        if (v.TryGetValue("boolValue", out var b))
        {
            return b;
        }
        if (v.TryGetValue("intValue", out var i))
        {
            return long.Parse((string)i!, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (v.TryGetValue("doubleValue", out var d))
        {
            return Convert.ToDouble(d, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (v.TryGetValue("arrayValue", out var a))
        {
            return Map(a).TryGetValue("values", out var vs) ? List(vs).Select(x => AnyValue(Map(x))).ToList() : new List<object?>();
        }
        return v.TryGetValue("kvlistValue", out var kv) ? Kv(Map(kv).GetValueOrDefault("values")) : null;
    }

    public static Dictionary<string, object?> Map(object? o) => (Dictionary<string, object?>)o!;

    public static List<object?> List(object? o) => o as List<object?> ?? new List<object?>();
}
