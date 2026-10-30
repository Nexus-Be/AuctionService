namespace Auction.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Auction.Application.Interfaces;
using Auction.Infrastructure.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        var rawConnection = configuration.GetConnectionString("DefaultConnection")
            ?? configuration["CONNECTION_STRING"]
            ?? "Host=localhost;Port=5437;Database=auction_db;Username=postgres;Password=postgres";
        var connectionString = NormalizeConnectionString(rawConnection);

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<IAuctionDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        return services;
    }

    private static string NormalizeConnectionString(string? connStr)
    {
        if (string.IsNullOrWhiteSpace(connStr)) return string.Empty;
        if (connStr.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || 
            connStr.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(connStr);
            var userInfo = uri.UserInfo.Split(':');
            var user = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : "";
            var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
            var host = uri.Host;
            var port = uri.Port > 0 ? uri.Port : 5432;
            var db = uri.AbsolutePath.TrimStart('/');
            return $"Host={host};Port={port};Database={db};Username={user};Password={pass};SSL Mode=Require;Trust Server Certificate=true";
        }
        return connStr;
    }
}
