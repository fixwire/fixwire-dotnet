using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Fixwire;

/// <summary>The session of the request a scope serves.</summary>
internal sealed class RequestSession
{
    private readonly object _lock = new();
    private string _status = "ok";

    public string Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    public void Mark(bool crashed)
    {
        lock (_lock)
        {
            if (crashed)
            {
                _status = "crashed";
            }
            else if (_status == "ok")
            {
                _status = "errored";
            }
        }
    }
}

/// <summary>
/// Release health for servers: each request is a session, counted per minute and user and sent
/// about every minute (sdks/PROTOCOL.md §5).
/// </summary>
internal sealed class Sessions : IDisposable
{
    /// <summary>The (minute, user) counts kept between sends; past it, requests count without their user.</summary>
    internal const int MaxBuckets = 5000;

    private readonly Client _client;
    private readonly object _lock = new();
    private readonly Timer _timer;
    private Dictionary<(long Minute, string? Did), int[]> _buckets = new(); // exited, errored, crashed

    public Sessions(Client client, TimeSpan interval)
    {
        _client = client;
        _timer = new Timer(_ => SendQuietly(), null, interval, interval);
    }

    /// <summary>Counts a request that ended.</summary>
    public void Record(string status, string? did, DateTimeOffset at)
    {
        var ms = at.ToUnixTimeMilliseconds();
        var key = (ms - (ms % 60_000), did);
        lock (_lock)
        {
            if (!_buckets.TryGetValue(key, out var counts))
            {
                if (_buckets.Count >= MaxBuckets)
                {
                    key = (key.Item1, null); // many users at once: counted without theirs, so the body stays under 1 MB
                }
                if (!_buckets.TryGetValue(key, out counts))
                {
                    counts = new int[3];
                    _buckets[key] = counts;
                }
            }
            counts[status == "crashed" ? 2 : status == "errored" ? 1 : 0]++;
        }
    }

    /// <summary>Sends what was counted.</summary>
    public void Send()
    {
        Dictionary<(long Minute, string? Did), int[]> taken;
        lock (_lock)
        {
            if (_buckets.Count == 0)
            {
                return;
            }
            taken = _buckets;
            _buckets = new();
        }
        var aggregates = new List<object?>();
        foreach (var kv in taken)
        {
            var a = new Dictionary<string, object?>
            {
                ["started"] = DateTimeOffset.FromUnixTimeMilliseconds(kv.Key.Minute).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            };
            if (kv.Key.Did != null)
            {
                a["did"] = kv.Key.Did;
            }
            a["exited"] = kv.Value[0];
            a["errored"] = kv.Value[1];
            a["crashed"] = kv.Value[2];
            aggregates.Add(a);
        }
        _client.SendJson("/v1/sessions", Transport.Session, new Dictionary<string, object?>
        {
            ["sdk"] = Client.Sdk(),
            ["release"] = _client.Options.Release,
            ["environment"] = _client.Options.Environment,
            ["aggregates"] = aggregates,
        });
    }

    /// <summary>Sends, from the timer.</summary>
    private void SendQuietly()
    {
        try
        {
            Send();
        }
#pragma warning disable CA1031 // an exception in a timer's callback ends the process
        catch (Exception e)
        {
            _client.Transport?.Log("sending sessions: " + e.Message);
        }
#pragma warning restore CA1031
    }

    public void Dispose() => _timer.Dispose();

    /// <summary>
    /// The user, hashed on the device: the first 16 bytes of the SHA-256 of their id (else email,
    /// else username), as hex. Never the raw id.
    /// </summary>
    public static string? DeviceId(User? u)
    {
        var id = u == null ? null : !FixwireOptions.Empty(u.Id) ? u.Id : !FixwireOptions.Empty(u.Email) ? u.Email : u.Username;
        if (FixwireOptions.Empty(id))
        {
            return null;
        }
        byte[] sum;
        using (var sha = SHA256.Create())
        {
            sum = sha.ComputeHash(Encoding.UTF8.GetBytes(id!));
        }
        var b = new StringBuilder(32);
        for (var i = 0; i < 16; i++)
        {
            b.Append(sum[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return b.ToString();
    }
}
