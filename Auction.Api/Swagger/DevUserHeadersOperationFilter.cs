namespace Auction.Api.Swagger;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

/// <summary>
/// Thêm ô nhập header vào Swagger cho các header mà controller đọc trực tiếp từ Request.Headers (nên Swagger không tự biết):
/// - Chỉ ở môi trường Development: x-user-id và x-user-roles, để test mà không cần API Gateway.
/// - Mọi môi trường: Idempotency-Key và If-Match cho POST /api/v1/auctions/{auctionId}/bids (đặt giá), vì API bắt buộc hai header này.
/// Endpoint đã có sẵn tham số cùng tên thì bỏ qua, tránh gửi trùng header.
/// </summary>
public sealed class DevUserHeadersOperationFilter : IOperationFilter
{
    private readonly IHostEnvironment _environment;

    public DevUserHeadersOperationFilter(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Parameters ??= new List<OpenApiParameter>();

        if (_environment.IsDevelopment())
        {
            AddHeader(operation, "x-user-id", "Id của user đang gọi (GUID), lấy từ DB User.", required: false, defaultValue: null);
            AddHeader(operation, "x-user-roles", "Role của user: buyer, seller hoặc admin.", required: false, defaultValue: "seller");
        }

        if (IsPlaceBid(context))
        {
            AddHeader(operation, "Idempotency-Key", "Chuỗi bất kỳ, mỗi lần đặt giá mới phải khác. Gửi lại cùng key và cùng nội dung thì nhận lại kết quả lần đầu.", required: true, defaultValue: null);
            AddHeader(operation, "If-Match", "RowVersion hiện tại của phiên (số), lấy từ trường rowVersion của GET /api/v1/auctions/{id}.", required: true, defaultValue: null);
        }
    }

    private static bool IsPlaceBid(OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath ?? string.Empty;
        return string.Equals(context.ApiDescription.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/bids", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddHeader(OpenApiOperation operation, string name, string description, bool required, string? defaultValue)
    {
        var exists = operation.Parameters.Any(p =>
            p.In == ParameterLocation.Header &&
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (exists)
            return;

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = required,
            Description = description,
            Schema = new OpenApiSchema
            {
                Type = "string",
                Default = defaultValue is null ? null : new OpenApiString(defaultValue)
            }
        });
    }
}