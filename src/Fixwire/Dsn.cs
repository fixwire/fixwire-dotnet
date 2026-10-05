namespace Fixwire;

/// <summary>Where the SDK sends, and with which key: <c>https://&lt;key&gt;@&lt;host&gt;</c>.</summary>
public sealed class Dsn
{
    private Dsn(string key, string baseUrl)
    {
        Key = key;
        BaseUrl = baseUrl;
    }

    /// <summary>The project's publishable key.</summary>
    public string Key { get; }

    /// <summary>The DSN without the key; the endpoints are relative to it.</summary>
    public string BaseUrl { get; }

    /// <summary>Reads a DSN.</summary>
    /// <param name="dsn">The project's DSN.</param>
    /// <exception cref="ArgumentException">It has no scheme, host or key.</exception>
    public static Dsn Parse(string? dsn)
    {
        if (!Uri.TryCreate((dsn ?? "").Trim(), UriKind.Absolute, out var u)
            || (u.Scheme != "https" && u.Scheme != "http")
            || string.IsNullOrEmpty(u.Host))
        {
            throw Invalid();
        }
        var key = u.UserInfo;
        var colon = key.IndexOf(':');
        if (colon >= 0)
        {
            key = key.Substring(0, colon);
        }
        if (key.Length == 0)
        {
            throw Invalid();
        }
        var path = u.AbsolutePath.TrimEnd('/');
        var port = u.IsDefaultPort ? "" : ":" + u.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new Dsn(key, u.Scheme + "://" + u.Host + port + path);
    }

    /// <summary>The address of an endpoint, such as <c>/v1/logs</c>.</summary>
    /// <param name="path">The endpoint's path.</param>
    public Uri Url(string path) => new(BaseUrl + path);

    private static ArgumentException Invalid() =>
        new("fixwire: the DSN must look like https://<key>@<host>");
}
