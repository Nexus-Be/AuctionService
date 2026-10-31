namespace Auction.Api.Middleware;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Auction.Domain.Exceptions;

public sealed class GlobalExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlerMiddleware> _logger;

    public GlobalExceptionHandlerMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlerMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;

        var (statusCode, errorCode, title, detail) = exception switch
        {
            BusinessRuleValidationException bve => (
                bve.StatusCode,
                bve.ErrorCode,
                GetStatusTitle(bve.StatusCode, "Business rule violation"),
                bve.Message),
            EntityNotFoundException nfe => (
                nfe.StatusCode,
                nfe.ErrorCode,
                GetStatusTitle(nfe.StatusCode, "Resource not found"),
                nfe.Message),
            DomainException dex => (
                dex.StatusCode,
                dex.ErrorCode,
                GetStatusTitle(dex.StatusCode, "Domain error"),
                dex.Message),
            ArgumentException aex => (
                StatusCodes.Status400BadRequest,
                "VAL_INVALID_ARGUMENT",
                GetStatusTitle(StatusCodes.Status400BadRequest, "Invalid argument"),
                aex.Message),
            _ => (
                StatusCodes.Status500InternalServerError,
                "INTERNAL_SERVER_ERROR",
                GetStatusTitle(StatusCodes.Status500InternalServerError, "An unexpected error occurred"),
                "An internal server error occurred. Please try again later.")
        };

        if (statusCode >= 500)
        {
            _logger.LogError(exception, "[ERROR {ErrorCode}] {Title}: {Detail}. Trace: {TraceId}", errorCode, title, detail, traceId);
        }
        else
        {
            _logger.LogWarning("[WARN {ErrorCode}] {Title}: {Detail}. Trace: {TraceId}", errorCode, title, detail, traceId);
        }

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = statusCode;

        var problemDetails = new
        {
            type = $"https://tools.ietf.org/html/rfc9110#section-{(statusCode >= 500 ? "15.6.1" : "15.5.1")}",
            title,
            status = statusCode,
            errorCode,
            detail,
            traceId
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(problemDetails));
    }

    private static string GetStatusTitle(int statusCode, string fallback) => statusCode switch
    {
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not found",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status423Locked => "Account locked",
        StatusCodes.Status429TooManyRequests => "Too many requests",
        _ => fallback
    };
}

public static class GlobalExceptionHandlerMiddlewareExtensions
{
    public static IApplicationBuilder UseGlobalExceptionHandler(this IApplicationBuilder app) =>
        app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
}
