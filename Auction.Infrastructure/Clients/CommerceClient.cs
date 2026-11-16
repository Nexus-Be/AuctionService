namespace Auction.Infrastructure.Clients;

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Auction.Application.Interfaces;
using Auction.Domain.Exceptions;
using Microsoft.Extensions.Logging;

/// <summary>Gọi Commerce để tạo đơn cho người thắng (POST /api/v1/auction-orders/settlement). Header X-Internal-Service-Key được gắn khi đăng ký HttpClient.</summary>
public sealed class CommerceClient : ICommerceClient
{
    private const string SettlementPath = "/api/v1/auction-orders/settlement";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<CommerceClient> _logger;

    public CommerceClient(HttpClient http, ILogger<CommerceClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<Guid?> SendSettlementAsync(CommerceSettlementPayload payload, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(SettlementPath, payload, JsonOptions, cancellationToken);
            var status = (int)response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
                return ReadOrderId(text);

            var code = ReadErrorCode(text);

            // Commerce đã có đơn còn hiệu lực của người thắng khác cho phiên này: không còn gì để gửi.
            if (status == 409 && (code == "SETTLEMENT_CONFLICT" || code == "AUCTION_ALREADY_SETTLED"))
            {
                _logger.LogWarning(
                    "Commerce already has an active order for auction {AuctionId} ({Code}); the settlement was not created again.",
                    payload.AuctionId, code);
                return null;
            }

            if (status >= 500 || status is 408 or 429)
                throw new DomainException(
                    "COMMERCE_SERVICE_UNAVAILABLE", $"Commerce answered HTTP {status} to the settlement.", 503);

            throw new DomainException(
                "COMMERCE_REJECTED_SETTLEMENT", $"Commerce rejected the settlement: HTTP {status} ({code}).", 502);
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new DomainException("COMMERCE_SERVICE_UNAVAILABLE", "The commerce service is unavailable.", 503);
        }
    }

    /// <summary>Lấy mã đơn từ câu trả lời của Commerce: orderId ở gốc hoặc trong data, không có thì thử trường id.</summary>
    private static Guid? ReadOrderId(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            Guid id;
            if (TryReadGuid(root, "orderId", out id))
                return id;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && (TryReadGuid(data, "orderId", out id) || TryReadGuid(data, "id", out id)))
                return id;

            return TryReadGuid(root, "id", out id) ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadErrorCode(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("errorCode", out var errorCode) && errorCode.ValueKind == JsonValueKind.String)
                return errorCode.GetString();
            if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                return code.GetString();
            if (root.TryGetProperty("error", out var errObj) && errObj.ValueKind == JsonValueKind.Object)
            {
                if (errObj.TryGetProperty("code", out var nestedCode) && nestedCode.ValueKind == JsonValueKind.String)
                    return nestedCode.GetString();
                if (errObj.TryGetProperty("errorCode", out var nestedErrorCode) && nestedErrorCode.ValueKind == JsonValueKind.String)
                    return nestedErrorCode.GetString();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadGuid(JsonElement element, string name, out Guid value)
    {
        value = Guid.Empty;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), out value);
    }
}
