using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Fixwire;

/// <summary>Exceptions and their stacks, as events carry them.</summary>
internal static class Frames
{
    /// <summary>The inner exceptions followed, and the frames kept per exception (the newest).</summary>
    public const int MaxChain = 10;

    public const int MaxFrames = 100;

    /// <summary>Namespaces of .NET, ASP.NET Core and well-known libraries: not the app's.</summary>
    private static readonly string[] Libraries =
    [
        "System.", "Microsoft.", "Windows.", "MS.", "Mono.", "Internal.", "Interop", "Newtonsoft.", "Npgsql",
        "Dapper", "Polly", "Serilog", "NLog", "log4net", "Grpc.", "Google.", "Azure.", "Amazon.", "StackExchange.",
        "MediatR", "AutoMapper", "FluentValidation", "Castle.", "Autofac", "Xunit", "NUnit", "Moq", "Fixwire.",
    ];

    /// <summary>An exception and its inner exceptions, the outermost first.</summary>
    /// <param name="exception">The exception.</param>
    /// <param name="mechanism">How it was caught; inner exceptions are <c>chained</c>.</param>
    /// <param name="handled">False for a crash.</param>
    /// <param name="options">For which frames are the app's.</param>
    public static List<ExceptionValue> Chain(Exception exception, string mechanism, bool handled, FixwireOptions options)
    {
        var chain = new List<ExceptionValue>();
        var seen = new HashSet<Exception>(ReferenceComparer.Instance);
        for (var e = exception; e != null && chain.Count < MaxChain && seen.Add(e); e = e.InnerException)
        {
            var type = e.GetType();
            chain.Add(new ExceptionValue
            {
                Type = type.FullName ?? type.Name,
                Module = type.Namespace ?? "",
                Message = MessageOf(e),
                Mechanism = chain.Count == 0 ? mechanism : "chained",
                Handled = handled,
                Frames = Of(new StackTrace(e, fNeedFileInfo: true), options),
            });
        }
        // An exception captured without being thrown has no stack: it gets the capturer's.
        if (chain.Count > 0 && chain[0].Frames.Count == 0)
        {
            chain[0].Frames = Of(new StackTrace(1, fNeedFileInfo: true), options, skipSdk: true);
        }
        return chain;
    }

    /// <summary>An exception's message; null when its Message throws (the type still says what it was).</summary>
    internal static string? MessageOf(Exception e)
    {
        try
        {
            return e.Message;
        }
#pragma warning disable CA1031 // an exception type's own bug must not lose its event
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>A stack trace (the newest call first) as frames, the oldest first.</summary>
#if NET8_0_OR_GREATER
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "A trimmed app may lose a frame's method; such frames are left out.")]
#endif
    private static List<Frame> Of(StackTrace trace, FixwireOptions options, bool skipSdk = false)
    {
        var stack = trace.GetFrames() ?? [];
        var frames = new List<Frame>(Math.Min(stack.Length, MaxFrames));
        foreach (var f in stack)
        {
            if (frames.Count == MaxFrames)
            {
                break; // the newest calls are kept
            }
            var method = f?.GetMethod();
            if (f == null || method == null)
            {
                continue;
            }
            if (skipSdk && method.DeclaringType?.Assembly == typeof(Frames).Assembly)
            {
                continue; // the SDK's own
            }
            var (module, function) = Name(method);
            frames.Add(new Frame
            {
                Module = module,
                Function = function,
                File = f.GetFileName(),
                Line = Math.Max(f.GetFileLineNumber(), 0),
                Column = Math.Max(f.GetFileColumnNumber(), 0),
                InApp = InApp(module, options),
            });
        }
        frames.Reverse();
        return frames;
    }

    /// <summary>
    /// The type and method a frame is in, as written: an async method's state machine
    /// (<c>Shop.Cart+&lt;CheckoutAsync&gt;d__3.MoveNext</c>) is <c>Shop.Cart</c>'s <c>CheckoutAsync</c>, and a
    /// lambda's closure class is the type it was written in.
    /// </summary>
    internal static (string Module, string Function) Name(MethodBase method)
    {
        var type = method.DeclaringType;
        var function = method.Name;
        if (type == null)
        {
            return ("", function);
        }
        if (type.Name.StartsWith("<", StringComparison.Ordinal) && type.DeclaringType != null)
        {
            var close = type.Name.IndexOf('>');
            if (function == "MoveNext" && close > 1)
            {
                function = type.Name.Substring(1, close - 1); // the async method or iterator
            }
            type = type.DeclaringType; // <>c, <>c__DisplayClass…, <Name>d__…
        }
        return (type.FullName ?? type.Name, function);
    }

    /// <summary>Whether a type is the app's code.</summary>
    public static bool InApp(string module, FixwireOptions options)
    {
        foreach (var p in options.InAppExclude)
        {
            if (module.StartsWith(p, StringComparison.Ordinal))
            {
                return false;
            }
        }
        foreach (var p in options.InAppInclude)
        {
            if (module.StartsWith(p, StringComparison.Ordinal))
            {
                return true;
            }
        }
        foreach (var p in Libraries)
        {
            if (module.StartsWith(p, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return module.Length > 0;
    }

    private sealed class ReferenceComparer : IEqualityComparer<Exception>
    {
        public static readonly ReferenceComparer Instance = new();

        public bool Equals(Exception? x, Exception? y) => ReferenceEquals(x, y);

        public int GetHashCode(Exception obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
