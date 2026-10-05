using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Fixwire.AspNetCore;

/// <summary>
/// Each request: its own hub (its scope holds the request), exceptions that escape the app as
/// crashes, a session for release health, and a server span that continues the caller's trace,
/// named after the route the endpoint matched.
/// </summary>
internal sealed class FixwireMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var hub = Hub.Current.Clone();
        using var bound = hub.Bind();
        var pii = hub.Client?.Options.SendDefaultPii == true;
        hub.Scope.Request = RequestOf(context, pii);
        var request = context.Request;
        using var span = hub.SpanBuilder(request.Method)
            .WithOp("http.server")
            .ContinueTrace(request.Headers["traceparent"].FirstOrDefault(), request.Headers["tracestate"].FirstOrDefault(), request.Headers["baggage"].FirstOrDefault())
            .WithAttribute("http.request.method", request.Method)
            .WithAttribute("url.path", request.Path.Value)
            .WithAttribute("url.scheme", request.Scheme)
            .WithAttribute("server.address", request.Host.Host)
            .WithAttribute("user_agent.original", request.Headers.UserAgent.FirstOrDefault())
            .Start();
        using var session = hub.StartRequestSession();
        var status = 0;
        try
        {
            await next(context).ConfigureAwait(false);
            status = context.Response.StatusCode;
        }
        catch (Exception e)
        {
            if (hub.Client is not { } client || !client.IsCaptured(e))
            {
                hub.CaptureException(e, "aspnetcore", handled: false);
            }
            span.SetError(e);
            status = 500;
            throw;
        }
        finally
        {
            if (Route(context) is { } route)
            {
                span.Name = request.Method + " " + route;
                span.SetAttribute("http.route", route);
            }
            span.SetAttribute("http.response.status_code", status);
            if (status >= 500)
            {
                span.SetError("HTTP " + status.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>The route the endpoint matched, such as <c>/items/{id}</c>.</summary>
    internal static string? Route(HttpContext context)
    {
        // UseExceptionHandler clears the endpoint but keeps the one that failed.
        var endpoint = context.GetEndpoint() ?? context.Features.Get<IExceptionHandlerFeature>()?.Endpoint;
        if (endpoint is not RouteEndpoint { RoutePattern.RawText: { } raw })
        {
            return null;
        }
        return raw.StartsWith('/') ? raw : "/" + raw;
    }

    private static Request RequestOf(HttpContext context, bool pii)
    {
        var r = context.Request;
        var request = new Request
        {
            Method = r.Method,
            Url = r.Scheme + "://" + r.Host.Value + r.PathBase.Value + r.Path.Value,
            Query = r.QueryString.HasValue ? r.QueryString.Value!.TrimStart('?') : null,
            RouteProvider = () => Route(context),
        };
        foreach (var h in r.Headers)
        {
            if (pii || !Request.IsSensitiveHeader(h.Key))
            {
                request.Headers[h.Key] = h.Value.ToString();
            }
        }
        if (pii)
        {
            var forwarded = r.Headers["X-Forwarded-For"].FirstOrDefault();
            request.ClientAddress = !string.IsNullOrEmpty(forwarded)
                ? forwarded.Split(',')[0].Trim()
                : context.Connection.RemoteIpAddress?.ToString();
        }
        return request;
    }
}

/// <summary>Puts the middleware first, before the app's own.</summary>
internal sealed class FixwireStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<FixwireMiddleware>();
        next(app);
    };
}

/// <summary>
/// Sees exceptions <c>UseExceptionHandler</c> catches before the middleware could: reports each as
/// a crash and lets the app's own handling go on.
/// </summary>
internal sealed class FixwireExceptionHandler : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        Report(exception);
        return ValueTask.FromResult(false);
    }

    internal static void Report(Exception exception)
    {
        var hub = Hub.Current;
        if (hub.Client is { } client && !client.IsCaptured(exception))
        {
            hub.CaptureException(exception, "aspnetcore", handled: false);
        }
    }
}

/// <summary>Sees exceptions the developer exception page catches, in development.</summary>
internal sealed class FixwireDeveloperPageExceptionFilter : IDeveloperPageExceptionFilter
{
    public Task HandleExceptionAsync(ErrorContext errorContext, Func<ErrorContext, Task> next)
    {
        FixwireExceptionHandler.Report(errorContext.Exception);
        return next(errorContext);
    }
}
