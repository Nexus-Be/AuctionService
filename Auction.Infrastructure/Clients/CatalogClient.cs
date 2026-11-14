namespace Auction.Infrastructure.Clients;

using System.Net;
using System.Text;
using System.Text.Json;
using Auction.Application.Interfaces;
using Auction.Domain.Exceptions;
using Microsoft.Extensions.Logging;

/// <summary>REST client of the Catalog service (internal APIs used by Auction).</summary>
public sealed class CatalogClient : ICatalogClient
{
    private const string CheckStatusPath = "/api/v1/internal/variants/check-status";
    private const string VariantsPath = "/api/v1/internal/variants";
    private const string ProductPath = "/api/v1/catalog/products/";
    private const string InternalProductPath = "/api/v1/internal/products/";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<CatalogClient> _logger;

    public CatalogClient(HttpClient http, ILogger<CatalogClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CatalogVariantCheck>> CheckVariantsForAuctionAsync(
        IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken = default)
    {
        if (variantIds.Count == 0)
            return Array.Empty<CatalogVariantCheck>();

        var data = await PostAsync<ItemsData<CheckItem>>(
            CheckStatusPath, new VariantQuery(variantIds, "AUCTION"), cancellationToken);

        var result = new List<CatalogVariantCheck>();
        foreach (var item in data.Items ?? new List<CheckItem>())
        {
            // Data from another service is never trusted blindly.
            if (item.VariantId == Guid.Empty || item.ProductId == Guid.Empty)
                throw InvalidResponse("a variant without an id or product id");

            result.Add(new CatalogVariantCheck(item.VariantId, item.ProductId, item.SaleType, item.IsPurchasable, item.Reason));
        }

        return result;
    }

    public async Task<IReadOnlyList<CatalogVariantInfo>> GetVariantsAsync(
        IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken = default)
    {
        if (variantIds.Count == 0)
            return Array.Empty<CatalogVariantInfo>();

        var data = await PostAsync<ItemsData<VariantItem>>(
            VariantsPath, new VariantQuery(variantIds, "AUCTION"), cancellationToken);

        var result = new List<CatalogVariantInfo>();
        foreach (var item in data.Items ?? new List<VariantItem>())
        {
            if (item.VariantId == Guid.Empty || item.ProductId == Guid.Empty || item.SellerId == Guid.Empty)
                throw InvalidResponse("a variant without an id, product id or seller id");

            result.Add(new CatalogVariantInfo(item.VariantId, item.ProductId, item.SellerId, item.ProductName, item.PrimaryImageUrl));
        }

        return result;
    }

    public async Task<CatalogProductDetail?> GetProductAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(ProductPath + productId, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            var data = await ReadAsync<ProductData>(response, cancellationToken);

            // Catalog trả mã sản phẩm ở trường productId (bản cũ dùng id).
            var returnedId = data.ProductId ?? data.Id ?? Guid.Empty;
            if (returnedId != productId)
                throw InvalidResponse("a product with a different id");

            // Chỉ lấy ảnh đang hoạt động (isActive không có thì coi là đang hoạt động).
            var images = (data.Images ?? new List<ImageItem>())
                .Where(i => !string.IsNullOrWhiteSpace(i.Url) && i.IsActive != false)
                .OrderBy(i => i.SortOrder)
                .Select(i => new CatalogImage(i.Url!, i.SortOrder))
                .ToList();

            return new CatalogProductDetail(returnedId, data.Name, data.Description, SpecificationsToJson(data.Specifications), images);
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            throw Unavailable(ex);
        }
    }

    public async Task LockProductForAuctionAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var (status, code) = await PostProductCommandAsync(productId, "lock-auction", cancellationToken);
        if (status is >= 200 and < 300)
            return;

        throw code switch
        {
            "PRODUCT_ALREADY_IN_AUCTION" => new DomainException(
                "PRODUCT_ALREADY_IN_AUCTION", "The product is already locked in another auction.", 409),
            "PRODUCT_ALREADY_AUCTIONED" => new DomainException(
                "PRODUCT_ALREADY_AUCTIONED", "The product has already been auctioned and cannot be put into auction again.", 409),
            "VAL_INVALID_INPUT" or "PRODUCT_NOT_FOUND" => new DomainException(
                "PRODUCT_NOT_ELIGIBLE", "The product cannot be put into auction.", 422),
            _ => status >= 500
                ? Unavailable(new HttpRequestException($"Catalog answered HTTP {status} to lock-auction."))
                : InvalidResponse($"lock-auction answered HTTP {status} ({code})")
        };
    }

    public async Task UnlockProductFromAuctionAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var (status, code) = await PostProductCommandAsync(productId, "unlock-auction", cancellationToken);
        if (status is >= 200 and < 300)
            return;

        // Sản phẩm không còn InAuction (đã được mở khoá trước đó) hoặc không còn tồn tại: không còn gì để mở khoá.
        if (code is "PRODUCT_NOT_IN_AUCTION" or "PRODUCT_NOT_FOUND")
        {
            _logger.LogInformation("Product {ProductId} was already unlocked in Catalog ({Code}).", productId, code);
            return;
        }

        throw status >= 500
            ? Unavailable(new HttpRequestException($"Catalog answered HTTP {status} to unlock-auction."))
            : InvalidResponse($"unlock-auction answered HTTP {status} ({code})");
    }

    public async Task MarkProductAuctionedAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var (status, code) = await PostProductCommandAsync(productId, "mark-auctioned", cancellationToken);
        if (status is >= 200 and < 300)
            return;

        throw status >= 500
            ? Unavailable(new HttpRequestException($"Catalog answered HTTP {status} to mark-auctioned."))
            : InvalidResponse($"mark-auctioned answered HTTP {status} ({code})");
    }

    public async Task RevertProductAuctionedAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var (status, code) = await PostProductCommandAsync(productId, "revert-auctioned", cancellationToken);
        if (status is >= 200 and < 300)
            return;

        // Nếu sản phẩm không ở trạng thái Auctioned (ví dụ vẫn đang InAuction hoặc đã về Active trước đó), đảm bảo sản phẩm được mở khoá về Active.
        if (code is "PRODUCT_NOT_AUCTIONED" or "PRODUCT_ALREADY_ACTIVE")
        {
            await UnlockProductFromAuctionAsync(productId, cancellationToken);
            return;
        }

        if (code is "PRODUCT_NOT_FOUND")
        {
            _logger.LogInformation("Product {ProductId} was not found in Catalog when reverting auctioned state ({Code}).", productId, code);
            return;
        }

        throw status >= 500
            ? Unavailable(new HttpRequestException($"Catalog answered HTTP {status} to revert-auctioned."))
            : InvalidResponse($"revert-auctioned answered HTTP {status} ({code})");
    }

    public async Task<CatalogProductEligibility> CheckProductAuctionEligibilityAsync(
        Guid productId, bool allowInAuction = false, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync($"{InternalProductPath}{productId}/auction-eligibility", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new CatalogProductEligibility(productId, false, "NotFound", "PRODUCT_NOT_FOUND");

            if ((int)response.StatusCode >= 500)
                throw Unavailable(new HttpRequestException($"Catalog answered HTTP {(int)response.StatusCode} to auction-eligibility."));

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var payload = document.RootElement;

            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("success", out var success))
            {
                if (success.ValueKind == JsonValueKind.False)
                {
                    var errCode = TryGetString(payload, "errorCode") ?? TryGetString(payload, "code") ?? "PRODUCT_NOT_ELIGIBLE";
                    if (allowInAuction && errCode is "PRODUCT_ALREADY_IN_AUCTION" or "PRODUCT_IN_AUCTION")
                        return new CatalogProductEligibility(productId, true, "InAuction", null);

                    return new CatalogProductEligibility(productId, false, null, errCode);
                }

                if (payload.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object)
                    payload = dataElement;
            }

            var status = TryGetString(payload, "status") ?? TryGetString(payload, "productStatus");
            var reason = TryGetString(payload, "reason") ?? TryGetString(payload, "ineligibleReason") ?? TryGetString(payload, "blockReason");
            var isBannedOrDeleted =
                TryGetBool(payload, "isBanned") == true ||
                TryGetBool(payload, "isDeleted") == true ||
                IsForbiddenProductState(status) ||
                IsForbiddenProductReason(reason);

            if (isBannedOrDeleted)
                return new CatalogProductEligibility(productId, false, status, reason ?? "PRODUCT_BANNED");

            var rawEligible = TryGetBool(payload, "isEligible")
                ?? TryGetBool(payload, "eligible")
                ?? TryGetBool(payload, "isPurchasable")
                ?? response.IsSuccessStatusCode;

            if (!rawEligible && allowInAuction)
            {
                var isLockedInAuction =
                    string.Equals(status, "InAuction", StringComparison.OrdinalIgnoreCase) ||
                    reason is "PRODUCT_ALREADY_IN_AUCTION" or "PRODUCT_IN_AUCTION" or "PRODUCT_NOT_ACTIVE";

                if (isLockedInAuction)
                    return new CatalogProductEligibility(productId, true, status ?? "InAuction", null);
            }

            return new CatalogProductEligibility(productId, rawEligible, status, reason);
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            throw Unavailable(ex);
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private static bool? TryGetBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return prop.GetBoolean();
        }
        return null;
    }

    private static bool IsForbiddenProductState(string? status) =>
        status is not null &&
        (status.Equals("Banned", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("Suspended", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("Deleted", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("Rejected", StringComparison.OrdinalIgnoreCase));

    private static bool IsForbiddenProductReason(string? reason) =>
        reason is not null &&
        (reason.Contains("BAN", StringComparison.OrdinalIgnoreCase) ||
         reason.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase) ||
         reason.Contains("DELETE", StringComparison.OrdinalIgnoreCase) ||
         reason.Equals("NOT_FOR_AUCTION", StringComparison.OrdinalIgnoreCase));

    /// <summary>Gọi một lệnh không có body của sản phẩm (lock-auction, unlock-auction, mark-auctioned). Trả mã HTTP và errorCode của Catalog (nếu có).</summary>
    private async Task<(int Status, string? ErrorCode)> PostProductCommandAsync(
        Guid productId, string command, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.PostAsync($"{InternalProductPath}{productId}/{command}", null, cancellationToken);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return (status, null);

            string? code = null;
            try
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("errorCode", out var errorCode)
                    && errorCode.ValueKind == JsonValueKind.String)
                {
                    code = errorCode.GetString();
                }
            }
            catch (JsonException)
            {
                // Catalog không trả JSON: chỉ còn mã HTTP.
            }

            return (status, code);
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            throw Unavailable(ex);
        }
    }

    /// <summary>Catalog trả thông số dạng danh sách (hoặc đối tượng) JSON; đổi về chuỗi JSON để Auction giữ nguyên cấu trúc. Bản cũ trả sẵn chuỗi thì dùng luôn.</summary>
    private static string? SpecificationsToJson(JsonElement? specifications) =>
        specifications switch
        {
            { ValueKind: JsonValueKind.Array or JsonValueKind.Object } value => value.GetRawText(),
            { ValueKind: JsonValueKind.String } value => value.GetString(),
            _ => null
        };

    private async Task<T> PostAsync<T>(string path, VariantQuery body, CancellationToken cancellationToken)
    {
        try
        {
            // The body is serialized with a typed record. An anonymous object sent through PostAsJsonAsync
            // arrived as an empty JSON object.
            using var content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(path, content, cancellationToken);
            return await ReadAsync<T>(response, cancellationToken);
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            throw Unavailable(ex);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var payload = document.RootElement;

        // Wrapped form: { "success": true, "data": {...} }
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("success", out var success))
        {
            if (success.ValueKind != JsonValueKind.True ||
                !payload.TryGetProperty("data", out payload) ||
                payload.ValueKind == JsonValueKind.Null)
                throw new JsonException("Catalog returned an unsuccessful or empty response.");
        }

        return payload.Deserialize<T>(JsonOptions)
            ?? throw new JsonException("Catalog returned an empty response.");
    }

    private static bool IsTransportFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or JsonException or NotSupportedException
        || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private DomainException Unavailable(Exception ex)
    {
        _logger.LogWarning(ex, "Catalog service call failed.");
        return new DomainException("CATALOG_SERVICE_UNAVAILABLE", "The catalog service is unavailable.", 503);
    }

    private DomainException InvalidResponse(string detail)
    {
        _logger.LogWarning("Catalog service returned unusable data: {Detail}.", detail);
        return new DomainException("CATALOG_RESPONSE_INVALID", "The catalog service returned an invalid response.", 502);
    }

    // ---- Catalog response shapes (only the fields Auction needs) ----

    private sealed record VariantQuery(IReadOnlyCollection<Guid> VariantIds, string Purpose);

    private sealed record Envelope<T>(bool Success, T? Data);

    private sealed record ItemsData<T>(List<T>? Items);

    private sealed record CheckItem(
        Guid VariantId, Guid ProductId, string? SaleType, bool IsPurchasable, string? Reason);

    private sealed record VariantItem(
        Guid VariantId, Guid ProductId, Guid SellerId, string? ProductName, string? PrimaryImageUrl);

    private sealed record ImageItem(string? Url, int SortOrder, bool? IsActive = null);

    private sealed record ProductData(
        Guid? ProductId, Guid? Id, string? Name, string? Description, JsonElement? Specifications, List<ImageItem>? Images);
}