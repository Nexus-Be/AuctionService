namespace Auction.Application.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Synchronous (REST) calls to the Catalog service. Implementations throw a DomainException with
/// status 503 when Catalog cannot be reached and 502 when it returns data that is not usable.
/// </summary>
public interface ICatalogClient
{
    /// <summary>Asks Catalog whether the variants (SKUs) can be sold by auction. Unknown variants are missing from the result.</summary>
    Task<IReadOnlyList<CatalogVariantCheck>> CheckVariantsForAuctionAsync(
        IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken = default);

    /// <summary>Gets the owner, product name and main image of the variants. Unknown variants are missing from the result.</summary>
    Task<IReadOnlyList<CatalogVariantInfo>> GetVariantsAsync(
        IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken = default);

    /// <summary>Gets the display data of a product. Returns null when the product does not exist.</summary>
    Task<CatalogProductDetail?> GetProductAsync(Guid productId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks the product for an auction (Catalog: Active to InAuction). Called synchronously when an auction is created:
    /// no lock means no auction. Throws PRODUCT_ALREADY_IN_AUCTION or PRODUCT_ALREADY_AUCTIONED (409), PRODUCT_NOT_ELIGIBLE (422)
    /// or CATALOG_SERVICE_UNAVAILABLE (503).
    /// </summary>
    Task LockProductForAuctionAsync(Guid productId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unlocks the product (Catalog: InAuction to Active). A product that is no longer InAuction counts as success, so calling it again is safe.
    /// Throws 503 for a temporary failure (worth retrying) and 502 for an answer that will not change by retrying.
    /// </summary>
    Task UnlockProductFromAuctionAsync(Guid productId, CancellationToken cancellationToken = default);

    /// <summary>Marks the product as sold by auction (Catalog: Auctioned, permanent). Idempotent. Throws 503 (temporary) or 502 (permanent).</summary>
    Task MarkProductAuctionedAsync(Guid productId, CancellationToken cancellationToken = default);
}

public sealed record CatalogVariantCheck(
    Guid VariantId,
    Guid ProductId,
    string? SaleType,
    bool IsPurchasable,
    string? Reason);

public sealed record CatalogVariantInfo(
    Guid VariantId,
    Guid ProductId,
    Guid SellerId,
    string? ProductName,
    string? PrimaryImageUrl);

public sealed record CatalogImage(string Url, int SortOrder);

public sealed record CatalogProductDetail(
    Guid ProductId,
    string? Name,
    string? Description,
    string? SpecificationsJson,
    IReadOnlyList<CatalogImage> Images);