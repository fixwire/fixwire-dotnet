using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Fixwire.Redaction;

namespace Fixwire.Tests.Redaction;

/// <summary>Cases beyond the shared corpus; every expected string was checked against the server.</summary>
public sealed class RedactorTest
{
    private static readonly Redactor R = Redactor.Default;

    // Fixtures are split so no secret scanner sees a whole one in source.
    private const string AwsKey = "AKIA" + "IOSFODNN7EXAMPLE";
    private const string KeyBegin = "-----BEGIN " + "RSA PRIVATE KEY-----";
    private const string KeyEnd = "-----END " + "RSA PRIVATE KEY-----";

    private static string Mask(string s) => R.Mask(s).Text!;

    private static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    [Fact]
    public void WordBoundariesAreAscii()
    {
        // Other scripts are neither word characters nor digits.
        Assert.Equal("\u00e9[REDACTED:aws_access_key]\u00e9 _" + AwsKey, Mask("\u00e9" + AwsKey + "\u00e9 _" + AwsKey));
        Assert.Equal("\u00e9[REDACTED:email]\u00e9", Mask("\u00e9ada@example.com\u00e9"));
        Assert.Equal("card \u00e9[REDACTED:credit_card]\u00e9", Mask("card \u00e94111111111111111\u00e9"));
        string arabicDigits = "card " + Repeat("\u0664", 16);
        Assert.Equal(arabicDigits, Mask(arabicDigits));
    }

    [Fact]
    public void CaseIsIgnoredLikeTheServer()
    {
        // The server's case folding takes the Kelvin sign for "k" and the long s for "s".
        Assert.Equal("to\u212Aen=[REDACTED:secret_assignment]", Mask("to\u212Aen=abcdefgh"));
        Assert.Equal("x\u017Fecret=[REDACTED:secret_assignment] token", Mask("x\u017Fecret=abcdefgh token"));
        Assert.Equal("bearer [REDACTED:http_auth]", Mask("bearer \u017F\u212Aabcdefghijkl1"));
        Assert.Equal("ba\u017Fic [REDACTED:http_auth] bearer", Mask("ba\u017Fic abcdefghijkl1 bearer"));
        Assert.Equal(
            "Account\u212Aey=[REDACTED:azure_storage_key]",
            Mask("Account\u212Aey=" + new string('a', 86) + "=="));

        // A name needs no word boundary before it, so the long s starts one anywhere.
        Assert.Equal("  \u017Fecret=[REDACTED:secret_assignment] token", Mask("  \u017Fecret=abcdefgh token"));

        // The pattern does not take U+0130 for "i".
        Assert.Equal("ap\u0130key access_key=[REDACTED:secret_assignment]", Mask("ap\u0130key access_key=abcdefgh"));
        Assert.Equal("ap\u0130_key=abcdefgh token", Mask("ap\u0130_key=abcdefgh token"));
    }

    [Fact]
    public void NamesMayEndLongerOnes()
    {
        Assert.Equal("access_key=[REDACTED:secret_assignment]", Mask("access_key=abcdefgh"));
        Assert.Equal("client_secret=[REDACTED:secret_assignment]", Mask("client_secret=abcdefgh"));
        Assert.Equal("csrfToken: [REDACTED:secret_assignment]", Mask("csrfToken: abcdefgh"));
        Assert.Equal("PHPSESSID=[REDACTED:secret_assignment]", Mask("PHPSESSID=abcdef123"));
        Assert.Equal("X-Amz-Signature=[REDACTED:secret_assignment]", Mask("X-Amz-Signature=0123456789abcdef"));

        // An OAuth code only in a query or fragment; counts, exit codes and longer words stay.
        Assert.Equal("https://x.example/cb?code=[REDACTED:secret_assignment]&state=1", Mask("https://x.example/cb?code=abcdefgh&state=1"));
        foreach (string s in new[] { "code=abcdefgh", "exit code=123456", "secretary=abcdefgh", "tokenizer=abcdefgh", "token_count=123456" })
        {
            Assert.Equal(s, Mask(s));
        }
    }

    [Fact]
    public void SecretValuesCountCodePoints()
    {
        // Six code points, not six UTF-16 units.
        Assert.Equal("token=\U0001F600\U0001F600\U0001F600", Mask("token=\U0001F600\U0001F600\U0001F600"));
        Assert.Equal("token=[REDACTED:secret_assignment]", Mask("token=" + Repeat("\U0001F600", 6)));

        // When a value is too short, the next match may start inside it.
        Assert.Equal(
            "token=\U0001F600\U0001F600pwd\"=[REDACTED:secret_assignment]",
            Mask("token=\U0001F600\U0001F600pwd\"=abcdefgh"));
    }

    [Fact]
    public void LowerCaseIsPerCodePoint()
    {
        Assert.Equal("istanbul k", Detectors.LowerCase("\u0130STANBUL \u212A"));
        Assert.Equal("\u00e0b\u03c9", Detectors.LowerCase("\u00c0B\u2126"));
        Assert.Equal("\U00010428x", Detectors.LowerCase("\U00010400X"));
        Assert.Equal("xapikeyname", Redactor.NormalizeKey("X-Api_Key Name"));
    }

    [Fact]
    public void SlackTokenStopsBeforeATrailingDash()
    {
        string first = "xox" + "b-123456789-";
        string second = "xox" + "b-1234567890";
        Assert.Equal(first + "  [REDACTED:slack_token]- x", Mask(first + "  " + second + "- x"));
    }

    [Fact]
    public void FindingsAreLeftmostFirstAndEarlierDetectorsWin()
    {
        MaskResult m = R.Mask("ada@example.com " + AwsKey);
        Assert.Equal("[REDACTED:email] [REDACTED:aws_access_key]", m.Text);
        Assert.Equal(new[] { "email", "aws_access_key" }, m.Findings);
        Assert.Equal("token=[REDACTED:secret_assignment]", Mask("token=ada@example.com"));
        Assert.Equal("pwd = \"[REDACTED:secret_assignment]\"", Mask("pwd = \"hunter22\""));
    }

    [Fact]
    public void PrivateKeysAndUrlCredentials()
    {
        Assert.Equal(
            "a [REDACTED:private_key] b " + KeyEnd,
            Mask("a " + KeyBegin + "\nMII\n" + KeyEnd + " b " + KeyEnd));
        string noType = "-----BEGIN " + " PRIVATE KEY-----x-----END PRIVATE KEY-----";
        Assert.Equal(noType, Mask(noType));
        Assert.Equal(
            "x://u:[REDACTED:url_credentials]@h://v:[REDACTED:url_credentials]@z 1a://u:p@h",
            Mask("x://u:p:q@h://v:w@z 1a://u:p@h"));
        Assert.Equal("postgres://u:[Filtered]@h", Mask("postgres://u:[Filtered]@h"));
    }

    [Fact]
    public void MaskNullAndEmpty()
    {
        MaskResult m = R.Mask(null);
        Assert.Null(m.Text);
        Assert.Empty(m.Findings);
        Assert.Equal(string.Empty, R.Mask(string.Empty).Text);
        Assert.Same("nothing here", R.Mask("nothing here").Text);
    }

    [Fact]
    public void RenamedKeysAreNumberedInCodePointOrder()
    {
        // UTF-16 order would put the emoji (past U+FFFF) before U+FF01.
        var doc = new Dictionary<string, object?>
        {
            ["http://u:\U0001F600@h"] = "emoji",
            ["http://u:\uFF01@h"] = "fullwidth",
        };
        int count = 0;
        Assert.Same(doc, R.Walk(doc, ref count));
        Assert.Equal(2, count);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["http://u:[REDACTED:url_credentials]@h"] = "fullwidth",
                ["http://u:[REDACTED:url_credentials]@h (2)"] = "emoji",
            },
            doc);
    }

    [Fact]
    public void WalkFiltersSensitiveValues()
    {
        var typed = new Dictionary<string, object?> { ["value"] = "hunter2" };
        var headers = new List<object?> { new List<object?> { "Cookie", "a=b" } };
        var doc = new Dictionary<string, object?>
        {
            ["password"] = typed,
            ["X-Api-Key"] = new List<object?> { 1, 2 },
            ["max_tokens"] = 512,
            ["session_id"] = string.Empty,
            ["headers"] = headers,
        };
        int count = 0;
        R.Walk(doc, ref count);
        Assert.Equal(3, count);
        Assert.Same(typed, doc["password"]);
        Assert.Equal(new Dictionary<string, object?> { ["value"] = "[Filtered]", ["type"] = "string" }, typed);
        Assert.Equal("[Filtered]", doc["X-Api-Key"]);
        Assert.Equal(512, doc["max_tokens"]);
        Assert.Equal(string.Empty, doc["session_id"]);
        Assert.Equal(new List<object?> { "Cookie", "[Filtered]" }, (List<object?>)headers[0]!);
    }

    [Fact]
    public void WalkFiltersOnceButPairsAlways()
    {
        var doc = new Dictionary<string, object?>
        {
            ["password"] = "[Filtered]",
            ["h"] = new List<object?> { new List<object?> { "Cookie", "[Filtered]" } },
            ["t"] = new Dictionary<string, object?> { ["token"] = new Dictionary<string, object?> { ["type"] = "int", ["value"] = "[Filtered]" } },
            ["secret"] = new Dictionary<string, object?> { ["value"] = null, ["type"] = "x" },
            ["apikey"] = new Dictionary<string, object?> { ["value"] = 5 },
        };
        int count = 0;
        R.Walk(doc, ref count);
        Assert.Equal(3, count);
        Assert.Equal("[Filtered]", doc["secret"]);
        Assert.Equal(new Dictionary<string, object?> { ["value"] = "[Filtered]", ["type"] = "string" }, doc["apikey"]);
        Assert.Equal("int", ((Dictionary<string, object?>)((Dictionary<string, object?>)doc["t"]!)["token"]!)["type"]);
    }

    [Fact]
    public void KeysAreComparedLikeTheServer()
    {
        var doc = new Dictionary<string, object?>
        {
            ["to\u212Aen"] = "x",
            ["credent\u0130al"] = "y",
            ["Pa\u017F\u017Fword"] = "z",
            ["ACCESS-KEY"] = "w",
            ["max_tokens"] = "abc",
        };
        int count = 0;
        R.Walk(doc, ref count);
        Assert.Equal(3, count);
        Assert.Equal("[Filtered]", doc["to\u212Aen"]);
        Assert.Equal("[Filtered]", doc["credent\u0130al"]);
        Assert.Equal("z", doc["Pa\u017F\u017Fword"]);
        Assert.Equal("[Filtered]", doc["ACCESS-KEY"]);
        Assert.Equal("abc", doc["max_tokens"]);
    }

    [Fact]
    public void WalkChangesInPlaceOrCopiesWhatIsReadOnly()
    {
        var array = new object?[] { "ada@example.com", 1 };
        var readOnlyMap = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?> { ["mail"] = "ada@example.com" });
        var readOnlyList = new ReadOnlyCollection<object?>(new List<object?> { "ada@example.com" });
        var untouched = new ReadOnlyCollection<object?>(new List<object?> { "nothing here", 1 });
        var doc = new Dictionary<string, object?>
        {
            ["array"] = array,
            ["readOnlyMap"] = readOnlyMap,
            ["readOnlyList"] = readOnlyList,
            ["untouched"] = untouched,
        };
        int count = 0;
        Assert.Same(doc, R.Walk(doc, ref count));
        Assert.Equal(3, count);
        Assert.Same(array, doc["array"]);
        Assert.Equal("[REDACTED:email]", array[0]);
        Assert.Equal(new Dictionary<string, object?> { ["mail"] = "[REDACTED:email]" }, doc["readOnlyMap"]);
        Assert.Equal("ada@example.com", readOnlyMap["mail"]);
        Assert.Equal(new List<object?> { "[REDACTED:email]" }, doc["readOnlyList"]);
        Assert.Same(untouched, doc["untouched"]);
    }

    [Fact]
    public void WalkMasksWhatTheJsonWriterWrites()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var id = Guid.NewGuid();
        var unchanged = new List<string> { "nothing here" };
        var doc = new Dictionary<string, object?>
        {
            ["uri"] = new Uri("https://u:" + "pw123456@example.com/"),
            ["text"] = new StringBuilder("mail ada@example.com"),
            ["strings"] = new List<string> { "ada@example.com", "x" },
            ["tags"] = new Dictionary<string, string> { ["password"] = "hunter2", ["user"] = "ada@example.com" },
            ["byId"] = new Hashtable { [7] = "ada@example.com" },
            ["pair"] = new[] { "Cookie", "a=b" },
            ["bytes"] = bytes,
            ["id"] = id,
            ["unchanged"] = unchanged,
            ["card"] = 4111111111111111L,
            ["n"] = 1.5,
            ["ok"] = true,
        };
        int count = 0;
        R.Walk(doc, ref count);
        Assert.Equal(7, count);
        Assert.Equal("https://u:[REDACTED:url_credentials]@example.com/", doc["uri"]);
        Assert.Equal("mail [REDACTED:email]", doc["text"]);
        Assert.Equal(new List<object?> { "[REDACTED:email]", "x" }, doc["strings"]);
        Assert.Equal(new Dictionary<string, object?> { ["password"] = "[Filtered]", ["user"] = "[REDACTED:email]" }, doc["tags"]);
        Assert.Equal(new Dictionary<string, object?> { ["7"] = "[REDACTED:email]" }, doc["byId"]);
        Assert.Equal(new List<object?> { "Cookie", "[Filtered]" }, doc["pair"]);
        Assert.Same(bytes, doc["bytes"]);
        Assert.Equal(id, doc["id"]);
        Assert.Same(unchanged, doc["unchanged"]);
        Assert.Equal(4111111111111111L, doc["card"]);
        Assert.Equal(1.5, doc["n"]);
        Assert.True(doc["ok"] is true);
    }

    [Fact]
    public void WalkStopsAtJsonDepth()
    {
        static object? Nest(int depth, object? leaf) => depth == 0 ? leaf : new List<object?> { Nest(depth - 1, leaf) };

        // The writer writes lists up to depth 64 (the root is 0), and so the strings in them.
        int count = 0;
        object? deepest = Nest(65, "ada@example.com");
        R.Walk(deepest, ref count);
        Assert.Equal(1, count);

        // A list deeper than that is written as null.
        count = 0;
        object? tooDeep = Nest(66, "ada@example.com");
        R.Walk(tooDeep, ref count);
        Assert.Equal(0, count);

        var self = new Dictionary<string, object?>();
        self["self"] = self;
        self["email"] = "ada@example.com";
        count = 0;
        R.Walk(self, ref count);
        Assert.Equal("[REDACTED:email]", self["email"]);
        Assert.Equal(1, count);
    }

    [Fact]
    public void SensitiveKeysReplaceTheDefaults()
    {
        Assert.Same(Redactor.Default, Redactor.Create(null));
        Redactor r = Redactor.Create(new[] { "Internal-ID" });
        var doc = new Dictionary<string, object?> { ["internal_id"] = "x", ["password"] = "y", ["Auth"] = "z" };
        int count = 0;
        r.Walk(doc, ref count);
        Assert.Equal(new Dictionary<string, object?> { ["internal_id"] = "[Filtered]", ["password"] = "y", ["Auth"] = "[Filtered]" }, doc);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Ipv4IsOffByDefault()
    {
        Assert.Equal("host 10.0.0.1", Mask("host 10.0.0.1"));
        var r = new Redactor(new[] { "ipv4" }, null);
        Assert.Equal("host [REDACTED:ipv4]", r.Mask("host 10.0.0.1").Text);
        Assert.Throws<ArgumentException>(() => new Redactor(new[] { "nope" }, null));
    }

    [Fact]
    public void SafeForConcurrentUse()
    {
        string input = "token=abcdefgh ada@example.com " + AwsKey + " card 4111111111111111";
        string want = Mask(input);
        var got = new string?[64];
        Parallel.For(0, got.Length, i => got[i] = R.Mask(input).Text);
        Assert.All(got, g => Assert.Equal(want, g));
    }

    /// <summary>Text a backtracking engine would take quadratic time on, and other worst cases.</summary>
    private static readonly Dictionary<string, string> Hostile = new()
    {
        ["log lines"] = Repeat("GET /api/v1/users/12345 took 87 ms; status=200 at com.example.Service.handle(Service.java:42) https://example.com/docs?x=1 basic info, token count 3 + 1. ", 700),
        ["schemes"] = Repeat("a.", 50_000) + "://",
        ["BEGIN lines"] = Repeat(KeyBegin + "\n", 3000),
        ["at signs"] = Repeat("a@", 50_000),
        ["digits"] = Repeat("1 ", 50_000),
        ["short secrets"] = Repeat("token=\U0001F600\U0001F600 ", 10_000),
        ["long slack runs"] = Repeat("xox" + "b-" + new string('a', 300) + " ", 300),
        ["bearer words"] = Repeat("bearer ", 15_000),
        ["jwt headers"] = Repeat("eyJ-", 50_000),
        ["jwt payloads"] = "eyJabcdefgh." + Repeat("eyJ-", 50_000),
        ["a name before spaces"] = "token" + new string(' ', 100_000),
        ["a name before spaces and no value"] = "password\"" + new string(' ', 100_000) + "= ",
        ["names before spaces"] = Repeat("secret_key  ", 20_000),
        ["session ids"] = Repeat("sessid", 20_000),
        ["code queries"] = Repeat("?code", 20_000),
        ["signatures without values"] = Repeat("signature: ,", 20_000),
    };

    public static TheoryData<string> HostileNames => new(Hostile.Keys);

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void HostileTextIsFast(string name)
    {
        string input = Hostile[name];
        R.Mask("warm up");
        for (int round = 0; round < 3; round++)
        {
            var sw = Stopwatch.StartNew();
            MaskResult m = R.Mask(input);
            sw.Stop();
            Assert.Empty(m.Findings);
            Assert.True(sw.ElapsedMilliseconds < 500, name + " took " + sw.ElapsedMilliseconds + " ms");
        }
    }

    [Fact]
    public void ManyFindingsAreFast()
    {
        // Each finding is checked against its neighbours only, not against every one before it.
        string input = Repeat("ada@example.com 4111111111111111 ", 25_000);
        var sw = Stopwatch.StartNew();
        MaskResult m = R.Mask(input);
        sw.Stop();
        Assert.Equal(50_000, m.Findings.Count);
        Assert.Equal(Repeat("[REDACTED:email] [REDACTED:credit_card] ", 25_000), m.Text);
        Assert.True(sw.ElapsedMilliseconds < 1000, "took " + sw.ElapsedMilliseconds + " ms");
    }

    [Fact]
    public void ManyKeysMaskingAlikeAreFast()
    {
        // Each masked key's numbering resumes where it stopped, not counted up from 2 again.
        var doc = new Dictionary<string, object?>();
        for (int i = 0; i < 20_000; i++)
        {
            doc["user" + i + "@example.com"] = i;
        }

        int count = 0;
        var sw = Stopwatch.StartNew();
        R.Walk(doc, ref count);
        sw.Stop();
        Assert.Equal(20_000, count);
        Assert.Equal(20_000, doc.Count);
        Assert.True(doc.ContainsKey("[REDACTED:email]") && doc.ContainsKey("[REDACTED:email] (20000)"));
        Assert.True(sw.ElapsedMilliseconds < 1000, "took " + sw.ElapsedMilliseconds + " ms");
    }

    [Fact]
    public void WhatRedactionFailsOnIsFiltered()
    {
        // A detector whose pattern times out (no real one does): the text goes as [Filtered], never unmasked.
        var r = new Redactor(new[] { "email" }, null);
        var slow = Detector.Pattern("slow", false, Array.Empty<string>(), "(x+x+)+y");
        typeof(Redactor).GetField("detectors", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(r, new[] { slow });
        string hostile = new string('x', 40) + " ada@example.com";
        Assert.Equal(Redactor.Filtered, r.Mask(hostile).Text);
        var doc = new Dictionary<string, object?> { ["note"] = hostile, ["ok"] = "fine" };
        int count = 0;
        r.Walk(doc, ref count);
        Assert.Equal(Redactor.Filtered, doc["note"]);
        Assert.Equal(1, count);
    }

    [Fact]
    public void JwtScannerFindsWhatThePatternFinds()
    {
        // The server's pattern, as a backtracking engine runs it (quadratic on "eyJ-eyJ-…").
        var pattern = new Regex(@"(?<![0-9A-Za-z_])eyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}");
        var jwt = new Redactor(new[] { "jwt" }, null);
        string[] pieces = { "eyJ", "eyJ", "abcdefgh", "a1", "-", "_", ".", " ", "x", "é" };
        var random = new Random(7);
        for (int i = 0; i < 5000; i++)
        {
            var b = new StringBuilder();
            for (int n = random.Next(1, 40); n > 0; n--)
            {
                b.Append(pieces[random.Next(pieces.Length)]);
            }

            string s = b.ToString();
            var want = pattern.Matches(s).Select(x => (x.Index, x.Index + x.Length));
            var got = jwt.Find(s).Select(f => (f.Start, f.End));
            Assert.True(want.SequenceEqual(got), s);
        }
    }
}
