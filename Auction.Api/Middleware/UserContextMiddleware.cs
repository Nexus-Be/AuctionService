namespace Auction.Api.Middleware;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Extracts gateway-forwarded identity headers (x-user-id, x-user-roles, x-user-email, x-user-permissions)
/// and initializes a strongly typed ClaimsPrincipal on HttpContext.User.
/// </summary>
public sealed class UserContextMiddleware
{
    private readonly RequestDelegate _next;

    public UserContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("x-user-id", out var userIdHeader) &&
            Guid.TryParse(userIdHeader.ToString(), out var userId))
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.ToString()),
                new("sub", userId.ToString()),
                new("userId", userId.ToString())
            };

            if (context.Request.Headers.TryGetValue("x-user-roles", out var rolesHeader) &&
                !string.IsNullOrWhiteSpace(rolesHeader.ToString()))
            {
                var roles = rolesHeader.ToString()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                foreach (var role in roles)
                {
                    claims.Add(new Claim(ClaimTypes.Role, role.ToLowerInvariant()));
                    claims.Add(new Claim("role", role.ToLowerInvariant()));
                }
            }

            if (context.Request.Headers.TryGetValue("x-user-email", out var emailHeader) &&
                !string.IsNullOrWhiteSpace(emailHeader.ToString()))
            {
                var email = emailHeader.ToString().Trim();
                claims.Add(new Claim(ClaimTypes.Email, email));
                claims.Add(new Claim("email", email));
            }

            if (context.Request.Headers.TryGetValue("x-user-permissions", out var permsHeader) &&
                !string.IsNullOrWhiteSpace(permsHeader.ToString()))
            {
                var perms = permsHeader.ToString()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                foreach (var perm in perms)
                {
                    claims.Add(new Claim("permission", perm.ToUpperInvariant()));
                }
            }

            var identity = new ClaimsIdentity(claims, "NexusGatewayAuth");
            context.User = new ClaimsPrincipal(identity);
        }

        await _next(context);
    }
}
