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
