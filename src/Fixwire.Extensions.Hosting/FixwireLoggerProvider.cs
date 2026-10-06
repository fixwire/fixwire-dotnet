using Microsoft.Extensions.Logging;

namespace Fixwire.Extensions.Hosting;

/// <summary>
/// Sends <see cref="ILogger"/> records to Fixwire: from <see cref="FixwireLoggingOptions.BreadcrumbLevel"/> they
/// become breadcrumbs, from <see cref="FixwireLoggingOptions.EventLevel"/> events (as the record's exception when it
/// has one, once if the app captured it already). Log lines themselves are not shipped.
/// </summary>
[ProviderAlias("Fixwire")]
public sealed class FixwireLoggerProvider : ILoggerProvider
{
    private readonly FixwireLoggingOptions _options;

    /// <summary>A provider with its levels.</summary>
    /// <param name="options">The levels.</param>
    public FixwireLoggerProvider(FixwireLoggingOptions options) => _options = options;

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new FixwireLogger(categoryName, _options);

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private sealed class FixwireLogger(string category, FixwireLoggingOptions options) : ILogger
    {
        /// <summary>
        /// Whether this thread is inside Log already: a record logged while one is captured (by
        /// BeforeSend, BeforeBreadcrumb, an exception's ToString) is not captured again.
        /// </summary>
        [ThreadStatic]
        private static bool _logging;

        // The SDK's own records never go back to Fixwire.
        private readonly bool _own = category == "Fixwire"
            || category.StartsWith("Fixwire.Extensions.", StringComparison.Ordinal)
            || category.StartsWith("Fixwire.AspNetCore.", StringComparison.Ordinal);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            !_own && logLevel != LogLevel.None && (logLevel >= options.BreadcrumbLevel || logLevel >= options.EventLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (_logging || !IsEnabled(logLevel) || !FixwireSdk.IsEnabled)
            {
                return;
            }
            _logging = true;
            try
            {
                Capture(logLevel, eventId, formatter(state, exception), exception);
            }
#pragma warning disable CA1031 // the app's log call must never fail because of Fixwire
            catch (Exception)
            {
            }
#pragma warning restore CA1031
            finally
            {
                _logging = false;
            }
        }

        private void Capture(LogLevel logLevel, EventId eventId, string message, Exception? exception)
        {
            var hub = Hub.Current;
            var level = LevelOf(logLevel);
            if (logLevel >= options.EventLevel)
            {
                if (exception != null && hub.Client is { } client && client.IsCaptured(exception))
                {
                    return; // sent already, where it was caught
                }
                if (exception != null && options.ReportedElsewhere.Contains(category))
                {
                    return; // an integration reports it, as what it is
                }
                var e = exception == null
                    ? new FixwireEvent { Message = message, Level = level }
                    : new FixwireEvent
                    {
                        Exception = exception,
                        Exceptions = Frames.Chain(exception, "logging", handled: true, hub.Client!.Options),
                        Level = level,
                    };
                e.Extra["logger"] = category;
                if (exception != null)
                {
                    e.Extra["log.message"] = message;
                }
                if (eventId.Id != 0 || eventId.Name != null)
                {
                    e.Extra["log.event_id"] = eventId.Name ?? eventId.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                hub.CaptureEvent(e);
            }
            else
            {
                hub.AddBreadcrumb(new Breadcrumb(category, message) { Type = "log", Level = level });
            }
        }

        private static Level LevelOf(LogLevel l) => l switch
        {
            >= LogLevel.Critical => Level.Fatal,
            LogLevel.Error => Level.Error,
            LogLevel.Warning => Level.Warning,
            LogLevel.Information => Level.Info,
            _ => Level.Debug,
        };
    }
}

/// <summary>Which <see cref="ILogger"/> records become breadcrumbs and which events.</summary>
public sealed class FixwireLoggingOptions
{
    /// <summary>Records at or above it become breadcrumbs (default Information).</summary>
    public LogLevel BreadcrumbLevel { get; set; } = LogLevel.Information;

    /// <summary>Records at or above it are sent as events (default Error).</summary>
    public LogLevel EventLevel { get; set; } = LogLevel.Error;

    /// <summary>Loggers whose exceptions an integration reports itself.</summary>
    internal HashSet<string> ReportedElsewhere { get; } = new(StringComparer.Ordinal);
}
