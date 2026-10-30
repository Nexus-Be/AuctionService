namespace Auction.Api.Extensions;

using System;
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

    public static Guid? GetUserId(this ControllerBase controller) => controller.HttpContext.GetUserId();
    public static string? GetUserEmail(this ControllerBase controller) => controller.HttpContext.GetUserEmail();
    public static string[] GetUserRoles(this ControllerBase controller) => controller.HttpContext.GetUserRoles();
    public static string[] GetUserPermissions(this ControllerBase controller) => controller.HttpContext.GetUserPermissions();
    public static bool HasRole(this ControllerBase controller, string role) => controller.HttpContext.HasRole(role);
    public static bool HasPermission(this ControllerBase controller, string permission) => controller.HttpContext.HasPermission(permission);
    public static bool IsAdmin(this ControllerBase controller) => controller.HttpContext.IsAdmin();
}
