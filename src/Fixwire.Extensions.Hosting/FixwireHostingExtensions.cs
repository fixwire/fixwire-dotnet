using System.Globalization;
using Fixwire;
using Fixwire.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// In the hosting namespace, so that `builder.AddFixwire()` needs no using of its own.
namespace Microsoft.Extensions.Hosting;

/// <summary>Sets Fixwire up in a .NET host.</summary>
public static class FixwireHostingExtensions
{
    /// <summary>
    /// Sets Fixwire up from the <c>Fixwire</c> configuration section (appsettings.json, environment
    /// variables such as <c>Fixwire__Dsn</c>, …) and then <paramref name="configure"/>: the SDK starts
    /// now, so that start-up errors are caught, and is flushed when the host stops. <see cref="ILogger"/>
    /// records become breadcrumbs and events, and <c>IHttpClientFactory</c> clients are traced.
    /// <code>
    /// var builder = Host.CreateApplicationBuilder(args);
    /// builder.AddFixwire(o => o.Release = "worker@1.4.0");
    /// </code>
    /// </summary>
    /// <param name="builder">The host's builder.</param>
    /// <param name="configure">Changes the options read from configuration.</param>
    public static IHostApplicationBuilder AddFixwire(this IHostApplicationBuilder builder, Action<FixwireOptions>? configure = null)
    {
        var section = builder.Configuration.GetSection("Fixwire");
        var options = FixwireConfiguration.Read(section);
        configure?.Invoke(options);
        var logging = FixwireConfiguration.ReadLogging(section.GetSection("Logging"));
        var handle = FixwireSdk.Init(options);
        builder.Services.AddSingleton<IHostedService>(new FixwireLifetime(handle));
        builder.Services.AddSingleton<ILoggerProvider>(new FixwireLoggerProvider(logging));
        builder.Services.ConfigureHttpClientDefaults(b => b.AddHttpMessageHandler(() => new FixwireHttpMessageHandler()));
        return builder;
    }

    /// <summary>Flushes and stops the SDK when the host stops.</summary>
    private sealed class FixwireLifetime(IDisposable handle) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            handle.Dispose();
            return Task.CompletedTask;
        }
    }
}

/// <summary>Reads the options from configuration, without reflection (so trimming and AOT are safe).</summary>
internal static class FixwireConfiguration
{
    public static FixwireOptions Read(IConfiguration c)
    {
        var o = new FixwireOptions
        {
            Dsn = c["Dsn"],
            Release = c["Release"],
            Environment = c["Environment"],
            ServerName = c["ServerName"],
            ServiceName = c["ServiceName"],
        };
        o.SampleRate = Double(c["SampleRate"]) ?? o.SampleRate;
        o.TracesSampleRate = Double(c["TracesSampleRate"]) ?? o.TracesSampleRate;
        o.SendDefaultPii = Bool(c["SendDefaultPii"]) ?? o.SendDefaultPii;
        o.Redact = Bool(c["Redact"]) ?? o.Redact;
        o.Debug = Bool(c["Debug"]) ?? o.Debug;
        o.AutoSessionTracking = Bool(c["AutoSessionTracking"]) ?? o.AutoSessionTracking;
        o.CaptureUnhandledExceptions = Bool(c["CaptureUnhandledExceptions"]) ?? o.CaptureUnhandledExceptions;
        o.MaxBreadcrumbs = Int(c["MaxBreadcrumbs"]) ?? o.MaxBreadcrumbs;
        foreach (var t in List(c.GetSection("TracePropagationTargets")))
        {
            o.TracePropagationTargets.Add(t);
        }
        foreach (var p in List(c.GetSection("InAppInclude")))
        {
            o.InAppInclude.Add(p);
        }
        foreach (var p in List(c.GetSection("InAppExclude")))
        {
            o.InAppExclude.Add(p);
        }
        return o;
    }

    public static FixwireLoggingOptions ReadLogging(IConfiguration c)
    {
        var o = new FixwireLoggingOptions();
        if (Enum.TryParse<LogLevel>(c["BreadcrumbLevel"], ignoreCase: true, out var b))
        {
            o.BreadcrumbLevel = b;
        }
        if (Enum.TryParse<LogLevel>(c["EventLevel"], ignoreCase: true, out var e))
        {
            o.EventLevel = e;
        }
        return o;
    }

    private static double? Double(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static int? Int(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static bool? Bool(string? s) => bool.TryParse(s, out var b) ? b : null;

    /// <summary>An array (<c>["a", "b"]</c> in JSON, <c>X__0</c> in the environment) or a comma-separated string.</summary>
    private static IEnumerable<string> List(IConfigurationSection s)
    {
        if (!string.IsNullOrWhiteSpace(s.Value))
        {
            return s.Value!.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0);
        }
        return s.GetChildren().Select(x => x.Value).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!);
    }
}
