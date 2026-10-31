namespace Auction.Api.Middleware;

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class InternalServiceAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<InternalServiceAuthMiddleware> _logger;

    public const string HeaderKeyInternalService = "X-Internal-Service-Key";

    public InternalServiceAuthMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<InternalServiceAuthMiddleware> logger)
    {
        _next = next;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api/v1/internal", StringComparison.OrdinalIgnoreCase))
        {
            var expectedKey = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_SECRET")
                ?? _configuration["INTERNAL_SERVICE_SECRET"]
                ?? _configuration["InternalSecurity:ServiceKey"];

            if (string.IsNullOrWhiteSpace(expectedKey))
            {
                _logger.LogError("Internal service key is not configured.");
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"errorCode\":\"INTERNAL_CONFIGURATION_ERROR\",\"message\":\"Internal service authentication is not configured.\"}");
                return;
            }

            var isDevOrSwagger = _environment.IsDevelopment()
                || _configuration.GetValue<bool>("Swagger:Enabled");

            var isSwaggerReferer = isDevOrSwagger &&
                (context.Request.Headers.TryGetValue("Referer", out var referer) &&
                 referer.ToString().Contains("/swagger", StringComparison.OrdinalIgnoreCase));

            if (context.Request.Headers.TryGetValue(HeaderKeyInternalService, out var providedKey))
            {
                if (!string.Equals(providedKey.ToString(), expectedKey, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Access denied to internal endpoint with invalid key: {Path}", context.Request.Path);
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"errorCode\":\"FORBIDDEN\",\"message\":\"Access denied to internal service resource.\"}");
                    return;
                }
            }
            else if (!isSwaggerReferer)
            {
                _logger.LogWarning("Access denied to internal endpoint (missing key): {Path}", context.Request.Path);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"errorCode\":\"FORBIDDEN\",\"message\":\"Access denied to internal service resource.\"}");
                return;
            }
            else
            {
                _logger.LogInformation("Permitting internal endpoint access for Swagger test in development: {Path}", context.Request.Path);
            }
        }

        await _next(context);
    }
}
