namespace Auction.Infrastructure.Services.Common;

using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// DelegatingHandler that automatically extracts ambient user identity and roles from HttpContext
/// and propagates them as HTTP headers (x-user-id, x-user-roles, x-user-email, x-user-permissions, x-client-ip, x-trace-id) on outbound requests.
/// </summary>
public sealed class IdentityPropagationHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<IdentityPropagationHandler> _logger;

    public IdentityPropagationHandler(
        IHttpContextAccessor httpContextAccessor,
        ILogger<IdentityPropagationHandler> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext != null)
        {
            if (!request.Headers.Contains("x-user-id"))
            {
                var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? httpContext.User.FindFirst("sub")?.Value
                    ?? (httpContext.Request.Headers.TryGetValue("x-user-id", out var hVal) ? hVal.ToString() : null);

                if (!string.IsNullOrWhiteSpace(userId))
                {
                    request.Headers.TryAddWithoutValidation("x-user-id", userId);
                }
            }

            if (!request.Headers.Contains("x-user-roles"))
            {
                var roles = httpContext.User.FindAll(ClaimTypes.Role)
                    .Concat(httpContext.User.FindAll("role"))
                    .Select(c => c.Value.ToLowerInvariant())
                    .Distinct()
                    .ToList();

                string? rolesString = null;
                if (roles.Count > 0)
                {
                    rolesString = string.Join(",", roles);
                }
                else if (httpContext.Request.Headers.TryGetValue("x-user-roles", out var hRoles))
                {
                    rolesString = hRoles.ToString();
                }

                if (!string.IsNullOrWhiteSpace(rolesString))
                {
                    request.Headers.TryAddWithoutValidation("x-user-roles", rolesString);
                }
            }

            if (!request.Headers.Contains("x-user-email"))
            {
                var email = httpContext.User.FindFirst(ClaimTypes.Email)?.Value
                    ?? (httpContext.Request.Headers.TryGetValue("x-user-email", out var hEmail) ? hEmail.ToString() : null);

                if (!string.IsNullOrWhiteSpace(email))
                {
                    request.Headers.TryAddWithoutValidation("x-user-email", email);
                }
            }

            if (!request.Headers.Contains("x-user-permissions"))
            {
                var perms = httpContext.User.FindAll("permission")
                    .Select(c => c.Value.ToUpperInvariant())
                    .Distinct()
                    .ToList();

                string? permsString = null;
                if (perms.Count > 0)
                {
                    permsString = string.Join(",", perms);
                }
                else if (httpContext.Request.Headers.TryGetValue("x-user-permissions", out var hPerms))
                {
                    permsString = hPerms.ToString();
                }

                if (!string.IsNullOrWhiteSpace(permsString))
                {
                    request.Headers.TryAddWithoutValidation("x-user-permissions", permsString);
                }
            }

            if (!request.Headers.Contains("x-client-ip"))
            {
                var clientIp = (httpContext.Request.Headers.TryGetValue("x-client-ip", out var hIp) && !string.IsNullOrWhiteSpace(hIp.ToString()))
                    ? hIp.ToString().Trim()
                    : httpContext.Connection.RemoteIpAddress?.ToString();

                if (!string.IsNullOrWhiteSpace(clientIp))
                {
                    request.Headers.TryAddWithoutValidation("x-client-ip", clientIp);
                }
            }

            if (!request.Headers.Contains("x-trace-id"))
            {
                var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
                if (!string.IsNullOrWhiteSpace(traceId))
                {
                    request.Headers.TryAddWithoutValidation("x-trace-id", traceId);
                }
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
