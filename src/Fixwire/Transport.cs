using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;

namespace Fixwire;

/// <summary>
/// Sends requests one at a time from a bounded queue on a background loop, and backs off where
/// Fixwire says to (<c>Fixwire-Rate-Limits</c>, <c>Retry-After</c>). Never blocks the caller.
/// </summary>
internal sealed class Transport : IDisposable
{
    // The kinds of data, as the protocol's rate limits name them.
    public const string Error = "error";
    public const string Span = "span";
    public const string Session = "session";
    public const string CheckIn = "check_in";
    public const string Feedback = "feedback";

    /// <summary>The sends of one request, and the longest a paused one waits.</summary>
    public const int MaxAttempts = 4;

    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    /// <summary>The longest pause an answer may ask for (Retry-After, Fixwire-Rate-Limits): longer ones are a day.</summary>
    private const long MaxPauseSeconds = 24 * 60 * 60;

    private static readonly TimeSpan MaxPause = TimeSpan.FromSeconds(MaxPauseSeconds);

    /// <summary>The kinds of data Fixwire-Rate-Limits may name; it names no others (and "" is all of them).</summary>
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    {
        "", Error, "log", Span, Session, CheckIn, Feedback, "file",
    };

    private static readonly char[] Colon = [':'];

    /// <summary>The first retry's wait; each next one waits twice as long (tests shorten it).</summary>
    internal static TimeSpan BackoffUnit { get; set; } = TimeSpan.FromSeconds(1);

    private sealed class Item(string path, string category, byte[] body)
    {
        public string Path { get; } = path;
        public string Category { get; } = category;
        public byte[] Body { get; } = body;
        public int Attempts { get; set; }
    }

    private readonly Dsn _dsn;
    private readonly FixwireOptions _options;
    private readonly HttpClient _http;
    private readonly ConcurrentQueue<Item> _queue = new();
    private readonly SemaphoreSlim _ready = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _paused = new(); // category ("" for all) → until
    private readonly Task _worker;
    private int _pending; // queued, being sent or waiting: what a flush waits for
    private int _retrying; // of them, waiting for a retry
    private TaskCompletionSource<bool> _idle = NewIdle(done: true);

    public Transport(Dsn dsn, FixwireOptions options)
    {
        _dsn = dsn;
        _options = options;
        // The SDK's own handler follows no redirect: the key goes to the DSN's host and nowhere else.
        _http = options.HttpMessageHandler is { } handler
            ? new HttpClient(handler, disposeHandler: false)
            : new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _http.Timeout = options.Timeout;
        using (ExecutionContext.SuppressFlow())
        {
            _worker = Task.Run(RunAsync); // without the caller's span, hub or Activity: its requests are no part of the app's work
        }
    }

    private static TaskCompletionSource<bool> NewIdle(bool done)
    {
        var t = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (done)
        {
            t.SetResult(true);
        }
        return t;
    }

    public void Log(string message)
    {
        if (_options.Debug)
        {
            Console.Error.WriteLine("fixwire: " + message);
        }
    }

    /// <summary>Queues a request; false when the queue is full (MaxQueue, retries apart) or closed.</summary>
    public bool Send(string path, string category, byte[] body)
    {
        lock (_lock)
        {
            if (_stop.IsCancellationRequested || _pending - _retrying >= _options.MaxQueue)
            {
                Log($"dropping a {category} request: {(_stop.IsCancellationRequested ? "closed" : "the queue is full")}");
                return false;
            }
            if (_pending++ == 0)
            {
                _idle = NewIdle(done: false);
            }
        }
        Enqueue(new Item(path, category, body));
        return true;
    }

    private void Enqueue(Item item)
    {
        _queue.Enqueue(item);
        try
        {
            _ready.Release();
        }
        catch (ObjectDisposedException)
        {
            Done(); // closed while it was queued
        }
    }

    private void Done()
    {
        TaskCompletionSource<bool>? idle = null;
        lock (_lock)
        {
            if (--_pending <= 0)
            {
                _pending = 0;
                idle = _idle;
            }
        }
        idle?.TrySetResult(true);
    }

    private void Later(Item item, TimeSpan wait, bool retry = false) =>
        _ = Task.Delay(wait, _stop.Token).ContinueWith(
            t =>
            {
                if (retry)
                {
                    lock (_lock)
                    {
                        _retrying--;
                    }
                }
                if (t.IsCanceled)
                {
                    Done(); // closed: dropped
                }
                else
                {
                    Enqueue(item);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task RunAsync()
    {
        while (true)
        {
            try
            {
                await _ready.WaitAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                break; // closed (and perhaps disposed of before this loop saw it)
            }
            if (!_queue.TryDequeue(out var item))
            {
                continue;
            }
            try
            {
                await DeliverAsync(item).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // the loop must go on whatever a request does
            catch (Exception e)
            {
                Log($"sending a {item.Category} request failed: {e.Message}");
                Done();
            }
#pragma warning restore CA1031
        }
        while (_queue.TryDequeue(out _))
        {
            Done();
        }
    }

    private TimeSpan PausedFor(string category, DateTimeOffset now)
    {
        lock (_lock)
        {
            var wait = TimeSpan.Zero;
            if (_paused.TryGetValue(category, out var c) && c - now > wait)
            {
                wait = c - now;
            }
            if (_paused.TryGetValue("", out var all) && all - now > wait)
            {
                wait = all - now;
            }
            return wait;
        }
    }

    private async Task DeliverAsync(Item item)
    {
        var wait = PausedFor(item.Category, DateTimeOffset.UtcNow);
        if (wait > TimeSpan.Zero)
        {
            if (wait > MaxWait)
            {
                Log($"dropping a {item.Category} request: paused for {wait.TotalSeconds:0} s");
                Done();
            }
            else
            {
                Later(item, wait);
            }
            return;
        }
        int status;
        var retryAfter = TimeSpan.Zero;
        try
        {
            (status, retryAfter) = await PostAsync(item).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            status = -1;
            Log($"sending a {item.Category} request: {e.Message}");
        }
        if (status is >= 200 and < 300)
        {
            Done();
        }
        else if (status is -1 or 429 or >= 500)
        {
            item.Attempts++;
            if (item.Attempts >= MaxAttempts)
            {
                Log($"dropping a {item.Category} request after {item.Attempts} attempts ({status})");
                Done();
                return;
            }
            var backoff = TimeSpan.FromTicks(BackoffUnit.Ticks * (1L << (item.Attempts - 1))); // about 1 s, 2 s, 4 s
            var later = backoff > retryAfter ? backoff : retryAfter;
            if (later > MaxWait)
            {
                // A request parked for hours would hold its place in the queue all that time.
                Log($"dropping a {item.Category} request: told to wait {later.TotalSeconds:0} s");
                Done();
                return;
            }
            bool full;
            lock (_lock)
            {
                full = _retrying >= _options.MaxQueue;
                if (!full)
                {
                    _retrying++;
                }
            }
            if (full)
            {
                Log($"dropping a {item.Category} request: {_options.MaxQueue} wait for a retry already");
                Done();
                return;
            }
            Later(item, later, retry: true);
        }
        else
        {
            Log($"{item.Category} request refused: {status}");
            Done();
        }
    }

    /// <summary>Sends a request, gzipped: the status and Retry-After.</summary>
    private async Task<(int Status, TimeSpan RetryAfter)> PostAsync(Item item)
    {
        using var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(item.Body, 0, item.Body.Length);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, _dsn.Url(item.Path))
        {
            Content = new ByteArrayContent(gz.ToArray()),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content.Headers.ContentEncoding.Add("gzip");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _dsn.Key);
        request.Headers.UserAgent.ParseAdd(Client.SdkName + "/" + Client.SdkVersion);
        // Only the status and headers are read: the body is never buffered, whatever its size.
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _stop.Token).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var status = (int)response.StatusCode;
        var retryAfter = RetryAfter(response.Headers, now);
        string? limits = response.Headers.TryGetValues("Fixwire-Rate-Limits", out var v) ? string.Join(",", v) : null;
        Limit(limits, now);
        if (status == 429 && limits == null)
        {
            // Everything waits, at least a minute.
            var secs = Math.Max((long)Math.Ceiling(retryAfter.TotalSeconds), 60);
            Limit(secs.ToString(CultureInfo.InvariantCulture) + ":", now);
        }
        else if (status >= 500 && retryAfter > TimeSpan.Zero)
        {
            Limit(((long)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + ":", now);
        }
        return (status, retryAfter);
    }

    /// <summary>
    /// Retry-After as a wait: seconds or an HTTP date, at most a day (more is a day); zero when
    /// absent, past or broken.
    /// </summary>
    internal static TimeSpan RetryAfter(HttpResponseHeaders headers, DateTimeOffset now)
    {
        TimeSpan wait;
        if (headers.TryGetValues("Retry-After", out var values) && Seconds(values.First(), out var secs))
        {
            wait = TimeSpan.FromSeconds(secs); // read here: .NET's parser takes no more than int's seconds
        }
        else
        {
            wait = headers.RetryAfter?.Date is { } date ? date - now : TimeSpan.Zero;
        }
        return wait <= TimeSpan.Zero ? TimeSpan.Zero : wait > MaxPause ? MaxPause : wait;
    }

    /// <summary>Whole seconds, at most a day (more digits than a long holds too); false for anything but digits.</summary>
    private static bool Seconds(string s, out long secs)
    {
        s = s.Trim();
        secs = 0;
        if (s.Length == 0)
        {
            return false;
        }
        foreach (var c in s)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        secs = long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Math.Min(n, MaxPauseSeconds) : MaxPauseSeconds;
        return true;
    }

    /// <summary>Reads <c>&lt;seconds&gt;:&lt;category;…&gt;, …</c>; no categories means all.</summary>
    internal void Limit(string? header, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(header))
        {
            return;
        }
        lock (_lock)
        {
            foreach (var part in header!.Split(','))
            {
                var sc = part.Trim().Split(Colon, 2);
                if (!Seconds(sc[0], out var secs) || secs <= 0)
                {
                    continue;
                }
                var until = now.AddSeconds(secs);
                var cats = sc.Length > 1 ? sc[1].Trim() : "";
                foreach (var cat in cats.Length == 0 ? new[] { "" } : cats.Split(';'))
                {
                    var name = cat.Trim();
                    if (!Categories.Contains(name))
                    {
                        continue; // the map stays as small as the protocol's list
                    }
                    if (!_paused.TryGetValue(name, out var was) || until > was)
                    {
                        _paused[name] = until;
                    }
                }
            }
        }
    }

    /// <summary>Waits until every queued request is sent or dropped; false on timeout.</summary>
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        Task<bool> idle;
        lock (_lock)
        {
            if (_pending == 0)
            {
                return true;
            }
            idle = _idle.Task;
        }
        var done = await Task.WhenAny(idle, Task.Delay(timeout)).ConfigureAwait(false);
        return done == idle;
    }

    public void Dispose() => Close(TimeSpan.FromSeconds(1));

    /// <summary>Stops sending, waiting at most so long for the loop to end.</summary>
    public void Close(TimeSpan wait)
    {
        _stop.Cancel();
        try
        {
            _worker.Wait(wait);
        }
        catch (AggregateException)
        {
            // stopping
        }
        _http.Dispose();
        _ready.Dispose();
        _stop.Dispose();
    }
}
