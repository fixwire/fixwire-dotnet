using System.Globalization;
using System.Text;
using System.Text.Json;
using Fixwire.Redaction;

namespace Fixwire.Tests.Redaction;

/// <summary>
/// The shared corpus of the Fixwire server's redaction (pkg/redact/testdata/vectors.json): this
/// port must mask every string and document exactly as the server does.
/// </summary>
public sealed class VectorsTest
{
    private static readonly JsonElement Vectors = Load();

    private static readonly Dictionary<string, string> Fixtures = Vectors.GetProperty("fixtures")
        .EnumerateObject()
        .ToDictionary(p => p.Name, p => string.Concat(p.Value.EnumerateArray().Select(x => x.GetString())));

    public static TheoryData<string> StringCases => Names("strings");

    public static TheoryData<string> DocumentCases => Names("documents");

    [Fact]
    public void SameDetectorsAndKeys()
    {
        Assert.Equal(List("detectors"), Redactor.DefaultDetectors);
        Assert.Equal(List("sensitive_keys"), Redactor.DefaultSensitiveKeys);
    }

    [Theory]
    [MemberData(nameof(StringCases))]
    public void Strings(string name)
    {
        JsonElement tc = Case("strings", name);
        MaskResult m = Redactor.Default.Mask(Expand(tc.GetProperty("input").GetString()!));
        Assert.Equal(tc.GetProperty("masked").GetString(), m.Text);
        Assert.Equal(tc.GetProperty("findings").EnumerateArray().Select(x => x.GetString()!), m.Findings);
    }

    [Theory]
    [MemberData(nameof(DocumentCases))]
    public void Documents(string name)
    {
        JsonElement tc = Case("documents", name);
        int count = 0;
        object? got = Redactor.Default.Walk(Decode(tc.GetProperty("input"), expand: true), ref count);
        Assert.Equal(Canonical(Decode(tc.GetProperty("masked"), expand: false)), Canonical(got));
        Assert.Equal(tc.GetProperty("count").GetInt32(), count);
    }

    /// <summary>The corpus, in the repository's pkg/ above this test, or where FIXWIRE_VECTORS points.</summary>
    private static JsonElement Load()
    {
        string? file = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "pkg", "redact", "testdata", "vectors.json");
            if (File.Exists(candidate))
            {
                file = candidate;
                break;
            }
        }

        file ??= Environment.GetEnvironmentVariable("FIXWIRE_VECTORS");
        if (string.IsNullOrEmpty(file))
        {
            throw new InvalidOperationException(
                "pkg/redact/testdata/vectors.json not found above " + AppContext.BaseDirectory + "; set FIXWIRE_VECTORS");
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.Clone();
    }

    private static TheoryData<string> Names(string section)
    {
        var data = new TheoryData<string>();
        foreach (JsonElement c in Vectors.GetProperty(section).EnumerateArray())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }

        return data;
    }

    private static List<string> List(string section) =>
        Vectors.GetProperty(section).EnumerateArray().Select(x => x.GetString()!).ToList();

    private static JsonElement Case(string section, string name) =>
        Vectors.GetProperty(section).EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    /// <summary>Replaces each {{fixture}} with its parts joined (kept apart in the file).</summary>
    private static string Expand(string s)
    {
        foreach (KeyValuePair<string, string> f in Fixtures)
        {
            s = s.Replace("{{" + f.Key + "}}", f.Value, StringComparison.Ordinal);
        }

        return s;
    }

    /// <summary>A mutable value of the JSON (keys too have their fixtures expanded when asked).</summary>
    private static object? Decode(JsonElement e, bool expand)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>();
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    map[expand ? Expand(p.Name) : p.Name] = Decode(p.Value, expand);
                }

                return map;
            case JsonValueKind.Array:
                return e.EnumerateArray().Select(x => Decode(x, expand)).ToList();
            case JsonValueKind.String:
                return expand ? Expand(e.GetString()!) : e.GetString();
            case JsonValueKind.Number:
                return e.TryGetDecimal(out decimal d) ? d : e.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    /// <summary>The value as JSON with sorted keys, to compare documents by value.</summary>
    private static string Canonical(object? v)
    {
        var b = new StringBuilder();
        Write(b, v);
        return b.ToString();
    }

    private static void Write(StringBuilder b, object? v)
    {
        switch (v)
        {
            case null:
                b.Append("null");
                break;
            case string s:
                b.Append(JsonSerializer.Serialize(s));
                break;
            case bool x:
                b.Append(x ? "true" : "false");
                break;
            case decimal d:
                b.Append(d.ToString("G29", CultureInfo.InvariantCulture));
                break;
            case double d:
                b.Append(d.ToString("R", CultureInfo.InvariantCulture));
                break;
            case IDictionary<string, object?> map:
                b.Append('{');
                foreach (string k in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    b.Append(JsonSerializer.Serialize(k)).Append(':');
                    Write(b, map[k]);
                    b.Append(',');
                }

                b.Append('}');
                break;
            case IList<object?> list:
                b.Append('[');
                foreach (object? x in list)
                {
                    Write(b, x);
                    b.Append(',');
                }

                b.Append(']');
                break;
            default:
                throw new InvalidOperationException("unexpected " + v.GetType());
        }
    }
}
