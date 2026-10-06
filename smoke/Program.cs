using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Fixwire;

// A free port for the fake ingest.
var probe = new TcpListener(IPAddress.Loopback, 0);
probe.Start();
int port = ((IPEndPoint)probe.LocalEndpoint).Port;
probe.Stop();

var logs = new List<string>();
using var ingest = new HttpListener();
ingest.Prefixes.Add($"http://localhost:{port}/");
ingest.Start();
var serving = Task.Run(async () =>
{
    while (ingest.IsListening)
    {
        HttpListenerContext context;
        try
        {
            context = await ingest.GetContextAsync();
        }
        catch (HttpListenerException)
        {
            return; // stopped
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        Stream body = context.Request.InputStream;
        if (context.Request.Headers["Content-Encoding"] == "gzip")
        {
            body = new GZipStream(body, CompressionMode.Decompress);
        }
        using (var reader = new StreamReader(body, Encoding.UTF8))
        {
            string text = await reader.ReadToEndAsync();
            if (context.Request.Url?.AbsolutePath == "/v1/logs")
            {
                lock (logs)
                {
                    logs.Add(text);
                }
            }
        }
        byte[] ok = Encoding.UTF8.GetBytes("{}");
        context.Response.StatusCode = 200;
        await context.Response.OutputStream.WriteAsync(ok, 0, ok.Length);
        context.Response.Close();
    }
});

bool flushed;
using (FixwireSdk.Init(o =>
{
    o.Dsn = $"http://smoke-key@localhost:{port}";
    o.Release = "smoke@1.0.0";
}))
{
    FixwireSdk.CaptureException(new InvalidOperationException("card 4111 1111 1111 1111 was declined"));
    flushed = await FixwireSdk.FlushAsync(TimeSpan.FromSeconds(5));
}
ingest.Stop();

string runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
string all;
lock (logs)
{
    all = string.Join("\n", logs);
}
var failures = new List<string>();
if (!flushed)
{
    failures.Add("the flush timed out");
}
if (!all.Contains("was declined"))
{
    failures.Add("no error reached /v1/logs");
}
if (all.Contains("4111 1111 1111 1111"))
{
    failures.Add("the card number wasn't masked");
}
if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAIL on {runtime}: {string.Join("; ", failures)}");
    return 1;
}
Console.WriteLine($"ok   Fixwire on {runtime}: an error reached the ingest, masked");
return 0;
