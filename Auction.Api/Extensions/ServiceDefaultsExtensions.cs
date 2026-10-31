namespace Auction.Api.Extensions;

using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using Auction.Api.Common.Converters;
using Auction.Api.Middleware;
using Auction.Api.Swagger;

public static class ServiceDefaultsExtensions
{
    public static IServiceCollection AddServiceDefaults(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        services.AddServiceObservability(configuration, serviceName);
        services.AddHttpContextAccessor();
        services.AddEndpointsApiExplorer();

        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = $"Nexus {ToDisplayName(serviceName)} REST API",
                Version = "v1",
                Description = $"RESTful Web API for {serviceName}."
            });

            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Enter a valid JWT Bearer token."
            });

            options.AddSecurityDefinition("x-user-id", new OpenApiSecurityScheme
            {
                Name = "x-user-id",
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Description = "Header x-user-id forwarded from API Gateway (GUID format)."
            });

            options.AddSecurityDefinition("x-user-roles", new OpenApiSecurityScheme
            {
                Name = "x-user-roles",
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Description = "Header x-user-roles forwarded from API Gateway (e.g. buyer, seller, admin)."
            });

            options.AddSecurityDefinition("x-user-email", new OpenApiSecurityScheme
            {
                Name = "x-user-email",
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Description = "Header x-user-email forwarded from API Gateway."
            });

            options.AddSecurityDefinition("x-user-permissions", new OpenApiSecurityScheme
            {
                Name = "x-user-permissions",
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Description = "Header x-user-permissions forwarded from API Gateway."
            });

            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    Array.Empty<string>()
                },
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "x-user-id"
                        }
                    },
                    Array.Empty<string>()
                },
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "x-user-roles"
                        }
                    },
                    Array.Empty<string>()
                }
            });

            // options.OperationFilter<InternalOperationFilter>();

            var xmlDocumentationPath = Path.Combine(
                AppContext.BaseDirectory,
                $"{typeof(ServiceDefaultsExtensions).Assembly.GetName().Name}.xml");
            if (File.Exists(xmlDocumentationPath))
            {
                options.IncludeXmlComments(xmlDocumentationPath, includeControllerXmlComments: true);
            }
        });

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

    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        app.UseGlobalExceptionHandler();
        // Internal service key check is temporarily disabled. Inter-service calls use plain REST routes.
        // app.UseMiddleware<InternalServiceAuthMiddleware>();

        if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
        {
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "v1");
                c.RoutePrefix = "swagger";
            });
            app.MapGet("/", () => Results.Redirect("/swagger"));
        }

        return app;
    }

    private static string ToDisplayName(string serviceName)
    {
        return string.Join(
            ' ',
            serviceName
                .Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
