namespace Auction.Application.Services.Auction;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using global::Auction.Application.Common;
using global::Auction.Application.Common.Extensions;
using global::Auction.Application.Common.Models;
using global::Auction.Application.Interfaces;
using global::Auction.Domain.Entities;
using global::Auction.Domain.Enums;
using global::Auction.Domain.Exceptions;
using global::Auction.Domain.Rules;
using AuctionEntity = global::Auction.Domain.Entities.Auction;

public sealed class AuctionService : IAuctionService
{
    private const int MaxReasonLength = 1000;
    private const int MaxTitleLength = 500;
    private const string PolicyConfigKey = "DEFAULT_AUCTION_POLICY";

    private readonly IAuctionDbContext _dbContext;
    private readonly ICatalogClient _catalog;
    private readonly IUserClient _users;
    private readonly TimeProvider _clock;

    public AuctionService(IAuctionDbContext dbContext, ICatalogClient catalog, IUserClient users, TimeProvider clock)
    {
        _dbContext = dbContext;
        _catalog = catalog;
        _users = users;
        _clock = clock;
    }

    // ------------------------------------------------------------------ Queries

    public async Task<PagedData<AuctionSummaryDto>> ListActiveAuctionsAsync(
        int page = 1, int pageSize = 20, string? status = null, CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);
        var statusFilter = ParseStatus(status);

        var query = _dbContext.Auctions
            .AsNoTracking()
            .Where(a => !a.IsDeleted && a.AuctionState != null);

        if (statusFilter is { } filter)
            query = query.Where(a => a.AuctionState!.Status == filter);

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(a => a.ScheduledStartAt)
            .ThenBy(a => a.Id)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(a => new
            {
                a.Id,
                a.ProductId,
                a.SkuId,
                a.SellerId,
                a.SnapshotProductTitle,
                a.StartPriceAmount,
                a.ScheduledStartAt,
                a.CreatedAt,
                Status = a.AuctionState!.Status,
                CurrentPriceAmount = a.AuctionState.CurrentPriceAmount,
                BidCount = a.AuctionState.BidCount,
                ScheduledEndAt = a.AuctionState.ScheduledEndAt
            })
            .ToListAsync(cancellationToken);

        // Main images come from Catalog in one batched call. They are display data only.
        var images = await TryGetMainImagesAsync(
            rows.Where(r => r.SkuId.HasValue).Select(r => r.SkuId!.Value).Distinct().ToList(), cancellationToken);

        var items = rows
            .Select(r => new AuctionSummaryDto(
                r.Id, r.ProductId, r.SkuId, r.SellerId, r.SnapshotProductTitle,
                r.SkuId is { } skuId && images.TryGetValue(skuId, out var imageUrl) ? imageUrl : null,
                r.Status, r.StartPriceAmount, r.CurrentPriceAmount, r.BidCount,
                r.ScheduledStartAt, r.ScheduledEndAt, r.CreatedAt))
            .ToList();

        return new PagedData<AuctionSummaryDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    public async Task<AuctionDetailDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.Auctions
            .AsNoTracking()
            .Where(a => a.Id == id && !a.IsDeleted && a.AuctionState != null)
            .Select(a => new
            {
                a.Id,
                a.ProductId,
                a.SkuId,
                a.SellerId,
                a.SnapshotProductTitle,
                a.StartPriceAmount,
                a.BidStepAmount,
                a.ScheduledStartAt,
                a.OriginalEndAt,
                a.MaxExtensionCount,
                a.AntiSnipingExtensionMinutes,
                a.RecreatedFromAuctionId,
                a.CreatedAt,
                Status = a.AuctionState!.Status,
                CurrentPriceAmount = a.AuctionState.CurrentPriceAmount,
                BidCount = a.AuctionState.BidCount,
                ExtensionCount = a.AuctionState.ExtensionCount,
                ScheduledEndAt = a.AuctionState.ScheduledEndAt,
                StartedAt = a.AuctionState.StartedAt,
                EndedAt = a.AuctionState.EndedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
            throw NotFound(id);

        // Description, specifications and images come from Catalog. They are display data only.
        var product = await TryGetProductAsync(row.ProductId, cancellationToken);
        var images = (product?.Images ?? Array.Empty<CatalogImage>())
            .OrderBy(i => i.SortOrder)
            .Select((image, index) => new AuctionImageDto(image.Url, IsPrimary: index == 0))
            .ToList();

        return new AuctionDetailDto(
            row.Id, row.ProductId, row.SkuId, row.SellerId, row.SnapshotProductTitle ?? product?.Name,
            product?.Description, images.FirstOrDefault()?.Url, images, ParseSpecifications(product?.SpecificationsJson),
            row.Status, row.StartPriceAmount, row.BidStepAmount, row.CurrentPriceAmount, row.BidCount,
            row.ScheduledStartAt, row.OriginalEndAt, row.ScheduledEndAt, row.StartedAt, row.EndedAt,
            row.ExtensionCount, row.MaxExtensionCount, row.AntiSnipingExtensionMinutes,
            row.RecreatedFromAuctionId, row.CreatedAt);
    }

    // ------------------------------------------------------------------ Commands

    public async Task<AuctionCreatedResponse> CreateAsync(
        ActorContext actor, CreateAuctionRequest request, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        // 1. Input checks (no database, no other service).
        if (request.ProductId is not { } productId || productId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "Product ID is required.");
        if (request.SkuId is not { } skuId || skuId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "SKU ID is required.");
        AuctionRules.ValidatePrices(request.StartPriceAmount, request.BidStepAmount);
        AuctionRules.ValidateSchedule(request.ScheduledStartAt, request.ScheduledEndAt, now);

        // 2. User service: the caller must be an approved seller with enough reputation.
        await EnsureSellerEligibleAsync(actor.UserId, cancellationToken);

        // 3. One open auction per product, then Catalog: the product must be an active auction item of this seller.
        await EnsureProductHasNoOpenAuctionAsync(productId, cancellationToken);
        var title = await EnsureProductEligibleAsync(actor.UserId, productId, skuId, cancellationToken);
        var policy = await GetPolicyAsync(cancellationToken);

        var created = NewAuction(
            actor.UserId, productId, skuId, title, recreatedFromAuctionId: null,
            request.StartPriceAmount!.Value, request.BidStepAmount!.Value,
            request.ScheduledStartAt!.Value, request.ScheduledEndAt!.Value, policy, now);

        _dbContext.Auctions.Add(created.Auction);
        _dbContext.AuctionStatusHistories.Add(created.History);
        _dbContext.AddAuditLog(
            AuditAction.AuctionCreate, "Auction", created.Auction.Id.ToString(),
            newValues: Snapshot(created.Auction, AuctionStatus.Scheduled),
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        await SaveAsync(productId, cancellationToken);

        var a = created.Auction;
        return new AuctionCreatedResponse(
            a.Id, a.ProductId, a.SkuId, a.SnapshotProductTitle, AuctionStatus.Scheduled,
            a.StartPriceAmount, a.BidStepAmount, a.ScheduledStartAt, a.OriginalEndAt,
            a.AntiSnipingExtensionMinutes, a.MaxExtensionCount, a.CreatedAt);
    }

    public async Task<AuctionUpdatedResponse> UpdateAsync(
        ActorContext actor, Guid auctionId, UpdateAuctionRequest request, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        if (request.StartPriceAmount is null && request.BidStepAmount is null &&
            request.ScheduledStartAt is null && request.ScheduledEndAt is null)
            throw Invalid("VAL_NO_FIELDS_TO_UPDATE", "At least one field must be provided.");

        var auction = await LoadForWriteAsync(auctionId, cancellationToken);
        EnsureOwner(auction, actor, "update");
        var state = auction.AuctionState!;

        AuctionRules.EnsureNotStarted(state.Status, auction.ScheduledStartAt, now,
            "AUCTION_NOT_EDITABLE", "Auction can only be edited before it starts.", 409);

        var startPrice = request.StartPriceAmount ?? auction.StartPriceAmount;
        var bidStep = request.BidStepAmount ?? auction.BidStepAmount;
        var start = request.ScheduledStartAt ?? auction.ScheduledStartAt;
        var end = request.ScheduledEndAt ?? state.ScheduledEndAt;

        // The same checks as when creating the auction.
        AuctionRules.ValidatePrices(startPrice, bidStep);
        AuctionRules.ValidateSchedule(start, end, now);

        var oldValues = Snapshot(auction, state.Status);
        var changed = new List<string>();
        if (startPrice != auction.StartPriceAmount) changed.Add("startPriceAmount");
        if (bidStep != auction.BidStepAmount) changed.Add("bidStepAmount");
        if (start != auction.ScheduledStartAt) changed.Add("scheduledStartAt");
        if (end != state.ScheduledEndAt) changed.Add("scheduledEndAt");

        auction.StartPriceAmount = startPrice;
        auction.BidStepAmount = bidStep;
        auction.ScheduledStartAt = start;
        auction.OriginalEndAt = end;
        auction.UpdatedAt = now;
        auction.UpdatedBy = actor.UserId;

        state.ScheduledEndAt = end;
        state.UpdatedAt = now;
        state.RowVersion += 1;

        _dbContext.AddAuditLog(
            AuditAction.AuctionUpdate, "Auction", auction.Id.ToString(),
            oldValues: oldValues, newValues: Snapshot(auction, state.Status), changedColumns: changed,
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        await SaveAsync(null, cancellationToken);

        return new AuctionUpdatedResponse(
            auction.Id, state.Status, auction.StartPriceAmount, auction.BidStepAmount,
            auction.ScheduledStartAt, state.ScheduledEndAt, now);
    }

    public async Task<AuctionCancelledResponse> CancelAsync(
        ActorContext actor, Guid auctionId, CancelAuctionRequest? request, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        var reason = request?.Reason;
        if (reason is not null)
        {
            reason = reason.Trim();
            if (reason.Length == 0)
                throw Invalid("VAL_INVALID_REASON", "Reason must not be empty.");
            if (reason.Length > MaxReasonLength)
                throw Invalid("VAL_INVALID_REASON", $"Reason must be at most {MaxReasonLength} characters.");
        }

        var auction = await LoadForWriteAsync(auctionId, cancellationToken);
        EnsureOwner(auction, actor, "cancel");
        var state = auction.AuctionState!;

        // A seller can cancel only before the auction starts, even when nobody has bid.
        // Cancelling a running auction is done by an administrator through force-cancel.
        AuctionRules.EnsureNotStarted(state.Status, auction.ScheduledStartAt, now,
            "INVALID_STATE_TRANSITION", "Auction can only be cancelled before it starts.", 422);

        var fromStatus = state.Status;
        var oldValues = Snapshot(auction, fromStatus);

        state.Status = AuctionStatus.Cancelled;
        state.UpdatedAt = now;
        state.RowVersion += 1;
        auction.UpdatedAt = now;
        auction.UpdatedBy = actor.UserId;

        // Release the product so that it can be put into a new auction. The unique index on
        // ProductOpenAuctions.ProductId ignores soft delete, so the row must be removed for real.
        var openAuction = await _dbContext.ProductOpenAuctions
            .FirstOrDefaultAsync(p => p.AuctionId == auction.Id, cancellationToken);
        if (openAuction is not null)
            _dbContext.ProductOpenAuctions.Remove(openAuction);

        _dbContext.AuctionStatusHistories.Add(new AuctionStatusHistory
        {
            Id = Guid.NewGuid(),
            AuctionId = auction.Id,
            FromStatus = fromStatus,
            ToStatus = AuctionStatus.Cancelled,
            Reason = reason,
            ChangedBy = actor.UserId,
            ChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        _dbContext.AddAuditLog(
            AuditAction.AuctionCancel, "Auction", auction.Id.ToString(),
            oldValues: oldValues, newValues: new { status = AuctionStatus.Cancelled.ToString(), reason },
            changedColumns: new[] { "status" },
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        await SaveAsync(null, cancellationToken);

        return new AuctionCancelledResponse(auction.Id, fromStatus, state.Status, reason, now);
    }

    public async Task<AuctionRecreatedResponse> RecreateAsync(
        ActorContext actor, Guid auctionId, RecreateAuctionRequest request, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        AuctionRules.ValidatePrices(request.StartPriceAmount, request.BidStepAmount);
        AuctionRules.ValidateSchedule(request.ScheduledStartAt, request.ScheduledEndAt, now);

        var source = await LoadForWriteAsync(auctionId, cancellationToken);
        EnsureOwner(source, actor, "recreate");

        if (source.AuctionState!.Status != AuctionStatus.Failed)
            throw new BusinessRuleValidationException(
                "INVALID_RECREATE_SOURCE", "Only a failed auction can be recreated.", 422);

        await EnsureSellerEligibleAsync(actor.UserId, cancellationToken);
        await EnsureProductHasNoOpenAuctionAsync(source.ProductId, cancellationToken);

        if (source.SkuId is not { } sourceSkuId)
            throw NotEligible("The auction has no SKU to check.");
        var title = await EnsureProductEligibleAsync(actor.UserId, source.ProductId, sourceSkuId, cancellationToken);
        var policy = await GetPolicyAsync(cancellationToken);

        var created = NewAuction(
            actor.UserId, source.ProductId, source.SkuId, title ?? source.SnapshotProductTitle, source.Id,
            request.StartPriceAmount!.Value, request.BidStepAmount!.Value,
            request.ScheduledStartAt!.Value, request.ScheduledEndAt!.Value, policy, now);

        _dbContext.Auctions.Add(created.Auction);
        _dbContext.AuctionStatusHistories.Add(created.History);
        _dbContext.AddAuditLog(
            AuditAction.AuctionCreate, "Auction", created.Auction.Id.ToString(),
            newValues: Snapshot(created.Auction, AuctionStatus.Scheduled),
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        await SaveAsync(source.ProductId, cancellationToken);

        var a = created.Auction;
        return new AuctionRecreatedResponse(
            a.Id, source.Id, a.ProductId, a.SkuId, AuctionStatus.Scheduled,
            a.StartPriceAmount, a.BidStepAmount, a.ScheduledStartAt, a.OriginalEndAt, a.CreatedAt);
    }

    // ------------------------------------------------------------------ Checks with other services

    /// <summary>User service must confirm the caller is an approved seller with enough reputation.</summary>
    private async Task EnsureSellerEligibleAsync(Guid sellerId, CancellationToken cancellationToken)
    {
        var check = await _users.CheckSellerReputationAsync(sellerId, cancellationToken);
        if (check.Allowed && check.IsApprovedSeller)
            return;

        // The seller is not approved: User sends no specific code for this case.
        if (!check.IsApprovedSeller && string.IsNullOrWhiteSpace(check.FailureReason))
            throw new DomainException("SELLER_NOT_APPROVED", "The seller has not been approved.", 403);

        throw UserEligibilityErrors.FromFailureReason(
            check.FailureReason, "create an auction", "SELLER_NOT_ELIGIBLE", "The seller is not eligible to create an auction.");
    }

    /// <summary>
    /// Asks Catalog if the SKU belongs to the product, is active, is an auction item and belongs to the seller.
    /// Returns the product name to keep with the auction. Throws PRODUCT_NOT_ELIGIBLE otherwise.
    /// </summary>
    private async Task<string?> EnsureProductEligibleAsync(
        Guid sellerId, Guid productId, Guid skuId, CancellationToken cancellationToken)
    {
        var variantIds = new[] { skuId };

        var check = (await _catalog.CheckVariantsForAuctionAsync(variantIds, cancellationToken))
            .FirstOrDefault(c => c.VariantId == skuId);
        if (check is null)
            throw NotEligible("The product variant was not found.");
        if (check.ProductId != productId)
            throw NotEligible("The SKU does not belong to the product.");
        if (check.SaleType is not null && !string.Equals(check.SaleType, "Auction", StringComparison.OrdinalIgnoreCase))
            throw NotEligible(ReasonMessage("NOT_FOR_AUCTION"));
        if (!check.IsPurchasable)
            throw NotEligible(ReasonMessage(check.Reason));

        var variant = (await _catalog.GetVariantsAsync(variantIds, cancellationToken))
            .FirstOrDefault(v => v.VariantId == skuId);
        if (variant is null)
            throw NotEligible("The product variant was not found.");
        if (variant.ProductId != productId)
            throw NotEligible("The SKU does not belong to the product.");
        if (variant.SellerId != sellerId)
            throw NotEligible("The product does not belong to the seller.");

        var name = variant.ProductName?.Trim();
        if (string.IsNullOrEmpty(name))
            return null;
        return name.Length > MaxTitleLength ? name[..MaxTitleLength] : name;
    }

    private static DomainException NotEligible(string message) => new("PRODUCT_NOT_ELIGIBLE", message, 422);

    private static string ReasonMessage(string? reason) => reason switch
    {
        "NOT_FOR_AUCTION" => "The product is not an auction product.",
        "PRODUCT_NOT_ACTIVE" => "The product is not active.",
        "VARIANT_NOT_ACTIVE" => "The product variant is not active.",
        "DELETED" => "The product has been deleted.",
        _ => "The product is not eligible for auction."
    };

    private async Task<Dictionary<Guid, string?>> TryGetMainImagesAsync(
        IReadOnlyCollection<Guid> skuIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string?>();
        if (skuIds.Count == 0)
            return result;

        try
        {
            foreach (var variant in await _catalog.GetVariantsAsync(skuIds, cancellationToken))
                result[variant.VariantId] = variant.PrimaryImageUrl;
        }
        catch (DomainException ex) when (ex.StatusCode >= 500)
        {
            // Images are display data only: the list is still returned without them (the failure is logged by the client).
        }

        return result;
    }

    private async Task<CatalogProductDetail?> TryGetProductAsync(Guid productId, CancellationToken cancellationToken)
    {
        try
        {
            return await _catalog.GetProductAsync(productId, cancellationToken);
        }
        catch (DomainException ex) when (ex.StatusCode >= 500)
        {
            // Description, specifications and images are display data only (the failure is logged by the client).
            return null;
        }
    }

    private static JsonElement? ParseSpecifications(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ Database helpers

    private async Task<AuctionEntity> LoadForWriteAsync(Guid auctionId, CancellationToken cancellationToken)
    {
        var auction = await _dbContext.Auctions
            .Include(a => a.AuctionState)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);

        if (auction?.AuctionState is null)
            throw NotFound(auctionId);

        return auction;
    }

    /// <summary>Only the seller of the auction can change it. Administrators use force-cancel instead.</summary>
    private static void EnsureOwner(AuctionEntity auction, ActorContext actor, string verb)
    {
        if (auction.SellerId != actor.UserId)
            throw new DomainException("FORBIDDEN", $"Only the seller of the auction can {verb} it.", 403);
    }

    private async Task EnsureProductHasNoOpenAuctionAsync(Guid productId, CancellationToken cancellationToken)
    {
        var hasOpenAuction = await _dbContext.ProductOpenAuctions
            .AsNoTracking()
            .AnyAsync(p => p.ProductId == productId, cancellationToken);

        if (hasOpenAuction)
            throw new DomainException("PRODUCT_ALREADY_IN_AUCTION", "The product already has an open auction.", 409);
    }

    private async Task SaveAsync(Guid? productId, CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException(
                "CONCURRENT_UPDATE", "The auction was changed by another request. Reload it and try again.", 409);
        }
        catch (DbUpdateException)
        {
            // Two sellers racing for the same product: the unique index on ProductId rejects the second one.
            if (productId is { } id &&
                await _dbContext.ProductOpenAuctions.AsNoTracking().AnyAsync(p => p.ProductId == id, cancellationToken))
            {
                throw new DomainException("PRODUCT_ALREADY_IN_AUCTION", "The product already has an open auction.", 409);
            }

            throw;
        }
    }

    private async Task<AuctionPolicy> GetPolicyAsync(CancellationToken cancellationToken)
    {
        var config = await _dbContext.AuctionConfigs
            .AsNoTracking()
            .Where(c => c.ConfigKey == PolicyConfigKey && c.IsActive && !c.IsDeleted)
            .OrderByDescending(c => c.Version)
            .FirstOrDefaultAsync(cancellationToken);

        if (config is null)
            throw new InvalidOperationException("Auction policy configuration is missing.");

        using var document = JsonDocument.Parse(config.ConfigValue);
        var root = document.RootElement;

        // Only the anti-sniping settings are read. Reputation settings belong to the User service.
        return new AuctionPolicy(
            ReadInt(root, "antiSnipingExtensionMinutes"),
            ReadInt(root, "maxExtensionCount"));
    }

    private static int ReadInt(JsonElement root, string name)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var number))
        {
            return number;
        }

        throw new InvalidOperationException($"Auction policy configuration is invalid: '{name}' is missing.");
    }

    private static (AuctionEntity Auction, AuctionStatusHistory History) NewAuction(
        Guid sellerId, Guid productId, Guid? skuId, string? title, Guid? recreatedFromAuctionId,
        decimal startPriceAmount, decimal bidStepAmount, DateTimeOffset start, DateTimeOffset end,
        AuctionPolicy policy, DateTimeOffset now)
    {
        var id = Guid.NewGuid();

        var auction = new AuctionEntity
        {
            Id = id,
            ProductId = productId,
            SkuId = skuId,
            SellerId = sellerId,
            SnapshotProductTitle = title,
            RecreatedFromAuctionId = recreatedFromAuctionId,
            StartPriceAmount = startPriceAmount,
            BidStepAmount = bidStepAmount,
            ScheduledStartAt = start,
            OriginalEndAt = end,
            AntiSnipingExtensionMinutes = policy.AntiSnipingExtensionMinutes,
            MaxExtensionCount = policy.MaxExtensionCount,
            CreatedBy = sellerId,
            UpdatedBy = sellerId,
            CreatedAt = now,
            UpdatedAt = now
        };

        auction.AuctionState = new AuctionState
        {
            Id = Guid.NewGuid(),
            AuctionId = id,
            Status = AuctionStatus.Scheduled,
            ScheduledEndAt = end,
            RowVersion = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        auction.ProductOpenAuction = new ProductOpenAuction
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            AuctionId = id,
            CreatedAt = now,
            UpdatedAt = now
        };

        var history = new AuctionStatusHistory
        {
            Id = Guid.NewGuid(),
            AuctionId = id,
            FromStatus = null,
            ToStatus = AuctionStatus.Scheduled,
            ChangedBy = sellerId,
            ChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        return (auction, history);
    }

    private static object Snapshot(AuctionEntity auction, AuctionStatus status) => new
    {
        status = status.ToString(),
        productId = auction.ProductId,
        skuId = auction.SkuId,
        startPriceAmount = auction.StartPriceAmount,
        bidStepAmount = auction.BidStepAmount,
        scheduledStartAt = auction.ScheduledStartAt,
        scheduledEndAt = auction.AuctionState?.ScheduledEndAt ?? auction.OriginalEndAt,
        recreatedFromAuctionId = auction.RecreatedFromAuctionId
    };

    private static DomainException Invalid(string errorCode, string message) => new(errorCode, message, 400);

    private static EntityNotFoundException NotFound(Guid auctionId) => new("AUCTION_NOT_FOUND", "Auction", auctionId);

    private static AuctionStatus? ParseStatus(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Names only: "1" is not accepted as a status.
        var trimmed = text.Trim();
        if (!int.TryParse(trimmed, out _) &&
            Enum.TryParse<AuctionStatus>(trimmed, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw Invalid("VAL_INVALID_STATUS",
            "Status must be one of Draft, Scheduled, Running, Ended, Settled, Failed or Cancelled.");
    }

    private sealed record AuctionPolicy(int AntiSnipingExtensionMinutes, int MaxExtensionCount);
}