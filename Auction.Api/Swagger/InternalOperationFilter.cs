namespace Auction.Api.Swagger;

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

public sealed class InternalOperationFilter : IOperationFilter
{
    private readonly string _serviceKey;

    public InternalOperationFilter(IConfiguration configuration)
    {
        _serviceKey = Environment.GetEnvironmentVariable("INTERNAL_SERVICE_SECRET")
            ?? configuration["INTERNAL_SERVICE_SECRET"]
            ?? configuration["InternalSecurity:ServiceKey"]
            ?? string.Empty;
    }

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.RelativePath is not { } path ||
            !path.StartsWith("api/v1/internal", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        operation.Parameters ??= new List<OpenApiParameter>();
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-Internal-Service-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Internal service secret key (pre-filled from environment).",
            Schema = new OpenApiSchema
            {
                Type = "string",
                Default = new OpenApiString(_serviceKey)
            }
        });
    }
}
