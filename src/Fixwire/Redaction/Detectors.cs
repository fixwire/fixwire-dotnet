using System.Text;
using System.Text.RegularExpressions;

namespace Fixwire.Redaction;

/// <summary>
/// The detectors of the Fixwire server's redaction, in its order, with the same patterns,
/// prefilters, validators and scanners.
/// </summary>
/// <remarks>
/// The server's patterns are ASCII-only. .NET's \d, \s and \b take in other scripts, so digits are
/// [0-9], whitespace is [\t\n\f\r ] and the word boundary is spelled out as lookarounds on ASCII
/// word characters. Nothing ignores case through RegexOptions (its folding depends on the runtime):
/// where the server ignores case, each letter is written as a class, with the Kelvin sign next to
/// "k" and the long s next to "s" as the server folds them. Atomic groups stand where the next token
/// cannot match what they took: the same matches without backtracking.
///
/// Three of the server's patterns are scanners here (private keys, JWTs, URL credentials): they
/// find the same matches, but a backtracking engine would take quadratic time on some text.
/// </remarks>
internal static class Detectors
{
    internal const string Ipv4 = "ipv4";

    private const string Word = "[0-9A-Za-z_]";

    /// <summary>The server's word boundary before a word character.</summary>
    private const string Start = "(?<!" + Word + ")";

    /// <summary>The server's word boundary after a word character.</summary>
    private const string End = "(?!" + Word + ")";

    /// <summary>The server's word boundary where either side may be a word character.</summary>
    private const string Edge = "(?:(?<=" + Word + ")(?!" + Word + ")|(?<!" + Word + ")(?=" + Word + "))";

    private const string Ws = @"[\t\n\f\r ]";

    /// <summary>The letters the server's case-insensitive classes add to [A-Za-z]: the long s and the Kelvin sign.</summary>
    private const string Folded = @"\u017F\u212A";

    /// <summary>Whether a prefilter literal must appear in the same case.</summary>
    private const bool ExactCase = true;

    private const bool AnyCase = false;

    /// <summary>Compiled where the runtime can (a little slower to build, faster to run); interpreted elsewhere.</summary>
#if NET
    internal const RegexOptions Options = RegexOptions.Compiled;
#else
    internal const RegexOptions Options = RegexOptions.None;
#endif

    /// <summary>
    /// A bound on every pattern, though none backtracks far: text that cannot be searched in time
    /// throws (and what holds it is not sent) rather than going out unmasked.
    /// </summary>
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Every detector, in the server's order.</summary>
    internal static readonly IReadOnlyList<Detector> Registry = Array.AsReadOnly(new[]
    {
        Detector.Scanner("private_key", ExactCase, Literals("PRIVATE KEY-----"), PrivateKeySpans),
        Detector.Pattern(
            "aws_access_key",
            ExactCase,
            Literals("AKIA", "ASIA", "ABIA", "ACCA"),
            Start + "(?:AKIA|ASIA|ABIA|ACCA)[0-9A-Z]{16}" + End),
        Detector.Pattern("gcp_api_key", ExactCase, Literals("AIza"), Start + @"AIza[0-9A-Za-z_\-]{35}"),
        Detector.Pattern(
            "azure_storage_key",
            AnyCase,
            Literals("accountkey="),
            IgnoringCase("accountkey") + "=([A-Za-z0-9+/" + Folded + "]{86}==)",
            group: 1),
        Detector.Pattern(
            "github_token",
            ExactCase,
            Literals("ghp_", "gho_", "ghu_", "ghs_", "ghr_", "github_pat_"),
            Start + "(?:gh[pousr]_(?>[A-Za-z0-9]{36,255})|github_pat_(?>[A-Za-z0-9_]{60,255}))" + End),
        Detector.Pattern(
            "stripe_key",
            ExactCase,
            Literals("sk_live_", "sk_test_", "rk_live_", "rk_test_", "whsec_"),
            Start + "(?:(?:sk|rk)_(?:live|test)_[0-9A-Za-z]{16,247}|whsec_[A-Za-z0-9+/=]{24,})"),
        Detector.Pattern("slack_token", ExactCase, Literals("xox"), Start + "xox[abposr]-[0-9A-Za-z-]{10,250}" + Edge),
        Detector.Pattern(
            "slack_webhook",
            ExactCase,
            Literals("hooks.slack.com/services/"),
            @"https://hooks\.slack\.com/services/T(?>[A-Z0-9]+)/B(?>[A-Z0-9]+)/[A-Za-z0-9]+"),
        Detector.Pattern(
            "anthropic_key",
            ExactCase,
            Literals("sk-ant-"),
            Start + @"sk-ant-(?:api|admin)[0-9]{2}-[A-Za-z0-9_\-]{80,}"),
        Detector.Pattern(
            "openai_key",
            ExactCase,
            Literals("sk-"),
            Start + @"sk-(?:(?:proj|svcacct|admin)-[A-Za-z0-9_\-]{40,}|[A-Za-z0-9]{20}T3BlbkFJ[A-Za-z0-9]{20})"),
        Detector.Scanner("jwt", ExactCase, Literals("eyJ"), JwtSpans),
        Detector.Pattern(
            "fixwire_secret_key",
            ExactCase,
            Literals("_sk_live_", "_sk_test_"),
            Start + "[a-z]{2,4}_sk_(?:live|test)_[0-9A-Za-z]{38}" + End),

        // The password in scheme://user:password@host (the user stays).
        Detector.Scanner("url_credentials", ExactCase, Literals("://"), UrlCredentialSpans, Unmasked),

        // Bearer and Basic credentials outside a header (messages, breadcrumbs).
        Detector.Pattern(
            "http_auth",
            AnyCase,
            Literals("bearer", "basic"),
            Start + "(?:" + IgnoringCase("bearer") + "|" + IgnoringCase("basic") + ")(?>" + Ws + "+)"
                + @"((?>[A-Za-z0-9._~+/\-" + Folded + "]{12,})=*)",
            group: 1,
            validate: CredentialLike),
        // A value given to a secret's name, in text, config and URLs. The name may end a longer one
        // (access_token, client_secret, csrfToken, PHPSESSID, X-Amz-Signature); an OAuth code counts
        // in a query or fragment only. The name is atomic: what follows it (a quote, a space, ":" or
        // "=") can't be a letter it gave back, so each place is tried once and the scan is linear.
        Detector.Pattern(
            "secret_assignment",
            AnyCase,
            Literals("pass", "pwd", "secret", "key", "token", "credential", "sess", "sig", "code"),
            "(?>" + IgnoringCase("password") + "|" + IgnoringCase("passwd") + "|" + IgnoringCase("pwd")
                + "|" + IgnoringCase("secret") + "(?:[_-]?" + IgnoringCase("key") + ")?"
                + "|" + IgnoringCase("private") + "[_-]?" + IgnoringCase("key")
                + "|" + IgnoringCase("token")
                + "|" + IgnoringCase("api") + "[_-]?" + IgnoringCase("key")
                + "|" + IgnoringCase("access") + "[_-]?" + IgnoringCase("key")
                + "|" + IgnoringCase("credentials") + "?"
                + "|" + IgnoringCase("sess") + "(?:" + IgnoringCase("ion") + ")?[_-]?" + IgnoringCase("id")
                + "|" + IgnoringCase("sig") + "(?:" + IgnoringCase("nature") + ")?"
                + "|[?&#]" + IgnoringCase("code") + ")"
                + "(?>[\"']?)(?>" + Ws + "*)[:=](?>" + Ws + "*)(?>[\"']?)"
                + "([^\\t\\n\\f\\r \"',;&]{6,})",
            group: 1,
            validate: Unmasked,
            minCodePoints: 6),
        Detector.Scanner("email", ExactCase, Literals("@"), EmailSpans),
        Detector.Scanner("credit_card", AnyCase, Literals(), CardSpans),
        Detector.Pattern(
            "iban",
            AnyCase,
            Literals(),
            Start + "[A-Z]{2}[0-9]{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,3})?" + End,
            validate: ValidIban,
            may: MayHoldIban),
        Detector.Scanner("us_ssn", ExactCase, Literals("-"), SsnSpans),
        Detector.Scanner("tr_tckn", AnyCase, Literals(), TcknSpans),
        Detector.Pattern("phone", ExactCase, Literals("+"), @"\+[0-9](?:[ .\-()]?[0-9]){7,14}" + End, validate: ValidPhone),
        Detector.Pattern(
            Ipv4,
            ExactCase,
            Literals("."),
            Start + @"(?:(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}"
                + "(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])" + End),
    });

    private static string[] Literals(params string[] s) => s;

    /// <summary>Whether value appears in s, compared by UTF-16 unit.</summary>
    internal static bool ContainsOrdinal(string s, string value) =>
#if NET
        s.Contains(value, StringComparison.Ordinal);
#else
        s.IndexOf(value, StringComparison.Ordinal) >= 0;
#endif

    /// <summary>
    /// A pattern for lower-case letters in any case, as the server folds them: "k" also matches the
    /// Kelvin sign and "s" the long s.
    /// </summary>
    private static string IgnoringCase(string letters)
    {
        var b = new StringBuilder();
        foreach (char c in letters)
        {
            b.Append('[').Append(c).Append(char.ToUpperInvariant(c));
            if (c == 'k')
            {
                b.Append(@"\u212A");
            }
            else if (c == 's')
            {
                b.Append(@"\u017F");
            }

            b.Append(']');
        }

        return b.ToString();
    }

    /// <summary>
    /// The string in lower case one code point at a time, like the server: U+0130 becomes "i" and
    /// the Kelvin sign "k", and nothing grows.
    /// </summary>
    internal static string LowerCase(string s)
    {
        StringBuilder? b = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                int cp = char.ConvertToUtf32(c, s[i + 1]);
                int l = LowerCodePoint(cp);
                if (l != cp && b == null)
                {
                    b = new StringBuilder(s.Length).Append(s, 0, i);
                }

                if (b != null)
                {
                    b.Append(char.ConvertFromUtf32(l));
                }

                i++;
                continue;
            }

            char lc = LowerChar(c);
            if (lc != c && b == null)
            {
                b = new StringBuilder(s.Length).Append(s, 0, i);
            }

            b?.Append(lc);
        }

        return b == null ? s : b.ToString();
    }

    /// <summary>A character in lower case; U+0130 becomes "i" as on the server (not every runtime maps it).</summary>
    private static char LowerChar(char c) =>
        c < 0x80 ? (c >= 'A' && c <= 'Z' ? (char)(c + 32) : c)
        : c == '\u0130' ? 'i'
        : char.ToLowerInvariant(c);

    /// <summary>A code point past U+FFFF in lower case.</summary>
    private static int LowerCodePoint(int cp)
    {
#if NETCOREAPP3_0_OR_GREATER
        return System.Text.Rune.ToLowerInvariant(new System.Text.Rune(cp)).Value;
#else
        // Letters with case past U+FFFF all lie below U+1F000; emoji and the rest stay as they are.
        if (cp >= 0x1F000)
        {
            return cp;
        }

        string pair = char.ConvertFromUtf32(cp);
        string lower = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToLower(pair);
        return lower.Length == 2 && char.IsSurrogatePair(lower, 0) ? char.ConvertToUtf32(lower, 0) : cp;
#endif
    }

    // Validators.

    /// <summary>Rejects values a scrubber already replaced.</summary>
    internal static bool Unmasked(string v) =>
        !v.StartsWith("[REDACTED", StringComparison.Ordinal) && v != Redactor.Filtered;

    /// <summary>
    /// Tells a token from a word after "basic": it has a digit, a base64 symbol, or capitals past
    /// its first letter ("dXNlcjpwYXNz", but not "Authentication").
    /// </summary>
    internal static bool CredentialLike(string v)
    {
        bool upper = false;
        bool lower = false;
        for (int i = 0; i < v.Length; i++)
        {
            char c = v[i];
            if ((c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=')
            {
                return true;
            }

            if (i > 0)
            {
                upper |= c >= 'A' && c <= 'Z';
                lower |= c >= 'a' && c <= 'z';
            }
        }

        return upper && lower;
    }

    private static readonly string[] CardPrefixes =
    {
        "4", "51", "52", "53", "54", "55", "2221", "2720", "34", "37", "6011", "65", "35", "36", "38", "300", "305", "62",
    };

    /// <summary>Checks the length, a known issuer prefix and the Luhn sum.</summary>
    internal static bool ValidCard(string s)
    {
        string d = Digits(s);
        if (d.Length < 13 || d.Length > 19)
        {
            return false;
        }

        bool known = false;
        foreach (string p in CardPrefixes)
        {
            if (d.StartsWith(p, StringComparison.Ordinal))
            {
                known = true;
                break;
            }
        }

        if (!known)
        {
            return false;
        }

        int sum = 0;
        bool twice = false;
        for (int i = d.Length - 1; i >= 0; i--)
        {
            int n = d[i] - '0';
            if (twice)
            {
                n *= 2;
                if (n > 9)
                {
                    n -= 9;
                }
            }

            sum += n;
            twice = !twice;
        }

        return sum % 10 == 0;
    }

    /// <summary>Checks the length (15 to 34) and the mod-97 checksum.</summary>
    internal static bool ValidIban(string s)
    {
        var compact = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c != ' ')
            {
                compact.Append(c);
            }
        }

        s = compact.ToString();
        if (s.Length < 15 || s.Length > 34)
        {
            return false;
        }

        // The remainder of the decimal number the letters spell (A = 10 … Z = 35),
        // read from the fifth character round to the fourth.
        int rem = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[(i + 4) % s.Length];
            if (c >= '0' && c <= '9')
            {
                rem = ((rem * 10) + (c - '0')) % 97;
            }
            else if (c >= 'A' && c <= 'Z')
            {
                rem = ((rem * 100) + (c - 'A' + 10)) % 97;
            }
            else
            {
                return false;
            }
        }

        return rem == 1;
    }

    /// <summary>Rejects numbers the US never issues.</summary>
    internal static bool ValidSsn(string s)
    {
        string area = s.Substring(0, 3);
        string group = s.Substring(4, 2);
        string serial = s.Substring(7, 4);
        return area != "000" && area != "666" && area[0] != '9' && group != "00" && serial != "0000";
    }

    /// <summary>Checks the Turkish identity number's two check digits.</summary>
    internal static bool ValidTckn(string s)
    {
        if (s.Length != 11 || s[0] == '0')
        {
            return false;
        }

        var d = new int[11];
        for (int i = 0; i < 11; i++)
        {
            d[i] = s[i] - '0';
        }

        int odd = d[0] + d[2] + d[4] + d[6] + d[8];
        int even = d[1] + d[3] + d[5] + d[7];
        if ((((odd * 7) - even) % 10 + 10) % 10 != d[9])
        {
            return false;
        }

        int sum = 0;
        for (int i = 0; i < 10; i++)
        {
            sum += d[i];
        }

        return sum % 10 == d[10];
    }

    /// <summary>Wants an international number of 8 to 15 digits.</summary>
    internal static bool ValidPhone(string s)
    {
        int n = Digits(s).Length;
        return n >= 8 && n <= 15;
    }

    private static string Digits(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c >= '0' && c <= '9')
            {
                b.Append(c);
            }
        }

        return b.ToString();
    }

    // Hand-written scanners for the detectors whose regular expressions would
    // otherwise try every position of digit-heavy text.

    private static bool IsWord(char c) =>
        c == '_' || (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    internal static List<NumberRun> NumberRuns(string s)
    {
        var output = new List<NumberRun>();
        int n = s.Length;
        for (int i = 0; i < n;)
        {
            if (!IsDigit(s[i]) || (i > 0 && IsWord(s[i - 1])))
            {
                i++;
                continue;
            }

            var run = new NumberRun(i);
            int group = 0;
            int j = i;
            while (j < n)
            {
                char c = s[j];
                if (IsDigit(c))
                {
                    run.Digits++;
                    group++;
                    j++;
                    continue;
                }

                if ((c == ' ' || c == '-') && j + 1 < n && IsDigit(s[j + 1]) && (run.Sep == '\0' || run.Sep == c))
                {
                    run.Sep = c;
                    run.AddGroup(group);
                    group = 0;
                    j++;
                    continue;
                }

                break;
            }

            run.AddGroup(group);
            run.End = j;
            if (j == n || !IsWord(s[j]))
            {
                output.Add(run);
            }

            i = j + 1;
        }

        return output;
    }

    private static List<TextSpan> CardSpans(Text t)
    {
        var output = new List<TextSpan>();
        foreach (NumberRun r in t.Numbers)
        {
            if (r.Digits >= 13 && r.Digits <= 19 && ValidCard(t.S.Substring(r.Start, r.End - r.Start)))
            {
                output.Add(new TextSpan(r.Start, r.End));
            }
        }

        return output;
    }

    private static List<TextSpan> SsnSpans(Text t)
    {
        var output = new List<TextSpan>();
        foreach (NumberRun r in t.Numbers)
        {
            if (r.Sep == '-'
                && r.Groups == 3
                && r.FirstGroups[0] == 3
                && r.FirstGroups[1] == 2
                && r.FirstGroups[2] == 4
                && ValidSsn(t.S.Substring(r.Start, r.End - r.Start)))
            {
                output.Add(new TextSpan(r.Start, r.End));
            }
        }

        return output;
    }

    private static List<TextSpan> TcknSpans(Text t)
    {
        var output = new List<TextSpan>();
        foreach (NumberRun r in t.Numbers)
        {
            if (r.Sep == '\0' && r.Digits == 11 && ValidTckn(t.S.Substring(r.Start, r.End - r.Start)))
            {
                output.Add(new TextSpan(r.Start, r.End));
            }
        }

        return output;
    }

    private static bool IsLocal(char c) => IsWord(c) || c == '.' || c == '%' || c == '+' || c == '-';

    private static bool IsDomain(char c) => (IsWord(c) && c != '_') || c == '.' || c == '-';

    /// <summary>
    /// Grows outwards from each "@" over the characters an address may hold, and keeps it if the
    /// domain ends in a dotted, alphabetic TLD.
    /// </summary>
    private static List<TextSpan> EmailSpans(Text t)
    {
        string s = t.S;
        var output = new List<TextSpan>();
        for (int i = s.IndexOf('@', 0); i >= 0; i = s.IndexOf('@', i + 1))
        {
            int start = i;
            int end = i + 1;
            while (start > 0 && IsLocal(s[start - 1]))
            {
                start--;
            }

            while (end < s.Length && IsDomain(s[end]))
            {
                end++;
            }

            while (end > i + 1 && (s[end - 1] == '.' || s[end - 1] == '-'))
            {
                end--;
            }

            // The last dot of the domain, past its first character.
            int dot = end - 1;
            while (dot > i + 1 && s[dot] != '.')
            {
                dot--;
            }

            if (start < i && dot > i + 1)
            {
                int tld = end - dot - 1;
                bool ok = tld >= 2 && tld <= 24;
                for (int k = dot + 1; k < end && ok; k++)
                {
                    char c = s[k];
                    ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                }

                while (start < i && (s[start] == '.' || s[start] == '-'))
                {
                    start++;
                }

                if (ok && start < i)
                {
                    output.Add(new TextSpan(start, end));
                }
            }
        }

        return output;
    }

    /// <summary>Whether two capitals and two digits start a word.</summary>
    private static bool MayHoldIban(string s)
    {
        for (int i = 0; i + 4 <= s.Length; i++)
        {
            if (IsUpper(s[i])
                && IsUpper(s[i + 1])
                && IsDigit(s[i + 2])
                && IsDigit(s[i + 3])
                && (i == 0 || !IsWord(s[i - 1])))
            {
                return true;
            }
        }

        return false;
    }

    // Scanners for two of the server's patterns, finding the same leftmost
    // matches in linear time.

    private const string KeyLabel = "PRIVATE KEY-----";

    /// <summary>
    /// The server's <c>-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----[\s\S]*?-----END (?:[A-Z ]+ )?PRIVATE KEY-----</c>:
    /// each BEGIN line with the first END line after it.
    /// </summary>
    private static List<TextSpan> PrivateKeySpans(Text t)
    {
        string s = t.S;
        var output = new List<TextSpan>();
        for (int from = 0; ;)
        {
            int begin = s.IndexOf("-----BEGIN ", from, StringComparison.Ordinal);
            if (begin < 0)
            {
                break;
            }

            int head = KeyLabelEnd(s, begin + 11);
            if (head < 0)
            {
                from = begin + 1;
                continue;
            }

            int end = -1;
            int line = s.IndexOf("-----END ", head, StringComparison.Ordinal);
            while (line >= 0 && (end = KeyLabelEnd(s, line + 9)) < 0)
            {
                line = s.IndexOf("-----END ", line + 1, StringComparison.Ordinal);
            }

            if (end < 0)
            {
                break; // a later BEGIN line finds no END line either
            }

            output.Add(new TextSpan(begin, end));
            from = end;
        }

        return output;
    }

    /// <summary>
    /// The end of <c>(?:[A-Z ]+ )?PRIVATE KEY-----</c> at i, or -1. "PRIVATE KEY" can only end the
    /// run of capitals and spaces from i, so there is one place to look.
    /// </summary>
    private static int KeyLabelEnd(string s, int i)
    {
        int run = i;
        while (run < s.Length && (IsUpper(s[run]) || s[run] == ' '))
        {
            run++;
        }

        int label = run - "PRIVATE KEY".Length;
        if (label < i || string.CompareOrdinal(s, label, KeyLabel, 0, KeyLabel.Length) != 0)
        {
            return -1;
        }

        if (label > i && (label < i + 2 || s[label - 1] != ' '))
        {
            return -1; // the type before the label needs a letter or space and then a space
        }

        return label + KeyLabel.Length;
    }

    private static bool IsUpper(char c) => c >= 'A' && c <= 'Z';

    /// <summary>The end of the run of <c>[A-Za-z0-9_-]</c> from i.</summary>
    private static int JwtRunEnd(string s, int i)
    {
        while (i < s.Length && (IsWord(s[i]) || s[i] == '-'))
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// The server's <c>\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}</c>. Every
    /// start inside the header's run reaches the same dot and fails as the first did, so the search
    /// goes on past it ("eyJ-eyJ-…" would otherwise be quadratic).
    /// </summary>
    private static List<TextSpan> JwtSpans(Text t)
    {
        string s = t.S;
        var output = new List<TextSpan>();
        for (int start = s.IndexOf("eyJ", StringComparison.Ordinal); start >= 0;)
        {
            if (start > 0 && IsWord(s[start - 1]))
            {
                start = s.IndexOf("eyJ", start + 1, StringComparison.Ordinal);
                continue;
            }

            int header = JwtRunEnd(s, start + 3);
            int end = -1;
            if (header - start - 3 >= 8 && JwtDotEyJ(s, header))
            {
                int payload = JwtRunEnd(s, header + 4);
                if (payload - header - 4 >= 8 && payload < s.Length && s[payload] == '.')
                {
                    int signature = JwtRunEnd(s, payload + 1);
                    if (signature - payload - 1 >= 8)
                    {
                        end = signature;
                    }
                }
            }

            if (end >= 0)
            {
                output.Add(new TextSpan(start, end));
            }

            start = s.IndexOf("eyJ", end >= 0 ? end : header, StringComparison.Ordinal);
        }

        return output;
    }

    /// <summary>Whether ".eyJ" is at i.</summary>
    private static bool JwtDotEyJ(string s, int i) =>
        i + 4 <= s.Length && string.CompareOrdinal(s, i, ".eyJ", 0, 4) == 0;

    private static bool IsLetter(char c) => (c >= 'a' && c <= 'z') || IsUpper(c);

    private static bool IsScheme(char c) => IsLetter(c) || IsDigit(c) || c == '+' || c == '.' || c == '-';

    /// <summary>Whether c ends the password of a URL: whitespace, "/", "?", "#" or "@".</summary>
    private static bool EndsPassword(char c)
    {
        switch (c)
        {
            case '\t':
            case '\n':
            case '\f':
            case '\r':
            case ' ':
            case '/':
            case '?':
            case '#':
            case '@':
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The password of the server's <c>\b[A-Za-z][A-Za-z0-9+.\-]*://[^\s/?#@:]*:([^\s/?#@]+)@</c>.
    /// Every start in the scheme before one "://" shares the rest of the match, so only the first
    /// is tried.
    /// </summary>
    private static List<TextSpan> UrlCredentialSpans(Text t)
    {
        string s = t.S;
        int n = s.Length;
        var output = new List<TextSpan>();
        int from = 0;
        for (int sep = s.IndexOf("://", StringComparison.Ordinal); sep >= 0;)
        {
            int scheme = sep;
            while (scheme > from && IsScheme(s[scheme - 1]))
            {
                scheme--;
            }

            // The first letter at a word boundary starts the scheme.
            while (scheme < sep && !(IsLetter(s[scheme]) && (scheme == 0 || !IsWord(s[scheme - 1]))))
            {
                scheme++;
            }

            if (scheme < sep)
            {
                int user = sep + 3;
                while (user < n && s[user] != ':' && !EndsPassword(s[user]))
                {
                    user++;
                }

                if (user < n && s[user] == ':')
                {
                    int end = user + 1;
                    while (end < n && !EndsPassword(s[end]))
                    {
                        end++;
                    }

                    if (end > user + 1 && end < n && s[end] == '@')
                    {
                        output.Add(new TextSpan(user + 1, end));
                        from = end + 1;
                        sep = s.IndexOf("://", from, StringComparison.Ordinal);
                        continue;
                    }
                }
            }

            sep = s.IndexOf("://", sep + 1, StringComparison.Ordinal);
        }

        return output;
    }
}

/// <summary>A run of digits, optionally split by single spaces or dashes, that stands alone as a word.</summary>
internal sealed class NumberRun
{
    internal NumberRun(int start) => Start = start;

    internal int Start { get; }

    internal int End { get; set; }

    internal int Digits { get; set; }

    /// <summary>The separator, '\0' when unbroken.</summary>
    internal char Sep { get; set; }

    /// <summary>The number of digit groups.</summary>
    internal int Groups { get; private set; }

    /// <summary>The sizes of the first three digit groups.</summary>
    internal int[] FirstGroups { get; } = new int[3];

    internal void AddGroup(int size)
    {
        if (Groups < 3)
        {
            FirstGroups[Groups] = size;
        }

        Groups++;
    }
}
