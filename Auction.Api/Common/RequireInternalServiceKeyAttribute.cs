namespace Auction.Api.Common;

using System;
using System.Security.Cryptography;
using System.Text;
using Auction.Domain.Exceptions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Yêu cầu header X-Internal-Service-Key đúng với khoá dùng chung giữa các service (dùng cho callback của Commerce).
/// Chỉ kiểm tra khi bật InternalSecurity:ProtectSettlementCallback (mặc định tắt cho tới khi các bên thống nhất thời điểm),
/// và không bao giờ kiểm tra ở môi trường Development hoặc khi Swagger:Enabled. Sai hoặc thiếu khoá thì trả 403 FORBIDDEN.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireInternalServiceKeyAttribute : ActionFilterAttribute
{
    public const string HeaderName = "X-Internal-Service-Key";

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var services = context.HttpContext.RequestServices;
        var configuration = services.GetRequiredService<IConfiguration>();

        if (!configuration.GetValue<bool>("InternalSecurity:ProtectSettlementCallback"))
            return;

        var environment = services.GetRequiredService<IHostEnvironment>();
        if (environment.IsDevelopment() || configuration.GetValue<bool>("Swagger:Enabled"))
            return;

        var expectedKey = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_SECRET")
            ?? configuration["INTERNAL_SERVICE_SECRET"]
            ?? configuration["InternalSecurity:ServiceKey"];
        if (string.IsNullOrWhiteSpace(expectedKey))
            throw new DomainException(
                "INTERNAL_CONFIGURATION_ERROR", "Internal service authentication is not configured.", 500);

        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var provided)
            || !KeysMatch(provided.ToString(), expectedKey))
        {
            throw new DomainException("FORBIDDEN", "Access denied to internal service resource.", 403);
        }
    }

    private static bool KeysMatch(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
}
