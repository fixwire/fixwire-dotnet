using Fixwire;
using Fixwire.AspNetCore;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// In ASP.NET Core's namespace, so that `builder.AddFixwire()` needs no using of its own.
namespace Microsoft.AspNetCore.Builder;

/// <summary>Sets Fixwire up in an ASP.NET Core app.</summary>
public static class FixwireWebApplicationBuilderExtensions
{
    /// <summary>
    /// Sets Fixwire up from the <c>Fixwire</c> configuration section and then <paramref name="configure"/>:
    /// everything <see cref="FixwireHostingExtensions.AddFixwire"/> does, and for each request its own
    /// scope, crash reporting (also of exceptions <c>UseExceptionHandler</c> or the developer page
    /// catch), release health and a server span named after its route.
    /// <code>
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.AddFixwire();
    /// </code>
    /// </summary>
    /// <param name="builder">The app's builder.</param>
    /// <param name="configure">Changes the options read from configuration.</param>
    public static WebApplicationBuilder AddFixwire(this WebApplicationBuilder builder, Action<FixwireOptions>? configure = null)
    {
        ((IHostApplicationBuilder)builder).AddFixwire(configure);
        builder.Services.AddTransient<IStartupFilter, FixwireStartupFilter>();
        builder.Services.AddSingleton<IExceptionHandler, FixwireExceptionHandler>();
        builder.Services.AddSingleton<IDeveloperPageExceptionFilter, FixwireDeveloperPageExceptionFilter>();
        return builder;
    }
}
