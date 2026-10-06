using System.Text.RegularExpressions;

namespace Fixwire.Redaction;

/// <summary>A span of text: Start inclusive, End exclusive (UTF-16 indexes).</summary>
internal readonly struct TextSpan
{
    internal TextSpan(int start, int end)
    {
        Start = start;
        End = end;
    }

    internal int Start { get; }

    internal int End { get; }
}

/// <summary>
/// Finds one kind of sensitive value. A cheap literal prefilter skips the pattern on text that
/// cannot match, and a validator rejects look-alikes (Luhn, mod-97, checksums), so trace ids,
/// hashes and timestamps survive.
/// </summary>
internal sealed class Detector
{
    /// <summary>Substrings one of which must appear (any case, unless caseSensitive); empty means always run.</summary>
    private readonly string[] prefilter;

    private readonly bool caseSensitive;

    /// <summary>A cheaper prefilter than literals, when there are none; null means always run.</summary>
    private readonly Func<string, bool>? may;

    private readonly Regex? pattern;

    /// <summary>The group to mask; 0 is the whole match.</summary>
    private readonly int group;

    /// <summary>
    /// The fewest code points the group may hold, when the server's count differs from the
    /// pattern's (it counts code points, .NET counts UTF-16 units); 0 when they agree.
    /// </summary>
    private readonly int minCodePoints;

    /// <summary>Replaces the pattern when set.</summary>
    private readonly Func<Text, List<TextSpan>>? scan;

    private Detector(
        string name,
        bool caseSensitive,
        string[] prefilter,
        Func<string, bool>? may,
        Regex? pattern,
        int group,
        int minCodePoints,
        Func<string, bool>? validate,
        Func<Text, List<TextSpan>>? scan)
    {
        Name = name;
        this.caseSensitive = caseSensitive;
        this.prefilter = prefilter;
        this.may = may;
        this.pattern = pattern;
        this.group = group;
        this.minCodePoints = minCodePoints;
        Validate = validate;
        this.scan = scan;
    }

    internal string Name { get; }

    /// <summary>Rejects look-alikes; null accepts every span.</summary>
    internal Func<string, bool>? Validate { get; }

    /// <summary>A detector built on a regular expression.</summary>
    internal static Detector Pattern(
        string name,
        bool caseSensitive,
        string[] prefilter,
        string regex,
        int group = 0,
        Func<string, bool>? validate = null,
        Func<string, bool>? may = null,
        int minCodePoints = 0) =>
        new(name, caseSensitive, prefilter, may, new Regex(regex, Detectors.Options, Detectors.MatchTimeout), group, minCodePoints, validate, null);

    /// <summary>A detector built on a scanner.</summary>
    internal static Detector Scanner(
        string name,
        bool caseSensitive,
        string[] prefilter,
        Func<Text, List<TextSpan>> scan,
        Func<string, bool>? validate = null) =>
        new(name, caseSensitive, prefilter, null, null, 0, 0, validate, scan);

    /// <summary>Whether the prefilters let t through to the pattern or scanner.</summary>
    internal bool MayMatch(Text t)
    {
        if (prefilter.Length > 0)
        {
            string hay = caseSensitive ? t.S : t.Lower;
            bool hit = false;
            foreach (string p in prefilter)
            {
                if (Detectors.ContainsOrdinal(hay, p))
                {
                    hit = true;
                    break;
                }
            }

            if (!hit)
            {
                return false;
            }
        }

        return may == null || may(t.S);
    }

    /// <summary>The candidate spans, from the scanner or the pattern (the configured group).</summary>
    internal List<TextSpan> Spans(Text t)
    {
        if (scan != null)
        {
            return scan(t);
        }

        string s = t.S;
        var output = new List<TextSpan>();
        for (Match m = pattern!.Match(s); m.Success;)
        {
            Group g = group > 0 && m.Groups[group].Success ? m.Groups[group] : m;
            if (minCodePoints > 0 && CodePoints(s, g.Index, g.Index + g.Length) < minCodePoints)
            {
                // The server finds no match starting here: look again one character on.
                m = pattern.Match(s, m.Index + 1);
                continue;
            }

            output.Add(new TextSpan(g.Index, g.Index + g.Length));
            m = m.NextMatch();
        }

        return output;
    }

    /// <summary>The number of code points in s[start..end) (a surrogate pair counts once).</summary>
    private static int CodePoints(string s, int start, int end)
    {
        int n = 0;
        for (int i = start; i < end; i++)
        {
            if (!(char.IsLowSurrogate(s[i]) && i > start && char.IsHighSurrogate(s[i - 1])))
            {
                n++;
            }
        }

        return n;
    }
}

/// <summary>One string being searched, with what several detectors share.</summary>
internal sealed class Text
{
    private string? lower;
    private List<NumberRun>? numbers;

    internal Text(string s) => S = s;

    internal string S { get; }

    /// <summary>The string in lower case, for the prefilters.</summary>
    internal string Lower => lower ??= Detectors.LowerCase(S);

    /// <summary>The standalone runs of digits, for the card, SSN and TCKN detectors.</summary>
    internal List<NumberRun> Numbers => numbers ??= Detectors.NumberRuns(S);
}
