namespace Fixwire;

/// <summary>
/// An outgoing HTTP request, for HTTP client integrations: a client span under the current span
/// when it is sampled, trace headers when the URL is one of the trace propagation targets, and an
/// <c>http</c> breadcrumb when it ends. Nothing here throws into the caller's request.
/// </summary>
public sealed class OutgoingRequest
{
    private readonly Hub _hub;
    private readonly string _method;
    private readonly string _url;
    private readonly Span? _span;
    private int _ended;

    private OutgoingRequest(Hub hub, string method, string url, Span? span)
    {
        _hub = hub;
        _method = method;
        _url = url;
        _span = span;
    }

    /// <summary>Starts tracking a request.</summary>
    /// <param name="hub">The hub of the work the request is for.</param>
    /// <param name="method">Such as <c>GET</c>.</param>
    /// <param name="url">The request's URL.</param>
    /// <param name="setHeader">Adds a trace header to the request, before it is sent.</param>
    public static OutgoingRequest Start(Hub hub, string method, string url, Action<string, string> setHeader)
    {
        var plain = WithoutQuery(url);
        var m = (method ?? "GET").ToUpperInvariant();
        Span? span = null;
        try
        {
            var parent = Span.Current ?? hub.Scope.Span;
            if (parent is { Sampled: true })
            {
                span = hub.SpanBuilder(m + " " + plain)
                    .WithOp("http.client")
                    .WithKind(SpanKind.Client)
                    .WithAttribute("http.request.method", m)
                    .WithAttribute("url.full", plain)
                    .WithAttribute("server.address", Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : null)
                    .StartDetached();
            }
            var from = span ?? parent;
            if (from != null && hub.Client is { } client && client.ShouldPropagate(url))
            {
                setHeader("traceparent", from.Traceparent);
                if (from.Tracestate != null)
                {
                    setHeader("tracestate", from.Tracestate);
                }
                if (from.Baggage != null)
                {
                    setHeader("baggage", from.Baggage);
                }
            }
        }
#pragma warning disable CA1031 // tracing must never break the request
        catch (Exception)
        {
        }
#pragma warning restore CA1031
        return new OutgoingRequest(hub, m, plain, span);
    }

    /// <summary>Ends the request with the server's answer; a 4xx or 5xx fails the span.</summary>
    /// <param name="status">The response's status code.</param>
    public void End(int status) => Finish(status, null);

    /// <summary>Ends a request that got no answer.</summary>
    /// <param name="exception">What went wrong (a timeout, a refused connection, …).</param>
    public void Fail(Exception exception) => Finish(0, exception);

    private void Finish(int status, Exception? exception)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1)
        {
            return;
        }
        try
        {
            if (_span != null)
            {
                if (status > 0)
                {
                    _span.SetAttribute("http.response.status_code", status);
                }
                if (exception != null)
                {
                    _span.SetError(exception);
                }
                else if (status >= 400)
                {
                    _span.SetError("HTTP " + status.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                _span.Finish();
            }
            var b = new Breadcrumb("http", null)
            {
                Type = "http",
                Level = exception != null || status >= 500 ? Level.Error : Level.Info,
            };
            b.Data["method"] = _method;
            b.Data["url"] = _url;
            if (status > 0)
            {
                b.Data["status_code"] = status;
            }
            _hub.AddBreadcrumb(b);
        }
#pragma warning disable CA1031 // tracing must never break the request
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private static string WithoutQuery(string url)
    {
        var cut = url.IndexOfAny(['?', '#']);
        return cut < 0 ? url : url.Substring(0, cut);
    }
}

/// <summary>
/// Times <see cref="HttpClient"/> requests as client spans of the current trace, sends trace headers
/// to the trace propagation targets, and leaves an <c>http</c> breadcrumb for each.
/// <code>
/// var http = new HttpClient(new FixwireHttpMessageHandler(new HttpClientHandler()));
/// </code>
/// With <c>IHttpClientFactory</c>, <c>Fixwire.AspNetCore</c> adds it to every client.
/// </summary>
public sealed class FixwireHttpMessageHandler : DelegatingHandler
{
    /// <summary>A handler for <c>IHttpClientFactory</c>, which sets the inner handler.</summary>
    public FixwireHttpMessageHandler() { }

    /// <summary>A handler around another.</summary>
    /// <param name="inner">The handler that sends.</param>
    public FixwireHttpMessageHandler(HttpMessageHandler inner)
        : base(inner) { }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? "";
        var outgoing = OutgoingRequest.Start(
            Hub.Current,
            request.Method.Method,
            url,
            (name, value) =>
            {
                if (!request.Headers.Contains(name))
                {
                    request.Headers.TryAddWithoutValidation(name, value); // the app's own trace headers win
                }
            });
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            outgoing.End((int)response.StatusCode);
            return response;
        }
        catch (Exception e)
        {
            outgoing.Fail(e);
            throw;
        }
    }
}
