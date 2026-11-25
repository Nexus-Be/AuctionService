namespace Auction.Api.Common;

using System;
using Auction.Domain.Exceptions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// The two admin routes that move an auction's clock (schedule and settlement deadlines) exist only for testing. They are open only when
/// <c>AdminTimeControls:Enabled</c> is switched on explicitly (it is off by default) AND the service does not run as Production.
/// A Production host never opens them, whatever the setting says.
/// </summary>
public static class AdminTimeControlGate
{
    public const string ConfigKey = "AdminTimeControls:Enabled";

    /// <summary>A missing, empty or malformed value is closed: only a clear "true" opens the gate, and a bad setting never turns into an error.</summary>
    public static bool IsOpen(IConfiguration configuration, IHostEnvironment environment) =>
        !environment.IsProduction() && bool.TryParse(configuration[ConfigKey], out var enabled) && enabled;
}

/// <summary>
/// Runs before model binding and authentication so that, while the gate is closed, every caller gets the same 404 as for a route that does not
/// exist: the answer does not reveal that the route is there or who may use it. When the gate is open, the controller still checks the role.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireAdminTimeControlsAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var services = context.HttpContext.RequestServices;
        if (!AdminTimeControlGate.IsOpen(services.GetRequiredService<IConfiguration>(), services.GetRequiredService<IHostEnvironment>()))
        {
            throw new DomainException("NOT_FOUND", "The requested resource was not found.", 404);
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
