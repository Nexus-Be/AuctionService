namespace Auction.Api.Extensions;

using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

public static class HttpContextExtensions
{
    public static Guid? GetUserId(this HttpContext context)
    {
        var claimValue = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst("id")?.Value;

        if (Guid.TryParse(claimValue, out var claimGuid))
        {
            return claimGuid;
        }

        if (context.Request.Headers.TryGetValue("x-user-id", out var headerVal) &&
            Guid.TryParse(headerVal.ToString(), out var headerGuid))
        {
            return headerGuid;
        }

        return null;
    }

    public static string? GetUserEmail(this HttpContext context)
    {
        var claimEmail = context.User.FindFirst(ClaimTypes.Email)?.Value;
        if (!string.IsNullOrWhiteSpace(claimEmail))
        {
            return claimEmail;
        }

        if (context.Request.Headers.TryGetValue("x-user-email", out var headerVal) &&
            !string.IsNullOrWhiteSpace(headerVal.ToString()))
        {
            return headerVal.ToString().Trim();
        }

        return null;
    }

    public static string[] GetUserRoles(this HttpContext context)
    {
        var roles = context.User.FindAll(ClaimTypes.Role)
            .Concat(context.User.FindAll("role"))
            .Select(c => c.Value.ToLowerInvariant())
            .Distinct()
            .ToList();

        if (roles.Count == 0 && context.Request.Headers.TryGetValue("x-user-roles", out var headerVal))
        {
            roles = headerVal.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(r => r.ToLowerInvariant())
                .Distinct()
                .ToList();
        }

        return roles.ToArray();
    }

    public static string[] GetUserPermissions(this HttpContext context)
    {
        var permissions = context.User.FindAll("permission")
            .Concat(context.User.FindAll("permissions"))
            .Select(c => c.Value.ToUpperInvariant())
            .Distinct()
            .ToList();

        if (permissions.Count == 0 && context.Request.Headers.TryGetValue("x-user-permissions", out var headerVal))
        {
            permissions = headerVal.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.ToUpperInvariant())
                .Distinct()
                .ToList();
        }

        return permissions.ToArray();
    }

    public static bool HasRole(this HttpContext context, string role)
    {
        if (string.IsNullOrWhiteSpace(role)) return false;
        var roles = context.GetUserRoles();
        return roles.Contains(role.Trim().ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
    }

    public static bool HasPermission(this HttpContext context, string permission)
    {
        if (string.IsNullOrWhiteSpace(permission)) return false;
        var permissions = context.GetUserPermissions();
        return permissions.Contains(permission.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsAdmin(this HttpContext context)
    {
        return context.HasRole("admin") || context.HasRole("administrator");
    }

    public static string? GetTraceId(this HttpContext context)
    {
        return Activity.Current?.Id ?? context.TraceIdentifier;
    }

    public static string? GetClientIp(this HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("x-client-ip", out var headerVal) &&
            !string.IsNullOrWhiteSpace(headerVal.ToString()))
        {
            return headerVal.ToString().Trim();
        }

        return context.Connection.RemoteIpAddress?.ToString();
    }

    public static string? GetIdempotencyKey(this HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("Idempotency-Key", out var headerVal) &&
            !string.IsNullOrWhiteSpace(headerVal.ToString()))
        {
            return headerVal.ToString().Trim();
        }

        return null;
    }

    // ControllerBase delegating shortcuts
    public static Guid? GetUserId(this ControllerBase controller) => controller.HttpContext.GetUserId();
    public static string? GetUserEmail(this ControllerBase controller) => controller.HttpContext.GetUserEmail();
    public static string[] GetUserRoles(this ControllerBase controller) => controller.HttpContext.GetUserRoles();
    public static string[] GetUserPermissions(this ControllerBase controller) => controller.HttpContext.GetUserPermissions();
    public static bool HasRole(this ControllerBase controller, string role) => controller.HttpContext.HasRole(role);
    public static bool HasPermission(this ControllerBase controller, string permission) => controller.HttpContext.HasPermission(permission);
    public static bool IsAdmin(this ControllerBase controller) => controller.HttpContext.IsAdmin();
    public static string? GetTraceId(this ControllerBase controller) => controller.HttpContext.GetTraceId();
    public static string? GetClientIp(this ControllerBase controller) => controller.HttpContext.GetClientIp();
    public static string? GetIdempotencyKey(this ControllerBase controller) => controller.HttpContext.GetIdempotencyKey();

    // Standardized RFC 9110 ProblemDetails Result
    public static IActionResult ErrorResult(this ControllerBase controller, int statusCode, string errorCode, string message)
    {
        var traceId = controller.GetTraceId();
        return new ObjectResult(new
        {
            type = $"https://tools.ietf.org/html/rfc9110#section-{(statusCode >= 500 ? "15.6.1" : "15.5.1")}",
            title = GetStatusTitle(statusCode),
            status = statusCode,
            errorCode,
            detail = message,
            message,
            traceId
        })
        {
            StatusCode = statusCode,
            ContentTypes = { "application/problem+json" }
        };
    }

    public static IActionResult UnauthorizedError(this ControllerBase controller, string message = "Authentication is required to access this resource.", string errorCode = "UNAUTHORIZED")
        => controller.ErrorResult(StatusCodes.Status401Unauthorized, errorCode, message);

    public static IActionResult ForbiddenError(this ControllerBase controller, string message = "You do not have permission to access or modify this resource.", string errorCode = "FORBIDDEN")
        => controller.ErrorResult(StatusCodes.Status403Forbidden, errorCode, message);

    public static IActionResult NotFoundError(this ControllerBase controller, string errorCode, string message)
        => controller.ErrorResult(StatusCodes.Status404NotFound, errorCode, message);

    public static IActionResult BadRequestError(this ControllerBase controller, string errorCode, string message)
        => controller.ErrorResult(StatusCodes.Status400BadRequest, errorCode, message);

    public static IActionResult InvalidUserIdError(this ControllerBase controller, string message = "Missing or invalid x-user-id header.")
        => controller.ErrorResult(StatusCodes.Status400BadRequest, "VAL_INVALID_USER_ID", message);

    // Extraction & Guard Helpers
    public static bool TryGetUserId(this ControllerBase controller, out Guid userId, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out IActionResult? errorResult)
    {
        var id = controller.GetUserId();
        if (id == null)
        {
            userId = Guid.Empty;
            errorResult = controller.UnauthorizedError();
            return false;
        }

        userId = id.Value;
        errorResult = null;
        return true;
    }

    public static bool EnsureOwnership(this ControllerBase controller, Guid resourceOwnerId, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out IActionResult? errorResult)
    {
        var currentUserId = controller.GetUserId();
        if (currentUserId == null)
        {
            errorResult = controller.UnauthorizedError();
            return false;
        }

        if (currentUserId.Value != resourceOwnerId && !controller.IsAdmin() && !controller.HasRole("staff"))
        {
            errorResult = controller.ForbiddenError();
            return false;
        }

        errorResult = null;
        return true;
    }

    public static bool EnsureRole(this ControllerBase controller, string requiredRole, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out IActionResult? errorResult)
    {
        if (!controller.HasRole(requiredRole))
        {
            errorResult = controller.ForbiddenError($"Access denied: Role '{requiredRole}' is required to access this resource.");
            return false;
        }

        errorResult = null;
        return true;
    }

    public static bool EnsureAdmin(this ControllerBase controller, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out IActionResult? errorResult)
    {
        if (!controller.IsAdmin())
        {
            errorResult = controller.ForbiddenError("Access denied: Administrator privileges are required.");
            return false;
        }

        errorResult = null;
        return true;
    }

    private static string GetStatusTitle(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "Bad request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not found",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status422UnprocessableEntity => "Unprocessable entity",
        StatusCodes.Status429TooManyRequests => "Too many requests",
        StatusCodes.Status500InternalServerError => "Internal server error",
        _ => "An error occurred"
    };
}
