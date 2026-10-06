using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace Fixwire.Redaction;

/// <summary>
/// Masks secrets and personal data on the device before anything is sent, with the same output
/// as the Fixwire server's redaction (proven by the shared corpus pkg/redact/testdata/vectors.json).
/// </summary>
/// <remarks>
/// Detectors run in a fixed order; a cheap prefilter skips each one on text that cannot match,
/// and validators (Luhn, mod-97, checksums) reject look-alikes so trace ids, hashes and timestamps
/// survive. Immutable and safe for concurrent use. Not part of the SDK's API.
/// </remarks>
internal sealed class Redactor
{
    /// <summary>Replaces the value of a sensitive key.</summary>
    public const string Filtered = "[Filtered]";

    /// <summary>Key fragments whose values are always filtered whole.</summary>
    public static readonly IReadOnlyList<string> DefaultSensitiveKeys = Array.AsReadOnly(new[]
    {
        "password", "passwd", "pwd", "secret", "apikey", "accesskey", "token", "credential", "privatekey",
        "authorization", "cookie", "sessionid", "csrf", "xsrf", "cvv", "cvc", "ssn", "creditcard", "cardnumber",
    });

    /// <summary>
    /// The detectors on by default, in the server's order: all but ipv4 (in error messages IP
    /// addresses are usually servers worth seeing).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultDetectors = DetectorNames();

    /// <summary>The JSON writer writes no container deeper than this (it writes null), so Walk walks none deeper.</summary>
    private const int MaxDepth = 64;

    private static readonly Redactor DefaultRedactor = new(DefaultDetectors, null);

    private readonly Detector[] detectors;
    private readonly string[] keys;

    /// <summary>A redactor with the named detectors; sensitiveKeys null means the defaults.</summary>
    /// <exception cref="ArgumentException">A detector name is unknown.</exception>
    internal Redactor(IReadOnlyList<string> detectorNames, IReadOnlyList<string>? sensitiveKeys)
    {
        var ds = new List<Detector>(detectorNames.Count);
        foreach (string name in detectorNames)
        {
            Detector? found = null;
            foreach (Detector d in Detectors.Registry)
            {
                if (d.Name == name)
                {
                    found = d;
                    break;
                }
            }

            ds.Add(found ?? throw new ArgumentException("redact: unknown detector \"" + name + "\"", nameof(detectorNames)));
        }

        detectors = ds.ToArray();
        var ks = new List<string>();
        if (sensitiveKeys == null)
        {
            ks.AddRange(DefaultSensitiveKeys);
        }
        else
        {
            foreach (string? k in sensitiveKeys)
            {
                if (k != null)
                {
                    ks.Add(NormalizeKey(k));
                }
            }
        }

        keys = ks.ToArray();
    }

    /// <summary>The redactor with the default detectors and sensitive keys.</summary>
    public static Redactor Default => DefaultRedactor;

    /// <summary>
    /// A redactor with the default detectors. Its sensitive keys replace the defaults, compared
    /// like the server does (lower case, without "-", "_" and spaces); null keeps the defaults.
    /// </summary>
    public static Redactor Create(IReadOnlyList<string>? sensitiveKeys) =>
        sensitiveKeys == null ? DefaultRedactor : new Redactor(DefaultDetectors, sensitiveKeys);

    /// <summary>
    /// Masks the findings in s: each becomes <c>[REDACTED:&lt;detector&gt;]</c>, as the server
    /// writes it. Null gives null text and no findings.
    /// </summary>
    public MaskResult Mask(string? s)
    {
        IReadOnlyList<Finding> fs = s == null ? Array.Empty<Finding>() : Find(s);
        if (fs.Count == 0)
        {
            return new MaskResult(s, Array.Empty<string>());
        }

        var names = new string[fs.Count];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = fs[i].Detector;
        }

        return new MaskResult(Replace(s!, fs), names);
    }

    /// <summary>
    /// Masks every string in a JSON-like value and filters the values of sensitive keys, by the
    /// server's rules: a typed attribute (<c>{"type": …, "value": …}</c>) keeps its shape, a list
    /// of two holding a sensitive key and a value is a pair, keys that count tokens are not
    /// secrets, and keys that hold data are masked too (keys that mask alike are numbered in code
    /// point order: <c>"[REDACTED:email] (2)"</c>).
    /// </summary>
    /// <remarks>
    /// Dictionaries (<c>IDictionary&lt;string, object?&gt;</c>) and lists (<c>IList&lt;object?&gt;</c>,
    /// object arrays too) are changed in place; read-only ones are copied when something in them
    /// is masked. Numbers, booleans and null stay as they are. Anything else is masked as the text
    /// the SDK's JSON writer writes for it, only when something in it is masked: other
    /// dictionaries and collections become a <c>Dictionary&lt;string, object?&gt;</c> or a
    /// <c>List&lt;object?&gt;</c>, other objects their string. Containers nested deeper than the
    /// writer goes are left alone (it writes null).
    /// </remarks>
    /// <param name="value">The value.</param>
    /// <param name="count">Grows by the number of values masked.</param>
    /// <returns>The masked value: value itself, unless it had to be replaced.</returns>
    public object? Walk(object? value, ref int count) => Walk(value, ref count, 0);

    /// <summary>The non-overlapping findings in s, leftmost first; when two overlap, the earlier detector wins.</summary>
    internal IReadOnlyList<Finding> Find(string s)
    {
        if (s.Length == 0)
        {
            return Array.Empty<Finding>();
        }

        // Findings are kept by start and never overlap, so a span is checked against its two
        // neighbours only, and each detector's findings are merged in at once: thousands of
        // findings in one text stay fast.
        var t = new Text(s);
        List<Finding>? output = null;
        foreach (Detector d in detectors)
        {
            if (!d.MayMatch(t))
            {
                continue;
            }

            List<Finding>? found = null;
            foreach (TextSpan span in d.Spans(t))
            {
                if (d.Validate != null && !d.Validate(s.Substring(span.Start, span.End - span.Start)))
                {
                    continue;
                }

                if (Overlaps(output, span) || Overlaps(found, span))
                {
                    continue;
                }

                found ??= new List<Finding>();
                found.Insert(InsertionPoint(found, span.Start), new Finding(d.Name, span.Start, span.End)); // at the end: spans come leftmost first
            }

            if (found != null)
            {
                output = output == null ? found : Merge(output, found);
            }
        }

        return output ?? (IReadOnlyList<Finding>)Array.Empty<Finding>();
    }

    /// <summary>Where a finding starting at start goes in fs, which is by start.</summary>
    private static int InsertionPoint(List<Finding> fs, int start)
    {
        int lo = 0;
        int hi = fs.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (fs[mid].Start < start)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>Whether span overlaps one of fs (by start, not overlapping): only the neighbours of its place can.</summary>
    private static bool Overlaps(List<Finding>? fs, TextSpan span)
    {
        if (fs == null)
        {
            return false;
        }

        int at = InsertionPoint(fs, span.Start);
        return (at > 0 && Overlaps(fs[at - 1], span)) || (at < fs.Count && Overlaps(fs[at], span));
    }

    private static bool Overlaps(Finding f, TextSpan span) => span.Start < f.End && f.Start < span.End;

    /// <summary>Two lists of findings by start as one.</summary>
    private static List<Finding> Merge(List<Finding> a, List<Finding> b)
    {
        var output = new List<Finding>(a.Count + b.Count);
        int i = 0;
        int j = 0;
        while (i < a.Count || j < b.Count)
        {
            output.Add(j == b.Count || (i < a.Count && a[i].Start < b[j].Start) ? a[i++] : b[j++]);
        }

        return output;
    }

    /// <summary>s with each finding replaced by [REDACTED:detector].</summary>
    private static string Replace(string s, IReadOnlyList<Finding> fs)
    {
        var b = new StringBuilder(s.Length + (24 * fs.Count));
        int last = 0;
        foreach (Finding f in fs)
        {
            b.Append(s, last, f.Start - last).Append("[REDACTED:").Append(f.Detector).Append(']');
            last = f.End;
        }

        return b.Append(s, last, s.Length - last).ToString();
    }

    internal static string NormalizeKey(string k)
    {
        string l = Detectors.LowerCase(k);
        StringBuilder? b = null;
        for (int i = 0; i < l.Length; i++)
        {
            char c = l[i];
            if (c == '-' || c == '_' || c == ' ')
            {
                b ??= new StringBuilder(l.Length).Append(l, 0, i);
            }
            else
            {
                b?.Append(c);
            }
        }

        return b == null ? l : b.ToString();
    }

    /// <summary>Whether a key's value must be filtered whole.</summary>
    internal bool Sensitive(string key)
    {
        string k = NormalizeKey(key);
        if (k == "auth")
        {
            return true;
        }

        foreach (string frag in keys)
        {
            if (Detectors.ContainsOrdinal(k, frag) && (frag != "token" || !TokenCount(k)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Keys that count model tokens rather than hold one: gen_ai.usage.input_tokens, max_tokens, token_count.</summary>
    private static bool TokenCount(string k) =>
        k.EndsWith("tokens", StringComparison.Ordinal)
        || Detectors.ContainsOrdinal(k, "tokencount")
        || Detectors.ContainsOrdinal(k, "usage");

    // Values are walked as the SDK's JSON writer (Fixwire.Internal.Json) writes them.
    private object? Walk(object? v, ref int n, int depth)
    {
        switch (v)
        {
            case null:
            case bool:
            case double or float or decimal:
            case int or long or short or byte or sbyte or uint or ulong or ushort:
                return v; // written as null, booleans and numbers
            case string s:
                return MaskText(s, s, ref n);
            case Array a when WrittenAsNumbers(a):
                return v; // numbers, booleans or single characters
            case IDictionary or IEnumerable when depth > MaxDepth:
                return v; // the writer writes null instead
            case IDictionary<string, object?> map when !map.IsReadOnly:
                return WalkMap(map, ref n, depth);
            case IList<object?> list when !list.IsReadOnly || v.GetType() == typeof(object[]):
                // Arrays report read-only but take new elements; arrays of a narrower type may not.
                return WalkList(list, ref n, depth);
        }

        // A copy is changed, and kept only when something in it was masked.
        int before = n;
        object? copy;
        switch (v)
        {
            case IDictionary<string, object?> map:
                copy = WalkMap(new Dictionary<string, object?>(map), ref n, depth);
                break;
            case IDictionary other:
                // Keys are written as their strings.
                var map2 = new Dictionary<string, object?>(other.Count);
                foreach (DictionaryEntry e in other)
                {
                    map2[Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? string.Empty] = e.Value;
                }

                copy = WalkMap(map2, ref n, depth);
                break;
            case IEnumerable items:
                var list2 = new List<object?>();
                foreach (object? item in items)
                {
                    list2.Add(item);
                }

                copy = WalkList(list2, ref n, depth);
                break;
            default:
                // Anything else is written as its string.
                string text = Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty;
                return MaskText(text, v, ref n);
        }

        return n != before ? copy : v;
    }

    private static bool WrittenAsNumbers(Array a)
    {
        Type? t = a.GetType().GetElementType();
        return t != null && t.IsPrimitive && t != typeof(IntPtr) && t != typeof(UIntPtr);
    }

    /// <summary>The masked text when it holds findings, else unchanged.</summary>
    private object MaskText(string text, object unchanged, ref int n)
    {
        IReadOnlyList<Finding> fs = Find(text);
        if (fs.Count == 0)
        {
            return unchanged;
        }

        n += fs.Count;
        return Replace(text, fs);
    }

    private IDictionary<string, object?> WalkMap(IDictionary<string, object?> map, ref int n, int depth)
    {
        // Keys are copied first: not every dictionary takes a new value while it is enumerated.
        var keyList = new string[map.Count];
        map.Keys.CopyTo(keyList, 0);
        List<Rename>? renamed = null;
        foreach (string key in keyList)
        {
            if (!map.TryGetValue(key, out object? val))
            {
                continue;
            }

            IReadOnlyList<Finding> fs = Find(key);
            if (fs.Count > 0)
            {
                renamed ??= new List<Rename>();
                renamed.Add(new Rename(key, Replace(key, fs), fs.Count));
            }

            if (Sensitive(key) && !Empty(val))
            {
                // A typed attribute ({"type": …, "value": …}) keeps its shape.
                if (val is IDictionary<string, object?> typed && typed.TryGetValue("value", out object? inner) && inner != null)
                {
                    if (!IsFiltered(inner))
                    {
                        if (typed.IsReadOnly)
                        {
                            typed = new Dictionary<string, object?>(typed);
                            map[key] = typed;
                        }

                        typed["value"] = Filtered;
                        typed["type"] = "string";
                        n++;
                    }
                }
                else if (!IsFiltered(val))
                {
                    map[key] = Filtered;
                    n++;
                }

                continue;
            }

            object? w = Walk(val, ref n, depth + 1);
            if (!ReferenceEquals(w, val))
            {
                map[key] = w;
            }
        }

        if (renamed != null)
        {
            // Keys hold data too ({"ada@example.com": 3}). Keys that mask alike
            // are numbered in key order: "[REDACTED:email] (2)".
            renamed.Sort();
            foreach (Rename r in renamed)
            {
                if (!map.TryGetValue(r.Key, out object? val))
                {
                    continue;
                }

                string key = r.Masked;
                for (int i = 2; map.ContainsKey(key); i++)
                {
                    key = r.Masked + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
                }

                map.Remove(r.Key);
                map[key] = val;
                n += r.Count;
            }
        }

        return map;
    }

    private IList<object?> WalkList(IList<object?> list, ref int n, int depth)
    {
        // Some maps are sent as [key, value] pairs (headers, tags).
        if (list.Count == 2 && list[0] is string k && Sensitive(k) && !Empty(list[1]))
        {
            list[1] = Filtered;
            n++;
            return list;
        }

        for (int i = 0; i < list.Count; i++)
        {
            object? item = list[i];
            object? w = Walk(item, ref n, depth + 1);
            if (!ReferenceEquals(w, item))
            {
                list[i] = w;
            }
        }

        return list;
    }

    private static bool Empty(object? v) => v == null || (v is string s && s.Length == 0);

    private static bool IsFiltered(object? v) => v is string s && s == Filtered;

    private static ReadOnlyCollection<string> DetectorNames()
    {
        var names = new List<string>();
        foreach (Detector d in Detectors.Registry)
        {
            if (d.Name != Detectors.Ipv4)
            {
                names.Add(d.Name);
            }
        }

        return names.AsReadOnly();
    }

    /// <summary>A key that holds data, with its masked form.</summary>
    private sealed class Rename : IComparable<Rename>
    {
        internal Rename(string key, string masked, int count)
        {
            Key = key;
            Masked = masked;
            Count = count;
        }

        internal string Key { get; }

        internal string Masked { get; }

        internal int Count { get; }

        /// <summary>By code point, as the server sorts (UTF-16 order differs past U+FFFF).</summary>
        public int CompareTo(Rename? other)
        {
            if (other == null)
            {
                return 1;
            }

            string a = Key;
            string b = other.Key;
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int x = a[i];
                int y = b[i];
                if (x != y)
                {
                    if (x >= 0xD800 && y >= 0xD800)
                    {
                        // Surrogates go above U+E000..U+FFFF.
                        x = x >= 0xE000 ? x - 0x800 : x + 0x2000;
                        y = y >= 0xE000 ? y - 0x800 : y + 0x2000;
                    }

                    return x - y;
                }
            }

            return a.Length - b.Length;
        }
    }
}

/// <summary>One match: the detector and the span it covers.</summary>
internal readonly struct Finding
{
    internal Finding(string detector, int start, int end)
    {
        Detector = detector;
        Start = start;
        End = end;
    }

    internal string Detector { get; }

    internal int Start { get; }

    internal int End { get; }
}

/// <summary>A string with its findings masked.</summary>
internal sealed class MaskResult
{
    internal MaskResult(string? text, IReadOnlyList<string> findings)
    {
        Text = text;
        Findings = findings;
    }

    /// <summary>The text with each finding replaced by <c>[REDACTED:&lt;detector&gt;]</c>; null when the input was null.</summary>
    public string? Text { get; }

    /// <summary>The detector of each finding, in the order they appear.</summary>
    public IReadOnlyList<string> Findings { get; }
}
