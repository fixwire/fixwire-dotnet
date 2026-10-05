using System.Collections;
using System.Globalization;
using System.Text;

namespace Fixwire.Internal;

/// <summary>
/// Writes JSON: dictionaries with string keys, lists, strings, numbers, booleans and null. Other
/// values are written as their string.
/// </summary>
internal static class Json
{
    private const int MaxDepth = 64;
    private const string Hex = "0123456789abcdef";

    public static string Write(object? value)
    {
        var b = new StringBuilder(256);
        Write(b, value, 0);
        return b.ToString();
    }

    private static void Write(StringBuilder b, object? v, int depth)
    {
        switch (v)
        {
            case null:
                b.Append("null");
                break;
            case string s:
                String(b, s);
                break;
            case bool x:
                b.Append(x ? "true" : "false");
                break;
            case double or float or decimal:
                Number(b, Convert.ToDouble(v, CultureInfo.InvariantCulture));
                break;
            case IFormattable f when IsInteger(v):
                b.Append(f.ToString(null, CultureInfo.InvariantCulture));
                break;
            case IDictionary d when depth <= MaxDepth:
                b.Append('{');
                var first = true;
                foreach (DictionaryEntry e in d)
                {
                    if (!first)
                    {
                        b.Append(',');
                    }
                    first = false;
                    String(b, Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? "");
                    b.Append(':');
                    Write(b, e.Value, depth + 1);
                }
                b.Append('}');
                break;
            case IEnumerable items when depth <= MaxDepth:
                b.Append('[');
                var firstItem = true;
                foreach (var item in items)
                {
                    if (!firstItem)
                    {
                        b.Append(',');
                    }
                    firstItem = false;
                    Write(b, item, depth + 1);
                }
                b.Append(']');
                break;
            case IDictionary or IEnumerable:
                b.Append("null"); // too deep
                break;
            default:
                String(b, Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");
                break;
        }
    }

    private static bool IsInteger(object v) =>
        v is int or long or short or byte or sbyte or uint or ulong or ushort;

    private static void Number(StringBuilder b, double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            b.Append("null");
        }
        else if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
        {
            b.Append(((long)d).ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            b.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    private static void String(StringBuilder b, string s)
    {
        b.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"':
                    b.Append("\\\"");
                    break;
                case '\\':
                    b.Append("\\\\");
                    break;
                case '\n':
                    b.Append("\\n");
                    break;
                case '\r':
                    b.Append("\\r");
                    break;
                case '\t':
                    b.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        b.Append("\\u00").Append(Hex[(c >> 4) & 0xf]).Append(Hex[c & 0xf]);
                    }
                    else if (c is '\u2028' or '\u2029')
                    {
                        b.Append(c == '\u2028' ? "\\u2028" : "\\u2029");
                    }
                    else if (char.IsHighSurrogate(c) && !(i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])))
                    {
                        b.Append('\ufffd'); // a lone surrogate is not valid UTF-8
                    }
                    else if (char.IsLowSurrogate(c) && !(i > 0 && char.IsHighSurrogate(s[i - 1])))
                    {
                        b.Append('\ufffd');
                    }
                    else
                    {
                        b.Append(c);
                    }
                    break;
            }
        }
        b.Append('"');
    }
}
