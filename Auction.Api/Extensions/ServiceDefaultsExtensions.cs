namespace Auction.Api.Extensions;

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Auction.Api.Common.Converters;
using Auction.Api.Middleware;

public static class ServiceDefaultsExtensions
{
    public static IServiceCollection AddServiceDefaults(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        services.AddServiceObservability(configuration, serviceName);
        services.AddHttpContextAccessor();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new PascalCaseJsonStringEnumConverter(allowIntegerValues: false));
        });
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new PascalCaseJsonStringEnumConverter(allowIntegerValues: false));
        });

        return services;
    }

    public static WebApplication UseServiceDefaults(this WebApplication app, string? serviceName = null)
    {
        app.UseGlobalExceptionHandler();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
            app.MapGet("/", () => Results.Redirect("/swagger"));
        }

        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = serviceName }));
        }

        return app;
    }

    public static WebApplication MigrateDatabase<TDbContext>(this WebApplication app) where TDbContext : DbContext
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        try
        {
            if (db.Database.IsRelational())
            {
                db.Database.Migrate();
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning("Database migration deferred on startup: {Message}", ex.Message);
        }

        return app;
    }

    public static WebApplication UseServiceDefaults<TDbContext>(this WebApplication app, string serviceName) where TDbContext : DbContext
    {
        app.UseServiceDefaults(serviceName);
        app.MigrateDatabase<TDbContext>();
        return app;
    }
}
