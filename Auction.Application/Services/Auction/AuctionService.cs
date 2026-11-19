namespace Auction.Application.Services.Auction;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using global::Auction.Application.Common;
using global::Auction.Application.Common.Extensions;
using global::Auction.Application.Common.Models;
using global::Auction.Application.Interfaces;
using global::Auction.Application.Services.Deposit;
using global::Auction.Domain.Entities;
using global::Auction.Domain.Enums;
using global::Auction.Domain.Exceptions;
using global::Auction.Domain.Rules;
using AuctionEntity = global::Auction.Domain.Entities.Auction;

public sealed class AuctionService : IAuctionService
{
    private const int MaxReasonLength = 1000;
    private const int MaxTitleLength = 500;
    private const int MaxOfferCandidates = 5;
    private const string PolicyConfigKey = "DEFAULT_AUCTION_POLICY";
    private static readonly TimeSpan SettlementIdempotencyLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan WinnerPaymentWindow = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAuctionDbContext _dbContext;
    private readonly ICatalogClient _catalog;
    private readonly IUserClient _users;
    private readonly IWalletClient _wallet;
    private readonly TimeProvider _clock;
    private readonly IAuctionNotifier _notifier;
    private readonly INotificationClient _notifications;
    private readonly ICatalogSync _catalogSync;
    private readonly ICommerceSync _commerceSync;
    private readonly IDepositService _deposits;

    /// <summary>Khởi tạo service với DbContext, các client gọi Catalog, User và Wallet, đồng hồ hệ thống, bộ thông báo realtime và service cọc.</summary>
    public AuctionService(
        IAuctionDbContext dbContext, ICatalogClient catalog, IUserClient users, IWalletClient wallet, TimeProvider clock, IAuctionNotifier notifier,
        INotificationClient notifications, ICatalogSync catalogSync, ICommerceSync commerceSync, IDepositService deposits)
    {
        _dbContext = dbContext;
        _catalog = catalog;
        _users = users;
        _wallet = wallet;
        _clock = clock;
        _notifier = notifier;
        _notifications = notifications;
        _catalogSync = catalogSync;
        _commerceSync = commerceSync;
        _deposits = deposits;
    }

    // ------------------------------------------------------------------ Queries

    /// <summary>Lấy danh sách phiên đấu giá có phân trang, tìm kiếm, lọc và sắp xếp. Dùng cho trang danh sách công khai.</summary>
    public Task<PagedData<AuctionSummaryDto>> ListActiveAuctionsAsync(
        int page = 1, int pageSize = 20, AuctionListFilter? filter = null, CancellationToken cancellationToken = default)
        => ListAuctionsAsync(page, pageSize, filter ?? new AuctionListFilter(), cancellationToken);

    /// <summary>Lấy danh sách phiên của riêng một seller (có phân trang, lọc theo trạng thái). Dùng cho trang quản lý của seller.</summary>
    public Task<PagedData<AuctionSummaryDto>> ListSellerAuctionsAsync(
        Guid sellerId, int page = 1, int pageSize = 20, string? status = null, CancellationToken cancellationToken = default)
        => ListAuctionsAsync(page, pageSize, new AuctionListFilter(Status: status, SellerId: sellerId), cancellationToken);

    /// <summary>Lấy danh sách phiên đấu giá mà caller đã tham gia (gom theo từng phiên, tránh lặp theo từng lượt bid).</summary>
    public async Task<PagedData<MyParticipatedAuctionDto>> ListMyAuctionsAsync(
        Guid userId, int page = 1, int pageSize = 20, string? participationState = null, string? status = null,
        CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);
        var statusFilter = ParseStatus(status);
        var stateFilter = participationState?.Trim().ToLowerInvariant();

        if (!string.IsNullOrEmpty(stateFilter)
            && stateFilter is not ("winning" or "outbid" or "won" or "lost"))
        {
            throw Invalid("VAL_INVALID_PARTICIPATION_STATE", "Participation state must be one of winning, outbid, won or lost.");
        }

        var query = _dbContext.Auctions
            .AsNoTracking()
            .Where(a => !a.IsDeleted && a.AuctionState != null
                && (_dbContext.Bids.Any(b => b.AuctionId == a.Id && b.BidderId == userId && !b.IsDeleted && b.BidInvalidation == null)
                    || _dbContext.AuctionDeposits.Any(d => d.AuctionId == a.Id && d.BidderId == userId && !d.IsDeleted)));

        if (statusFilter is { } sFilter)
            query = query.Where(a => a.AuctionState!.Status == sFilter);

        if (stateFilter == "winning")
        {
            query = query.Where(a => a.AuctionState!.Status == AuctionStatus.Running && a.AuctionState.CurrentWinnerId == userId);
        }
        else if (stateFilter == "outbid")
        {
            query = query.Where(a => a.AuctionState!.Status == AuctionStatus.Running
                && a.AuctionState.CurrentWinnerId != userId
                && _dbContext.Bids.Any(b => b.AuctionId == a.Id && b.BidderId == userId && !b.IsDeleted && b.BidInvalidation == null));
        }
        else if (stateFilter == "won")
        {
            query = query.Where(a => (a.AuctionState!.Status == AuctionStatus.Ended || a.AuctionState.Status == AuctionStatus.Settled)
                && (a.AuctionResult != null ? a.AuctionResult.WinnerId == userId : a.AuctionState.CurrentWinnerId == userId));
        }
        else if (stateFilter == "lost")
        {
            query = query.Where(a => (a.AuctionState!.Status == AuctionStatus.Ended || a.AuctionState.Status == AuctionStatus.Settled
                    || a.AuctionState.Status == AuctionStatus.Failed || a.AuctionState.Status == AuctionStatus.Cancelled)
                && !(a.AuctionResult != null && a.AuctionResult.WinnerId == userId)
                && a.AuctionState.CurrentWinnerId != userId);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(a => a.AuctionState!.ScheduledEndAt)
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
                Status = a.AuctionState!.Status,
                CurrentPriceAmount = a.AuctionState.CurrentPriceAmount,
                CurrentWinnerId = a.AuctionState.CurrentWinnerId,
                BidCount = a.AuctionState.BidCount,
                ScheduledEndAt = a.AuctionState.ScheduledEndAt,
                ResultWinnerId = a.AuctionResult != null ? a.AuctionResult.WinnerId : (Guid?)null,
                OrderId = a.AuctionSettlement != null && a.AuctionSettlement.WinnerId == userId
                    ? a.AuctionSettlement.OrderId
                    : (Guid?)null,
                MyHighestBidAmount = _dbContext.Bids
                    .Where(b => b.AuctionId == a.Id && b.BidderId == userId && !b.IsDeleted && b.BidInvalidation == null)
                    .Select(b => (decimal?)b.Amount)
                    .Max(),
                MyDepositStatus = _dbContext.AuctionDeposits
                    .Where(d => d.AuctionId == a.Id && d.BidderId == userId && !d.IsDeleted)
                    .Select(d => (DepositStatus?)d.Status)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var images = await TryGetMainImagesAsync(
            rows.Where(r => r.SkuId.HasValue).Select(r => r.SkuId!.Value).Distinct().ToList(), cancellationToken);

        var items = rows.Select(r =>
        {
            var effectiveWinnerId = r.ResultWinnerId ?? r.CurrentWinnerId;
            var isWinning = r.Status switch
            {
                AuctionStatus.Running => r.CurrentWinnerId == userId,
                AuctionStatus.Ended or AuctionStatus.Settled => effectiveWinnerId == userId,
                _ => false
            };

            var computedState = r.Status switch
            {
                AuctionStatus.Running when isWinning => "Winning",
                AuctionStatus.Running when r.MyHighestBidAmount.HasValue => "Outbid",
                AuctionStatus.Ended or AuctionStatus.Settled when isWinning => "Won",
                AuctionStatus.Ended or AuctionStatus.Settled or AuctionStatus.Failed or AuctionStatus.Cancelled => "Lost",
                _ => "Pending"
            };

            return new MyParticipatedAuctionDto(
                r.Id,
                r.ProductId,
                r.SkuId,
                r.SellerId,
                r.SnapshotProductTitle,
                r.SkuId is { } skuId && images.TryGetValue(skuId, out var imageUrl) ? imageUrl : null,
                r.Status,
                r.StartPriceAmount,
                r.CurrentPriceAmount,
                r.MyHighestBidAmount,
                isWinning,
                computedState,
                r.MyDepositStatus,
                r.OrderId,
                r.BidCount,
                r.ScheduledStartAt,
                r.ScheduledEndAt);
        }).ToList();

        return new PagedData<MyParticipatedAuctionDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    /// <summary>Admin/Staff xem danh sách phiên đấu giá toàn sàn với các bộ lọc phục vụ vận hành.</summary>
    public async Task<PagedData<AdminAuctionSummaryDto>> ListAdminAuctionsAsync(
        int page = 1, int pageSize = 20, string? status = null, Guid? sellerId = null, Guid? productId = null,
        bool? hasAttentionDeposit = null, bool? overdueSettlement = null, DateTimeOffset? fromDate = null, DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);
        var statusFilter = ParseStatus(status);
        var now = _clock.GetUtcNow();

        var query = _dbContext.Auctions
            .AsNoTracking()
            .Where(a => !a.IsDeleted && a.AuctionState != null);

        if (statusFilter is { } filter)
            query = query.Where(a => a.AuctionState!.Status == filter);
        if (sellerId is { } sId && sId != Guid.Empty)
            query = query.Where(a => a.SellerId == sId);
        if (productId is { } pId && pId != Guid.Empty)
            query = query.Where(a => a.ProductId == pId);
        if (fromDate is { } from)
            query = query.Where(a => a.CreatedAt >= from);
        if (toDate is { } to)
            query = query.Where(a => a.CreatedAt <= to);

        if (hasAttentionDeposit is true)
        {
            query = query.Where(a => _dbContext.AuctionDeposits.Any(
                d => d.AuctionId == a.Id && !d.IsDeleted && (d.NeedsAttention || d.ActionFailureCount > 10)));
        }
        else if (hasAttentionDeposit is false)
        {
            query = query.Where(a => !_dbContext.AuctionDeposits.Any(
                d => d.AuctionId == a.Id && !d.IsDeleted && (d.NeedsAttention || d.ActionFailureCount > 10)));
        }

        if (overdueSettlement is true)
        {
            query = query.Where(a => a.AuctionState!.Status == AuctionStatus.Ended
                && a.AuctionSettlement != null
                && a.AuctionSettlement.Status == AuctionSettlementStatus.PendingPayment
                && a.AuctionSettlement.PaymentDeadlineAt <= now);
        }
        else if (overdueSettlement is false)
        {
            query = query.Where(a => !(a.AuctionState!.Status == AuctionStatus.Ended
                && a.AuctionSettlement != null
                && a.AuctionSettlement.Status == AuctionSettlementStatus.PendingPayment
                && a.AuctionSettlement.PaymentDeadlineAt <= now));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(a => new AdminAuctionSummaryDto(
                a.Id,
                a.ProductId,
                a.SkuId,
                a.SellerId,
                a.SnapshotProductTitle,
                a.AuctionState!.Status,
                a.StartPriceAmount,
                a.AuctionState.CurrentPriceAmount,
                a.AuctionState.BidCount,
                a.ScheduledStartAt,
                a.AuctionState.ScheduledEndAt,
                _dbContext.AuctionDeposits.Any(d => d.AuctionId == a.Id && !d.IsDeleted && (d.NeedsAttention || d.ActionFailureCount > 10)),
                a.AuctionState.Status == AuctionStatus.Ended
                    && a.AuctionSettlement != null
                    && a.AuctionSettlement.Status == AuctionSettlementStatus.PendingPayment
                    && a.AuctionSettlement.PaymentDeadlineAt <= now,
                a.NeedsAttention,
                a.CreatedAt))
            .ToListAsync(cancellationToken);

        return new PagedData<AdminAuctionSummaryDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    /// <summary>Admin/Staff xem danh sách các phiên đang bị kẹt lỗi cọc, đồng bộ hoặc quá hạn settlement.</summary>
    public async Task<PagedData<AdminAuctionSummaryDto>> ListAttentionRequiredAuctionsAsync(
        int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);
        var now = _clock.GetUtcNow();

        var query = _dbContext.Auctions
            .AsNoTracking()
            .Where(a => !a.IsDeleted && a.AuctionState != null
                && (a.NeedsAttention
                    || _dbContext.AuctionDeposits.Any(d => d.AuctionId == a.Id && !d.IsDeleted && (d.NeedsAttention || d.ActionFailureCount > 10))
                    || (a.AuctionState.Status == AuctionStatus.Ended
                        && a.AuctionSettlement != null
                        && a.AuctionSettlement.Status == AuctionSettlementStatus.PendingPayment
                        && a.AuctionSettlement.PaymentDeadlineAt <= now)));

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(a => a.UpdatedAt)
            .ThenBy(a => a.Id)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(a => new AdminAuctionSummaryDto(
                a.Id,
                a.ProductId,
                a.SkuId,
                a.SellerId,
                a.SnapshotProductTitle,
                a.AuctionState!.Status,
                a.StartPriceAmount,
                a.AuctionState.CurrentPriceAmount,
                a.AuctionState.BidCount,
                a.ScheduledStartAt,
                a.AuctionState.ScheduledEndAt,
                _dbContext.AuctionDeposits.Any(d => d.AuctionId == a.Id && !d.IsDeleted && (d.NeedsAttention || d.ActionFailureCount > 10)),
                a.AuctionState.Status == AuctionStatus.Ended
                    && a.AuctionSettlement != null
                    && a.AuctionSettlement.Status == AuctionSettlementStatus.PendingPayment
                    && a.AuctionSettlement.PaymentDeadlineAt <= now,
                a.NeedsAttention,
                a.CreatedAt))
            .ToListAsync(cancellationToken);

        return new PagedData<AdminAuctionSummaryDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    /// <summary>Admin/Staff kích hoạt chạy lại đồng bộ Catalog / Commerce / User cho một phiên bị kẹt.</summary>
    public async Task<AdminSyncRetryResponse> RetrySyncAsync(
        ActorContext actor, Guid auctionId, CancellationToken cancellationToken = default)
    {
        if (auctionId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "Auction ID is required.");

        var auction = await _dbContext.Auctions
            .Include(a => a.AuctionState)
            .Include(a => a.AuctionSettlement)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (auction?.AuctionState is null)
            throw NotFound(auctionId);

        var now = _clock.GetUtcNow();
        var resetFlags = 0;

        if (auction.NeedsAttention)
        {
            auction.NeedsAttention = false;
            resetFlags++;
        }

        if (auction.ViolationState == BackgroundSyncState.Pending
            || auction.StreakResetState == BackgroundSyncState.Pending
            || auction.StockReleaseState == BackgroundSyncState.Pending)
        {
            auction.NextRetryAt = now;
            auction.LastError = null;
            auction.UpdatedAt = now;
            resetFlags++;
        }

        var pushedCommerce = false;
        if (auction.AuctionState.Status == AuctionStatus.Ended
            && auction.AuctionSettlement is { Status: AuctionSettlementStatus.PendingPayment, OrderId: null })
        {
            pushedCommerce = true;
        }

        var pushedCatalogUnlock = auction.StockReleaseState == BackgroundSyncState.Pending
            && auction.AuctionState.Status is AuctionStatus.Failed or AuctionStatus.Cancelled;

        if (resetFlags > 0)
        {
            _dbContext.AddAuditLog(
                AuditAction.AuctionUpdate, "Auction", auctionId.ToString(),
                oldValues: null,
                newValues: new { action = "AdminSyncRetry", pushedCommerce, pushedCatalogUnlock },
                userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);
            await SaveAsync(null, cancellationToken);
        }

        if (pushedCommerce)
            _commerceSync.PushSettlement(auctionId);
        if (pushedCatalogUnlock)
            _catalogSync.UnlockProduct(auction.ProductId, auctionId);

        return new AdminSyncRetryResponse(auctionId, pushedCommerce, pushedCatalogUnlock, resetFlags, now);
    }

    /// <summary>Admin/Staff vô hiệu hoá một lượt bid gian lận khi phiên đang chạy và tính lại giá dẫn đầu.</summary>
    public async Task<InvalidateBidResponse> InvalidateBidAsync(
        ActorContext actor, Guid auctionId, Guid bidId, InvalidateBidRequest? request, CancellationToken cancellationToken = default)
    {
        if (auctionId == Guid.Empty || bidId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "Auction ID and Bid ID are required.");

        var now = _clock.GetUtcNow();
        var auction = await _dbContext.Auctions
            .Include(a => a.AuctionState)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (auction?.AuctionState is null)
            throw NotFound(auctionId);

        var state = auction.AuctionState;
        if (state.Status != AuctionStatus.Running)
            throw new BusinessRuleValidationException(
                "INVALID_STATE_TRANSITION", "Only bids in a running auction can be invalidated.", 422);

        var bid = await _dbContext.Bids
            .Include(b => b.BidInvalidation)
            .FirstOrDefaultAsync(b => b.Id == bidId && b.AuctionId == auctionId && !b.IsDeleted, cancellationToken);
        if (bid is null)
            throw new EntityNotFoundException("BID_NOT_FOUND", "Bid", bidId);

        if (bid.BidInvalidation is not null)
        {
            return new InvalidateBidResponse(
                auctionId, bidId, bid.BidInvalidation.Reason,
                state.CurrentPriceAmount, state.CurrentWinnerId, state.BidCount, bid.BidInvalidation.InvalidatedAt);
        }

        var reason = BidInvalidationReason.BidderBanned;
        var note = string.IsNullOrWhiteSpace(request?.Reason) ? "Invalidated by admin/staff." : request.Reason.Trim();

        _dbContext.BidInvalidations.Add(new BidInvalidation
        {
            Id = Guid.NewGuid(),
            BidId = bidId,
            Reason = reason,
            InvalidatedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        var histories = await _dbContext.BidHistories
            .Where(h => h.BidId == bidId && !h.IsDeleted)
            .ToListAsync(cancellationToken);
        foreach (var history in histories)
        {
            history.IsInvalidated = true;
            history.UpdatedAt = now;
        }

        var remainingTop = await _dbContext.Bids
            .AsNoTracking()
            .Where(b => b.AuctionId == auctionId && b.Id != bidId && !b.IsDeleted && b.BidInvalidation == null)
            .OrderByDescending(b => b.Amount)
            .ThenBy(b => b.BidSeq)
            .Select(b => new { b.BidderId, b.Amount })
            .FirstOrDefaultAsync(cancellationToken);

        var remainingCount = await _dbContext.Bids
            .AsNoTracking()
            .CountAsync(b => b.AuctionId == auctionId && b.Id != bidId && !b.IsDeleted && b.BidInvalidation == null, cancellationToken);

        var oldValues = new
        {
            currentPriceAmount = state.CurrentPriceAmount,
            currentWinnerId = state.CurrentWinnerId,
            bidCount = state.BidCount
        };

        state.CurrentPriceAmount = remainingTop?.Amount;
        state.CurrentWinnerId = remainingTop?.BidderId;
        state.BidCount = remainingCount;
        state.UpdatedAt = now;
        state.RowVersion += 1;

        _dbContext.AddAuditLog(
            AuditAction.BidInvalidate, "Bid", bidId.ToString(),
            oldValues: oldValues,
            newValues: new
            {
                auctionId,
                bidId,
                reason = reason.ToString(),
                note,
                currentPriceAmount = state.CurrentPriceAmount,
                currentWinnerId = state.CurrentWinnerId,
                bidCount = state.BidCount
            },
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        await SaveAsync(null, cancellationToken);

        await _notifier.AuctionStatusChangedAsync(
            auctionId, new AuctionStatusChangedMessage(auctionId, state.Status.ToString(), now), CancellationToken.None);

        return new InvalidateBidResponse(
            auctionId, bidId, reason, state.CurrentPriceAmount, state.CurrentWinnerId, state.BidCount, now);
    }

    // ------------------------------------------------------------------ Watchlist & Reports

    /// <summary>Buyer yêu thích / theo dõi phiên đấu giá và chọn có nhận thông báo bid mới hay không.</summary>
    public async Task<WatchAuctionResponse> WatchAuctionAsync(
        ActorContext actor, Guid auctionId, WatchAuctionRequest? request, CancellationToken cancellationToken = default)
    {
        if (auctionId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "Auction ID is required.");

        var auction = await _dbContext.Auctions
            .AsNoTracking()
            .Include(a => a.AuctionState)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (auction?.AuctionState is null)
            throw NotFound(auctionId);

        if (auction.SellerId == actor.UserId)
            throw new DomainException("SELLER_CANNOT_WATCH", "Sellers cannot add their own auction to watchlist.", 403);

        var notify = request?.NotifyOnNewBid ?? true;
        var now = _clock.GetUtcNow();

        var existing = await _dbContext.AuctionWatches
            .FirstOrDefaultAsync(w => w.AuctionId == auctionId && w.UserId == actor.UserId, cancellationToken);

        if (existing is null)
        {
            _dbContext.AuctionWatches.Add(new AuctionWatch
            {
                Id = Guid.NewGuid(),
                AuctionId = auctionId,
                UserId = actor.UserId,
                NotifyOnNewBid = notify,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            existing.IsDeleted = false;
            existing.DeletedAt = null;
            existing.NotifyOnNewBid = notify;
            existing.UpdatedAt = now;
        }

        await SaveAsync(null, cancellationToken);
        return new WatchAuctionResponse(auctionId, IsWatching: true, notify, now);
    }

    /// <summary>Buyer bỏ yêu thích / ngừng theo dõi phiên đấu giá.</summary>
    public async Task<WatchAuctionResponse> UnwatchAuctionAsync(
        ActorContext actor, Guid auctionId, CancellationToken cancellationToken = default)
    {
        if (auctionId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "Auction ID is required.");

        var now = _clock.GetUtcNow();
        var existing = await _dbContext.AuctionWatches
            .FirstOrDefaultAsync(w => w.AuctionId == auctionId && w.UserId == actor.UserId && !w.IsDeleted, cancellationToken);

        if (existing is not null)
        {
            existing.IsDeleted = true;
            existing.DeletedAt = now;
            existing.UpdatedAt = now;
            await SaveAsync(null, cancellationToken);
        }

        return new WatchAuctionResponse(auctionId, IsWatching: false, NotifyOnNewBid: false, now);
    }

    /// <summary>Buyer xem danh sách các phiên đấu giá đã yêu thích / theo dõi.</summary>
    public async Task<PagedData<MyWatchlistItemDto>> ListMyWatchlistAsync(
        Guid userId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);

        var query = _dbContext.AuctionWatches
            .AsNoTracking()
            .Where(w => w.UserId == userId && !w.IsDeleted && w.Auction != null && !w.Auction.IsDeleted && w.Auction.AuctionState != null);

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(w => w.UpdatedAt)
            .ThenBy(w => w.Id)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(w => new
            {
                w.AuctionId,
                w.Auction!.ProductId,
                w.Auction.SkuId,
                w.Auction.SellerId,
                w.Auction.SnapshotProductTitle,
                Status = w.Auction.AuctionState!.Status,
                w.Auction.StartPriceAmount,
                CurrentPriceAmount = w.Auction.AuctionState.CurrentPriceAmount,
                BidCount = w.Auction.AuctionState.BidCount,
                w.NotifyOnNewBid,
                w.Auction.ScheduledStartAt,
                ScheduledEndAt = w.Auction.AuctionState.ScheduledEndAt,
                WatchedAt = w.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        var images = await TryGetMainImagesAsync(
            rows.Where(r => r.SkuId.HasValue).Select(r => r.SkuId!.Value).Distinct().ToList(), cancellationToken);

        var items = rows.Select(r => new MyWatchlistItemDto(
            r.AuctionId,
            r.ProductId,
            r.SkuId,
            r.SellerId,
            r.SnapshotProductTitle,
            r.SkuId is { } skuId && images.TryGetValue(skuId, out var imageUrl) ? imageUrl : null,
            r.Status,
            r.StartPriceAmount,
            r.CurrentPriceAmount,
            r.BidCount,
            r.NotifyOnNewBid,
            r.ScheduledStartAt,
            r.ScheduledEndAt,
            r.WatchedAt)).ToList();

        return new PagedData<MyWatchlistItemDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    /// <summary>Buyer gửi báo cáo vi phạm cho một phiên đấu giá đang Scheduled hoặc Running.</summary>
    public async Task<CreateAuctionReportResponse> CreateReportAsync(
        ActorContext actor, Guid auctionId, CreateAuctionReportRequest request, CancellationToken cancellationToken = default)
    {
        if (auctionId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "Auction ID is required.");
        if (request.Reason is null || !Enum.IsDefined(request.Reason.Value))
            throw Invalid("VAL_INVALID_REASON", "A valid report reason is required.");

        var description = request.Description?.Trim();
        if (string.IsNullOrEmpty(description) || description.Length < 10)
            throw Invalid("VAL_INVALID_DESCRIPTION", "Report description must be at least 10 characters.");
        if (description.Length > 1000)
            throw Invalid("VAL_INVALID_DESCRIPTION", "Report description must be at most 1000 characters.");

        var auction = await _dbContext.Auctions
            .AsNoTracking()
            .Include(a => a.AuctionState)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (auction?.AuctionState is null)
            throw NotFound(auctionId);

        if (auction.SellerId == actor.UserId)
            throw new DomainException("SELLER_CANNOT_REPORT", "Sellers cannot report their own auction.", 403);

        if (auction.AuctionState.Status is not (AuctionStatus.Scheduled or AuctionStatus.Running))
            throw new BusinessRuleValidationException(
                "INVALID_STATE_TRANSITION", "Only scheduled or running auctions can be reported.", 422);

        var alreadyReported = await _dbContext.AuctionReports
            .AsNoTracking()
            .AnyAsync(r => r.AuctionId == auctionId && r.ReporterId == actor.UserId && !r.IsDeleted, cancellationToken);
        if (alreadyReported)
            throw new DomainException("REPORT_ALREADY_EXISTS", "You have already reported this auction.", 409);

        var now = _clock.GetUtcNow();
        var report = new AuctionReport
        {
            Id = Guid.NewGuid(),
            AuctionId = auctionId,
            ReporterId = actor.UserId,
            Reason = request.Reason.Value,
            Description = description,
            Status = AuctionReportStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.AuctionReports.Add(report);
        _dbContext.AddAuditLog(
            AuditAction.AuctionReportCreate, "AuctionReport", report.Id.ToString(),
            oldValues: null,
            newValues: new { auctionId, reason = report.Reason.ToString(), status = report.Status.ToString() },
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        await SaveAsync(null, cancellationToken);
        return new CreateAuctionReportResponse(report.Id, auctionId, report.Reason, report.Status, now);
    }

    /// <summary>Buyer xem danh sách các báo cáo mình đã gửi và phản hồi từ Admin.</summary>
    public async Task<PagedData<MyAuctionReportDto>> ListMyReportsAsync(
        Guid userId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);

        var query = _dbContext.AuctionReports
            .AsNoTracking()
            .Where(r => r.ReporterId == userId && !r.IsDeleted);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(r => new MyAuctionReportDto(
                r.Id,
                r.AuctionId,
                r.Auction != null ? r.Auction.SnapshotProductTitle : null,
                r.Auction != null && r.Auction.AuctionState != null ? r.Auction.AuctionState.Status : null,
                r.Reason,
                r.Description,
                r.Status,
                r.AdminNote,
                r.CreatedAt,
                r.ReviewedAt))
            .ToListAsync(cancellationToken);

        return new PagedData<MyAuctionReportDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    /// <summary>Admin/Staff xem danh sách báo cáo phiên đấu giá trên toàn sàn.</summary>
    public async Task<PagedData<AdminAuctionReportDto>> ListAdminReportsAsync(
        int page = 1, int pageSize = 20, string? status = null, Guid? auctionId = null, CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);

        AuctionReportStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            var trimmed = status.Trim();
            if (int.TryParse(trimmed, out _)
                || !Enum.TryParse<AuctionReportStatus>(trimmed, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                throw Invalid("VAL_INVALID_STATUS", "Report status must be one of Pending, ActionTaken or Dismissed.");
            }
            statusFilter = parsed;
        }

        var query = _dbContext.AuctionReports
            .AsNoTracking()
            .Where(r => !r.IsDeleted);

        if (statusFilter is { } filter)
            query = query.Where(r => r.Status == filter);
        if (auctionId is { } aId && aId != Guid.Empty)
            query = query.Where(r => r.AuctionId == aId);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(r => r.Status)
            .ThenByDescending(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(r => new AdminAuctionReportDto(
                r.Id,
                r.AuctionId,
                r.Auction != null ? r.Auction.SnapshotProductTitle : null,
                r.Auction != null && r.Auction.AuctionState != null ? r.Auction.AuctionState.Status : null,
                r.Auction != null ? r.Auction.SellerId : Guid.Empty,
                r.ReporterId,
                r.Reason,
                r.Description,
                r.Status,
                r.AdminNote,
                r.ReviewedBy,
                r.CreatedAt,
                r.ReviewedAt))
            .ToListAsync(cancellationToken);

        return new PagedData<AdminAuctionReportDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    /// <summary>Admin/Staff xác nhận phiên không có sai phạm: đóng các báo cáo Pending sang Dismissed và gửi phản hồi cho người tố cáo.</summary>
    public async Task<DismissAuctionReportsResponse> DismissReportsAsync(
        ActorContext actor, Guid auctionId, DismissAuctionReportsRequest? request, CancellationToken cancellationToken = default)
    {
        if (auctionId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "Auction ID is required.");

        var auction = await _dbContext.Auctions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (auction is null)
            throw NotFound(auctionId);

        var now = _clock.GetUtcNow();
        var note = string.IsNullOrWhiteSpace(request?.Note)
            ? "No policy violation was found after review."
            : request.Note.Trim();
        if (note.Length > 1000)
            throw Invalid("VAL_INVALID_REASON", "Note must be at most 1000 characters.");

        var pendingReports = await _dbContext.AuctionReports
            .Where(r => r.AuctionId == auctionId && !r.IsDeleted && r.Status == AuctionReportStatus.Pending)
            .ToListAsync(cancellationToken);

        if (pendingReports.Count > 0)
        {
            var dismissMessage = $"Your report on \"{auction.SnapshotProductTitle ?? "this auction"}\" has been reviewed. No policy violation was found at this time.";
            foreach (var report in pendingReports)
            {
                report.Status = AuctionReportStatus.Dismissed;
                report.AdminNote = note;
                report.ReviewedBy = actor.UserId;
                report.ReviewedAt = now;
                report.UpdatedAt = now;
            }

            _notifications.NotifyAuctionReportResolved(new AuctionReportResolvedNotification(
                auctionId,
                auction.SnapshotProductTitle,
                AuctionReportStatus.Dismissed.ToString(),
                dismissMessage,
                note,
                pendingReports.Select(r => r.ReporterId).Distinct().ToList(),
                now,
                actor.TraceId));

            _dbContext.AddAuditLog(
                AuditAction.AuctionReportResolve, "Auction", auctionId.ToString(),
                oldValues: null,
                newValues: new { outcome = AuctionReportStatus.Dismissed.ToString(), dismissedCount = pendingReports.Count, note },
                userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

            await SaveAsync(null, cancellationToken);
        }

        return new DismissAuctionReportsResponse(auctionId, pendingReports.Count, note, now);
    }

    // ------------------------------------------------------------------ Buy Now & Below-Reserve Approval

    /// <summary>
    /// Buyer bấm Mua ngay (Buy Now): khóa đủ tiền hàng (tách Tiền cọc + Tiền hàng còn lại) qua Ví Nexus,
    /// chuyển phiên sang Ended (BuyNow), giải tỏa cọc toàn bộ bidder khác và đẩy sang Commerce (trả phí ship trong 24h).
    /// </summary>
    public async Task<BuyNowResponse> BuyNowAsync(
        ActorContext actor, Guid auctionId, string? idempotencyKey, long? expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        if (auctionId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "Auction ID is required.");
        BidRules.ValidateIdempotencyKey(idempotencyKey);

        var key = idempotencyKey!.Trim();
        var requestHash = $"{auctionId:N}|{actor.UserId:N}|BUYNOW";
        var saved = await _dbContext.IdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == key && k.Scope == IdempotencyScope.BuyNow && k.ExpiresAt > now, cancellationToken);
        if (saved is not null)
        {
            if (saved.RequestHash != requestHash)
                throw new BusinessRuleValidationException(
                    "IDEMPOTENCY_KEY_REUSED", "The Idempotency-Key was already used with a different request.", 422);

            if (!string.IsNullOrWhiteSpace(saved.ResponseBody))
            {
                var body = JsonSerializer.Deserialize<BuyNowResponse>(saved.ResponseBody, JsonOptions);
                if (body is not null)
                    return body;
            }
        }

        var auction = await _dbContext.Auctions
            .Include(a => a.AuctionState)
            .Include(a => a.AuctionResult)
            .Include(a => a.AuctionSettlement)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (auction?.AuctionState is null)
            throw NotFound(auctionId);

        if (auction.SellerId == actor.UserId)
            throw new DomainException("SELLER_CANNOT_BID_OWN_AUCTION", "A seller cannot buy their own auction.", 403);

        var state = auction.AuctionState;
        if (expectedRowVersion is { } expected && state.RowVersion != expected)
            throw new DomainException("CONCURRENT_UPDATE", "The auction changed before Buy Now could be completed. Reload and try again.", 409);

        if (state.Status != AuctionStatus.Running || now >= state.ScheduledEndAt)
            throw new DomainException("AUCTION_NOT_RUNNING", "Buy Now is only available while the auction is running.", 409);

        if (auction.BuyNowPriceAmount is not { } buyNowPrice)
            throw new DomainException("BUY_NOW_NOT_CONFIGURED", "This auction does not have a Buy Now price.", 409);

        if (!AuctionRules.IsBuyNowAvailable(state.Status, auction.ReservePriceAmount, auction.BuyNowPriceAmount, state.CurrentPriceAmount, state.BidCount))
            throw new DomainException("BUY_NOW_NOT_AVAILABLE", "Buy Now is no longer available because bidding has reached the threshold.", 409);

        var profile = await _users.GetAuctionDepositProfileAsync(actor.UserId, bypassCache: true, cancellationToken);
        DepositProfileGuard.EnsureCanJoin(profile);

        var existingDeposit = await _dbContext.AuctionDeposits
            .FirstOrDefaultAsync(d => d.AuctionId == auctionId && d.BidderId == actor.UserId && !d.IsDeleted, cancellationToken);
        if (existingDeposit is { ReleasePending: true })
            throw new DomainException("DEPOSIT_RELEASE_PENDING", "Your previous payment hold is still being released. Try again shortly.", 409);

        var policy = await DepositPolicyReader.ReadAsync(_dbContext, cancellationToken);
        decimal depositAmount;
        string? newlyCreatedDepositHoldId = null;
        AuctionDeposit depositEntity;

        if (existingDeposit is { Status: DepositStatus.Authorized })
        {
            depositEntity = existingDeposit;
            depositAmount = Math.Min(existingDeposit.Amount, buyNowPrice);
        }
        else
        {
            var quote = DepositRules.Calculate(
                policy, auction.StartPriceAmount, profile.ReputationScore, profile.IsNewUser, profile.ViolationStreak);
            depositAmount = Math.Min(quote.Amount, buyNowPrice);

            if (existingDeposit is null)
            {
                depositEntity = new AuctionDeposit
                {
                    Id = Guid.NewGuid(),
                    AuctionId = auctionId,
                    BidderId = actor.UserId,
                    Amount = depositAmount,
                    RatePercent = quote.RatePercent,
                    PolicyVersion = policy.Version,
                    Status = DepositStatus.Pending,
                    AttemptCount = 1,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _dbContext.AuctionDeposits.Add(depositEntity);
            }
            else
            {
                depositEntity = existingDeposit;
                depositEntity.Amount = depositAmount;
                depositEntity.RatePercent = quote.RatePercent;
                depositEntity.PolicyVersion = policy.Version;
                depositEntity.Status = DepositStatus.Pending;
                depositEntity.AttemptCount += 1;
                depositEntity.ReleasedAt = null;
                depositEntity.ReleaseReason = null;
                depositEntity.ReleasePending = false;
                depositEntity.PendingAction = DepositPendingAction.None;
                depositEntity.FailureCode = null;
                depositEntity.UpdatedAt = now;
            }

            var depHold = await _wallet.CreateDepositHoldAsync(
                new CreateDepositHoldCommand(
                    depositEntity.Id, actor.UserId, depositAmount, DepositRules.Currency, auctionId, policy.Version),
                $"buynow-dep:{depositEntity.Id:N}:{depositEntity.AttemptCount}",
                cancellationToken);
            if (depHold.Status != DepositHoldState.Authorized)
                throw new DomainException("DEPOSIT_HOLD_NOT_ACTIVE", "Could not lock the Buy Now deposit in your wallet.", 409);

            newlyCreatedDepositHoldId = depHold.HoldId;
            depositEntity.GatewayReference = depHold.HoldId;
            depositEntity.Status = DepositStatus.Authorized;
            depositEntity.AuthorizedAt = depHold.AuthorizedAt ?? now;
            depositEntity.UpdatedAt = now;
            depositEntity.RowVersion += 1;
        }

        var remainingGoodsAmount = Math.Max(0m, buyNowPrice - depositAmount);
        Guid? goodsDepositId = null;
        string? goodsHoldId = null;

        if (remainingGoodsAmount > 0m)
        {
            goodsDepositId = Guid.NewGuid();
            try
            {
                var goodsHold = await _wallet.CreateDepositHoldAsync(
                    new CreateDepositHoldCommand(
                        goodsDepositId.Value, actor.UserId, remainingGoodsAmount, DepositRules.Currency, auctionId, policy.Version),
                    $"buynow-goods:{auctionId:N}:{actor.UserId:N}:{key}",
                    cancellationToken);
                if (goodsHold.Status != DepositHoldState.Authorized)
                    throw new DomainException("BUY_NOW_HOLD_FAILED", "Could not lock the remaining Buy Now amount in your wallet.", 409);

                goodsHoldId = goodsHold.HoldId;
            }
            catch
            {
                if (newlyCreatedDepositHoldId is not null)
                {
                    try
                    {
                        await _wallet.ReleaseDepositHoldAsync(
                            newlyCreatedDepositHoldId, "BUY_NOW_ABORTED", $"buynow-abort-dep:{depositEntity.Id:N}", CancellationToken.None);
                    }
                    catch
                    {
                    }
                }
                throw;
            }
        }

        var fromStatus = state.Status;
        var paymentDeadline = now.Add(WinnerPaymentWindow);

        state.Status = AuctionStatus.Ended;
        state.EndedAt = now;
        state.CurrentWinnerId = actor.UserId;
        state.CurrentPriceAmount = buyNowPrice;
        state.UpdatedAt = now;
        state.RowVersion += 1;

        var result = auction.AuctionResult;
        if (result is null)
        {
            result = new AuctionResult
            {
                Id = Guid.NewGuid(),
                AuctionId = auctionId,
                DeterminedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.AuctionResults.Add(result);
        }
        result.WinnerId = actor.UserId;
        result.WinningBidId = null;
        result.FinalPriceAmount = buyNowPrice;
        result.DeterminedAt = now;
        result.UpdatedAt = now;

        var settlement = auction.AuctionSettlement;
        if (settlement is null)
        {
            settlement = new AuctionSettlement
            {
                Id = Guid.NewGuid(),
                AuctionId = auctionId,
                Status = AuctionSettlementStatus.PendingPayment,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.AuctionSettlements.Add(settlement);
        }
        settlement.WinnerId = actor.UserId;
        settlement.WinningBidId = null;
        settlement.Status = AuctionSettlementStatus.PendingPayment;
        settlement.PaymentDeadlineAt = paymentDeadline;
        settlement.IsBuyNow = true;
        settlement.IsBelowReserve = false;
        settlement.BuyNowGoodsDepositId = goodsDepositId;
        settlement.BuyNowGoodsHoldId = goodsHoldId;
        settlement.BuyNowGoodsAmount = remainingGoodsAmount > 0m ? remainingGoodsAmount : null;
        settlement.BuyNowGoodsPendingAction = DepositPendingAction.None;
        settlement.UpdatedAt = now;

        var depositsToRelease = await _dbContext.AuctionDeposits
            .Where(d => d.AuctionId == auctionId && d.BidderId != actor.UserId && !d.IsDeleted &&
                        (d.Status == DepositStatus.Authorized || d.Status == DepositStatus.Pending))
            .ToListAsync(cancellationToken);
        foreach (var other in depositsToRelease)
        {
            other.Status = DepositStatus.Released;
            other.ReleasePending = true;
            other.PendingAction = DepositPendingAction.Release;
            other.ReleaseReason = "AUCTION_ENDED_BUY_NOW";
            other.NextRetryAt = now;
            other.NeedsAttention = false;
            other.UpdatedAt = now;
            other.RowVersion += 1;
        }

        _dbContext.AuctionStatusHistories.Add(new AuctionStatusHistory
        {
            Id = Guid.NewGuid(),
            AuctionId = auctionId,
            FromStatus = fromStatus,
            ToStatus = AuctionStatus.Ended,
            Reason = "Auction closed immediately via Buy Now.",
            ChangedBy = actor.UserId,
            ChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        var response = new BuyNowResponse(
            auctionId, actor.UserId, buyNowPrice, depositAmount, remainingGoodsAmount,
            state.Status, now, paymentDeadline, state.RowVersion);

        _dbContext.IdempotencyKeys.Add(new IdempotencyKey
        {
            Id = Guid.NewGuid(),
            Key = key,
            Scope = IdempotencyScope.BuyNow,
            RequestHash = requestHash,
            ResponseStatus = 200,
            ResponseBody = JsonSerializer.Serialize(response, JsonOptions),
            ExpiresAt = now.Add(SettlementIdempotencyLifetime),
            CreatedAt = now,
            UpdatedAt = now
        });

        _dbContext.AddAuditLog(
            AuditAction.AuctionBuyNow, "Auction", auctionId.ToString(),
            oldValues: new { status = fromStatus.ToString() },
            newValues: new { status = state.Status.ToString(), buyerId = actor.UserId, buyNowPrice, depositAmount, remainingGoodsAmount },
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        _notifications.NotifyAuctionWinner(new AuctionWinnerNotification(
            auctionId, auction.SnapshotProductTitle, result.Id, result.Id,
            actor.UserId, auction.SellerId, buyNowPrice, now, paymentDeadline));

        try
        {
            await SaveAsync(null, cancellationToken);
        }
        catch
        {
            // Compensating release if DB commit failed after locking wallet funds.
            if (goodsHoldId is not null)
            {
                try { await _wallet.ReleaseDepositHoldAsync(goodsHoldId, "BUY_NOW_DB_FAILED", $"buynow-abort-goods:{goodsDepositId:N}", CancellationToken.None); }
                catch { }
            }
            if (newlyCreatedDepositHoldId is not null)
            {
                try { await _wallet.ReleaseDepositHoldAsync(newlyCreatedDepositHoldId, "BUY_NOW_DB_FAILED", $"buynow-abort-dep2:{depositEntity.Id:N}", CancellationToken.None); }
                catch { }
            }
            throw;
        }

        if (depositsToRelease.Count > 0)
            await _deposits.ReleasePendingDepositsForAuctionAsync(auctionId, cancellationToken);

        _commerceSync.PushSettlement(auctionId);

        await _notifier.AuctionStatusChangedAsync(
            auctionId, new AuctionStatusChangedMessage(auctionId, state.Status.ToString(), now, "BuyNow"), CancellationToken.None);

        return response;
    }

    /// <summary>Seller đồng ý bán dưới giá sàn trong vòng 12 giờ: tạo đơn cho Hạng 1 (24h thanh toán) và reset chuỗi từ chối về 0.</summary>
    public async Task<ReserveApprovalResponse> ApproveBelowReserveAsync(
        ActorContext actor, Guid auctionId, CancellationToken cancellationToken = default)
    {
        if (auctionId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "Auction ID is required.");

        var now = _clock.GetUtcNow();
        var auction = await _dbContext.Auctions
            .Include(a => a.AuctionState)
            .Include(a => a.AuctionResult)
            .Include(a => a.AuctionSettlement)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (auction?.AuctionState is null)
            throw NotFound(auctionId);

        EnsureOwner(auction, actor, "approve below-reserve sale");

        var settlement = auction.AuctionSettlement;
        if (auction.AuctionState.Status != AuctionStatus.Ended
            || settlement is null
            || settlement.Status != AuctionSettlementStatus.AwaitingSellerApproval)
        {
            throw new DomainException("NOT_AWAITING_SELLER_APPROVAL", "This auction is not awaiting seller reserve approval.", 409);
        }

        if (settlement.SellerApprovalDeadlineAt is { } deadline && deadline <= now)
            throw new DomainException("SELLER_APPROVAL_EXPIRED", "The 12-hour seller approval window has expired.", 409);

        var paymentDeadline = now.Add(WinnerPaymentWindow);
        settlement.Status = AuctionSettlementStatus.PendingPayment;
        settlement.PaymentDeadlineAt = paymentDeadline;
        settlement.UpdatedAt = now;

        // Reset seller's consecutive decline streak ONLY when they approve a below-reserve sale.
        var penalty = await _dbContext.SellerReservePenalties
            .FirstOrDefaultAsync(p => p.SellerId == auction.SellerId && !p.IsDeleted, cancellationToken);
        if (penalty is not null)
        {
            penalty.ConsecutiveDeclineCount = 0;
            penalty.UpdatedAt = now;
        }

        _dbContext.AddAuditLog(
            AuditAction.ReserveApprovalAccept, "Auction", auctionId.ToString(),
            oldValues: new { settlementStatus = AuctionSettlementStatus.AwaitingSellerApproval.ToString() },
            newValues: new { settlementStatus = settlement.Status.ToString(), acceptedPrice = auction.AuctionResult?.FinalPriceAmount, paymentDeadline },
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        if (auction.AuctionResult is { } res && res.WinnerId is { } winId && res.WinningBidId is { } winBidId)
        {
            _notifications.NotifyAuctionWinner(new AuctionWinnerNotification(
                auctionId, auction.SnapshotProductTitle, res.Id, winBidId, winId,
                auction.SellerId, res.FinalPriceAmount ?? 0m, now, paymentDeadline));
        }

        await SaveAsync(null, cancellationToken);
        _commerceSync.PushSettlement(auctionId);

        return new ReserveApprovalResponse(
            auctionId, auction.AuctionState.Status, settlement.Status, auction.AuctionResult?.FinalPriceAmount,
            paymentDeadline, penalty?.ConsecutiveDeclineCount ?? 0, penalty?.ReserveRestrictedUntil, now);
    }

    /// <summary>Seller từ chối bán dưới giá sàn trong vòng 12 giờ: đóng phiên Failed, hoàn cọc Hạng 1, tăng chuỗi từ chối (đủ 3 lần khóa 14 ngày).</summary>
    public async Task<ReserveApprovalResponse> RejectBelowReserveAsync(
        ActorContext actor, Guid auctionId, RejectBelowReserveRequest? request, CancellationToken cancellationToken = default)
    {
        if (auctionId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "Auction ID is required.");

        var now = _clock.GetUtcNow();
        var auction = await _dbContext.Auctions
            .Include(a => a.AuctionState)
            .Include(a => a.AuctionSettlement)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (auction?.AuctionState is null)
            throw NotFound(auctionId);

        EnsureOwner(auction, actor, "decline below-reserve sale");

        var state = auction.AuctionState;
        var settlement = auction.AuctionSettlement;
        if (state.Status != AuctionStatus.Ended
            || settlement is null
            || settlement.Status != AuctionSettlementStatus.AwaitingSellerApproval)
        {
            throw new DomainException("NOT_AWAITING_SELLER_APPROVAL", "This auction is not awaiting seller reserve approval.", 409);
        }

        var reason = string.IsNullOrWhiteSpace(request?.Reason)
            ? "ReserveNotMet: Seller declined to sell below the reserve price."
            : $"ReserveNotMet: {request.Reason.Trim()}";
        if (reason.Length > MaxReasonLength)
            reason = reason[..MaxReasonLength];

        settlement.Status = AuctionSettlementStatus.Cancelled;
        settlement.UpdatedAt = now;

        state.Status = AuctionStatus.Failed;
        state.UpdatedAt = now;
        state.RowVersion += 1;

        auction.StockReleaseState = BackgroundSyncState.Pending;
        auction.NextRetryAt = now;
        auction.NeedsAttention = false;
        auction.UpdatedAt = now;

        var openAuction = await _dbContext.ProductOpenAuctions
            .FirstOrDefaultAsync(p => p.AuctionId == auctionId, cancellationToken);
        if (openAuction is not null)
            _dbContext.ProductOpenAuctions.Remove(openAuction);

        var depositsToRelease = await MarkDepositsForReleaseAsync(auctionId, "RESERVE_NOT_MET", now, actor, cancellationToken);

        var penalty = await _dbContext.SellerReservePenalties
            .FirstOrDefaultAsync(p => p.SellerId == auction.SellerId && !p.IsDeleted, cancellationToken);
        if (penalty is null)
        {
            penalty = new SellerReservePenalty
            {
                Id = Guid.NewGuid(),
                SellerId = auction.SellerId,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.SellerReservePenalties.Add(penalty);
        }
        penalty.ConsecutiveDeclineCount += 1;
        if (penalty.ConsecutiveDeclineCount >= AuctionRules.MaxConsecutiveReserveDeclines)
            penalty.ReserveRestrictedUntil = now.AddDays(AuctionRules.ReserveRestrictionDays);
        penalty.UpdatedAt = now;

        _dbContext.AuctionStatusHistories.Add(new AuctionStatusHistory
        {
            Id = Guid.NewGuid(),
            AuctionId = auctionId,
            FromStatus = AuctionStatus.Ended,
            ToStatus = AuctionStatus.Failed,
            Reason = reason,
            ChangedBy = actor.UserId,
            ChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        _dbContext.AddAuditLog(
            AuditAction.ReserveApprovalDecline, "Auction", auctionId.ToString(),
            oldValues: new { status = AuctionStatus.Ended.ToString(), settlementStatus = AuctionSettlementStatus.AwaitingSellerApproval.ToString() },
            newValues: new { status = AuctionStatus.Failed.ToString(), settlementStatus = settlement.Status.ToString(), consecutiveDeclineCount = penalty.ConsecutiveDeclineCount, restrictedUntil = penalty.ReserveRestrictedUntil },
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        _notifications.NotifyAuctionFailed(new AuctionFailedNotification(
            auctionId, auction.SellerId, auction.SnapshotProductTitle, reason, now, actor.TraceId));

        await SaveAsync(null, cancellationToken);

        if (depositsToRelease > 0)
            await _deposits.ReleasePendingDepositsForAuctionAsync(auctionId, cancellationToken);

        _catalogSync.UnlockProduct(auction.ProductId, auctionId);
        await _notifier.AuctionStatusChangedAsync(
            auctionId, new AuctionStatusChangedMessage(auctionId, AuctionStatus.Failed.ToString(), now, "ReserveNotMet"), CancellationToken.None);

        return new ReserveApprovalResponse(
            auctionId, state.Status, settlement.Status, null, null,
            penalty.ConsecutiveDeclineCount, penalty.ReserveRestrictedUntil, now);
    }

    /// <summary>Seller xem trạng thái chuỗi từ chối dưới giá sàn và thời hạn bị khóa quyền đặt Giá sàn (nếu có).</summary>
    public async Task<SellerReserveStatusResponse> GetSellerReserveStatusAsync(
        Guid sellerId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var penalty = await _dbContext.SellerReservePenalties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.SellerId == sellerId && !p.IsDeleted, cancellationToken);

        var isRestricted = penalty?.ReserveRestrictedUntil is { } until && until > now;
        var count = penalty is not null && !isRestricted && penalty.ReserveRestrictedUntil <= now
            ? 0
            : penalty?.ConsecutiveDeclineCount ?? 0;

        return new SellerReserveStatusResponse(
            sellerId,
            count,
            isRestricted,
            isRestricted ? penalty!.ReserveRestrictedUntil : null);
    }

    private async Task EnsureSellerCanSetReservePriceAsync(
        Guid sellerId, decimal? reservePriceAmount, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (reservePriceAmount is null)
            return;

        var penalty = await _dbContext.SellerReservePenalties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.SellerId == sellerId && !p.IsDeleted, cancellationToken);

        if (penalty?.ReserveRestrictedUntil is { } until && until > now)
        {
            throw new BusinessRuleValidationException(
                "RESERVE_PRICE_RESTRICTED",
                $"Setting a reserve price is temporarily restricted until {until:yyyy-MM-dd HH:mm} UTC due to consecutive below-reserve rejections.",
                422);
        }
    }

    /// <summary>Lấy số liệu thống kê các phiên thành công và thất bại của seller.</summary>
    public async Task<SellerAuctionStatisticsDto> GetSellerAuctionStatisticsAsync(
        Guid sellerId, CancellationToken cancellationToken = default)
    {
        var counts = await _dbContext.Auctions
            .AsNoTracking()
            .Where(a =>
                a.SellerId == sellerId &&
                !a.IsDeleted &&
                a.AuctionState != null &&
                !a.AuctionState.IsDeleted &&
                (a.AuctionState.Status == AuctionStatus.Settled || a.AuctionState.Status == AuctionStatus.Failed))
            .GroupBy(a => a.AuctionState!.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);

        var successfulAuctions = counts.GetValueOrDefault(AuctionStatus.Settled);
        var failedAuctions = counts.GetValueOrDefault(AuctionStatus.Failed);
        var completedAuctions = successfulAuctions + failedAuctions;
        decimal? successRate = completedAuctions == 0
            ? null
            : Math.Round(successfulAuctions * 100m / completedAuctions, 2, MidpointRounding.AwayFromZero);

        return new SellerAuctionStatisticsDto(
            sellerId,
            successfulAuctions,
            failedAuctions,
            completedAuctions,
            successRate);
    }

    /// <summary>
    /// Lấy lịch sử đổi trạng thái của một phiên, mới nhất trước, có phân trang.
    /// Chỉ seller sở hữu phiên hoặc admin/staff được xem (người khác nhận 403 FORBIDDEN). Phiên không tồn tại trả 404.
    /// </summary>
    public async Task<PagedData<AuctionStatusHistoryDto>> GetStatusHistoryAsync(
        ActorContext actor, bool isPrivileged, Guid auctionId, int page = 1, int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);

        var sellerId = await _dbContext.Auctions
            .AsNoTracking()
            .Where(a => a.Id == auctionId && !a.IsDeleted)
            .Select(a => (Guid?)a.SellerId)
            .FirstOrDefaultAsync(cancellationToken);

        if (sellerId is null)
            throw NotFound(auctionId);

        if (!isPrivileged && sellerId.Value != actor.UserId)
            throw new DomainException(
                "FORBIDDEN", "Only the seller of the auction, an admin or a staff member can view its status history.", 403);

        var query = _dbContext.AuctionStatusHistories
            .AsNoTracking()
            .Where(h => h.AuctionId == auctionId && !h.IsDeleted);

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(h => h.ChangedAt)
            .ThenByDescending(h => h.Id)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(h => new
            {
                h.Id,
                h.FromStatus,
                h.ToStatus,
                h.Reason,
                h.ChangedBy,
                h.ChangedAt
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(r => new AuctionStatusHistoryDto(r.Id, r.FromStatus, r.ToStatus, r.Reason, r.ChangedBy, r.ChangedAt))
            .ToList();

        return new PagedData<AuctionStatusHistoryDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    // Trạng thái của từng bước trong saga (chuỗi hiển thị cho admin).
    private const string StepCompleted = "Completed";
    private const string StepInProgress = "InProgress";
    private const string StepPending = "Pending";
    private const string StepSkipped = "Skipped";
    private const string StepFailed = "Failed";

    /// <summary>
    /// Admin xem tiến trình hậu đấu giá (saga) của một phiên: lên lịch, đấu giá, kết thúc, chốt kết quả, thanh toán qua Commerce,
    /// mời bidder kế tiếp (nếu có) và trạng thái cuối. Dựng từ dữ liệu có sẵn (trạng thái, kết quả, settlement, lời mời), không có bảng saga riêng.
    /// Trả kèm hạn đang chờ và cờ IsOverdue để admin phát hiện phiên bị kẹt (ví dụ Commerce chưa báo kết quả thanh toán).
    /// Saga chỉ tồn tại sau khi đã chốt người thắng: phiên không tồn tại hoặc chưa có người thắng đều trả 404 AUCTION_NOT_FOUND.
    /// </summary>
    public async Task<AuctionSagaDto> GetSagaAsync(Guid auctionId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        var auction = await _dbContext.Auctions
            .AsNoTracking()
            .Include(a => a.AuctionState)
            .Include(a => a.AuctionResult)
            .Include(a => a.AuctionSettlement)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);

        var state = auction?.AuctionState;
        var result = auction?.AuctionResult;
        if (result is not null && result.IsDeleted)
            result = null;

        if (auction is null || state is null || result is null)
            throw new DomainException("AUCTION_NOT_FOUND", "Auction was not found or has no winning saga.", 404);

        var settlement = auction.AuctionSettlement;
        if (settlement is not null && settlement.IsDeleted)
            settlement = null;

        // Mỗi phiên chỉ có tối đa một lời mời, lấy lời mời mới nhất cho chắc.
        var offer = await _dbContext.AuctionWinnerOffers
            .AsNoTracking()
            .Where(o => o.AuctionId == auctionId && !o.IsDeleted)
            .OrderByDescending(o => o.OfferedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var status = state.Status;
        var cancelled = status == AuctionStatus.Cancelled;
        var isFinished = status is AuctionStatus.Settled or AuctionStatus.Failed or AuctionStatus.Cancelled;
        var closed = state.EndedAt is not null;

        var steps = new List<AuctionSagaStepDto>();

        // 1. Lên lịch.
        steps.Add(new AuctionSagaStepDto(
            "Scheduled", StepCompleted, auction.CreatedAt, $"Scheduled start at {auction.ScheduledStartAt:O}."));

        // 2. Đang đấu giá.
        string biddingState;
        if (state.StartedAt is null)
            biddingState = cancelled ? StepSkipped : StepPending;
        else if (status == AuctionStatus.Running)
            biddingState = StepInProgress;
        else if (cancelled && !closed)
            biddingState = StepFailed;
        else
            biddingState = StepCompleted;

        var currentPrice = state.CurrentPriceAmount.HasValue
            ? state.CurrentPriceAmount.Value.ToString("0.##", CultureInfo.InvariantCulture)
            : "none";
        steps.Add(new AuctionSagaStepDto(
            "Bidding", biddingState, state.StartedAt,
            $"{state.BidCount} bid(s), {state.ExtensionCount} extension(s), current price {currentPrice}."));

        // 3. Hết giờ.
        string endedState;
        if (closed)
            endedState = StepCompleted;
        else if (cancelled)
            endedState = StepSkipped;
        else if (status == AuctionStatus.Running)
            endedState = StepInProgress;
        else
            endedState = StepPending;

        steps.Add(new AuctionSagaStepDto(
            "Ended", endedState, state.EndedAt,
            closed ? "The auction time is over." : $"Scheduled end at {state.ScheduledEndAt:O}."));

        // 4. Chốt kết quả (người thắng và giá chốt).
        string resultState;
        DateTimeOffset? resultAt = null;
        string? resultDetail = null;
        if (result is not null)
        {
            resultState = StepCompleted;
            resultAt = result.DeterminedAt;
            resultDetail = $"Winner {result.WinnerId}, final price {result.FinalPriceAmount}.";
        }
        else if (closed)
        {
            resultState = StepFailed;
            resultDetail = "The auction ended without a winner.";
        }
        else if (cancelled)
        {
            resultState = StepSkipped;
        }
        else
        {
            resultState = StepPending;
        }

        steps.Add(new AuctionSagaStepDto("Result", resultState, resultAt, resultDetail));

        // 5. Thanh toán qua Commerce.
        string paymentState;
        DateTimeOffset? paymentAt = null;
        string? paymentDetail = null;
        if (settlement is null)
        {
            paymentState = isFinished ? StepSkipped : StepPending;
        }
        else
        {
            switch (settlement.Status)
            {
                case AuctionSettlementStatus.PendingPayment:
                    paymentState = StepInProgress;
                    paymentDetail = $"Waiting for the payment of winner {settlement.WinnerId}, deadline {settlement.PaymentDeadlineAt:O}.";
                    break;
                case AuctionSettlementStatus.Paid:
                    paymentState = StepCompleted;
                    paymentAt = settlement.PaidAt;
                    paymentDetail = $"Paid, order {settlement.OrderId}.";
                    break;
                default:
                    paymentState = StepFailed;
                    paymentDetail = $"Payment {settlement.Status}, order {settlement.OrderId}.";
                    break;
            }
        }

        steps.Add(new AuctionSagaStepDto("Payment", paymentState, paymentAt, paymentDetail));

        // 6. Lời mời bidder kế tiếp (chỉ có khi thanh toán của người thắng trước không thành công).
        if (offer is not null)
        {
            string offerState;
            var offerAt = offer.RespondedAt;
            switch (offer.Status)
            {
                case AuctionWinnerOfferStatus.Pending:
                    offerState = StepInProgress;
                    offerAt = offer.OfferedAt;
                    break;
                case AuctionWinnerOfferStatus.Accepted:
                    offerState = StepCompleted;
                    break;
                default:
                    offerState = StepFailed;
                    offerAt ??= offer.ExpiresAt;
                    break;
            }

            steps.Add(new AuctionSagaStepDto(
                "Winner offer", offerState, offerAt,
                $"Offer to bidder {offer.BidderId} for {offer.Amount}, status {offer.Status}, expires at {offer.ExpiresAt:O}."));
        }

        // 7. Trạng thái cuối của phiên.
        var finalState = status switch
        {
            AuctionStatus.Settled => StepCompleted,
            AuctionStatus.Failed or AuctionStatus.Cancelled => StepFailed,
            _ => StepPending
        };
        steps.Add(new AuctionSagaStepDto(
            "Final status", finalState, isFinished ? state.UpdatedAt : null, $"Auction status is {status}."));

        // Bước hiện tại: bước đang chạy, không có thì bước chờ đầu tiên, không có nữa thì bước cuối.
        var current = steps.FirstOrDefault(s => s.State == StepInProgress)
            ?? steps.FirstOrDefault(s => s.State == StepPending)
            ?? steps[^1];

        // Đang chờ gì và hạn là bao giờ: quá hạn mà chưa xong nghĩa là phiên có thể bị kẹt.
        string? waitingFor = null;
        DateTimeOffset? deadlineAt = null;
        if (status == AuctionStatus.Scheduled)
        {
            waitingFor = "Start time of the auction";
            deadlineAt = auction.ScheduledStartAt;
        }
        else if (status == AuctionStatus.Running)
        {
            waitingFor = "End time of the auction";
            deadlineAt = state.ScheduledEndAt;
        }
        else if (status == AuctionStatus.Ended)
        {
            if (offer is { Status: AuctionWinnerOfferStatus.Pending })
            {
                waitingFor = "Answer of the invited bidder";
                deadlineAt = offer.ExpiresAt;
            }
            else if (settlement is { Status: AuctionSettlementStatus.PendingPayment })
            {
                waitingFor = "Payment result from Commerce";
                deadlineAt = settlement.PaymentDeadlineAt;
            }
        }

        var isOverdue = !isFinished && deadlineAt is not null && deadlineAt.Value < now;

        // Saga bắt đầu khi settlement được tạo (hoặc khi chốt kết quả nếu chưa có settlement) và cập nhật theo lần thay đổi gần nhất của các phần liên quan.
        var createdAt = settlement?.CreatedAt ?? result.CreatedAt;
        var updatedAt = new[] { state.UpdatedAt, result.UpdatedAt, settlement?.UpdatedAt ?? createdAt, offer?.UpdatedAt ?? createdAt }.Max();

        return new AuctionSagaDto(
            auction.Id, "AuctionSettlement", current.Name,
            new AuctionSagaStateData(auction.ProductId, auction.SellerId, status, isFinished, waitingFor, deadlineAt, isOverdue, steps),
            createdAt, updatedAt);
    }

    /// <summary>
    /// Lấy kết quả của phiên sau khi đã kết thúc (API 06): trả về phase hậu đấu giá, thông tin người thắng,
    /// settlement (depositAmount, remainingAmount), cọc của chính người gọi và lời mời người về nhì, lọc theo vai trò người gọi.
    /// </summary>
    public async Task<AuctionResultDto> GetResultAsync(
        Guid auctionId, Guid? callerUserId = null, bool isPrivileged = false, CancellationToken cancellationToken = default)
    {
        var auction = await _dbContext.Auctions
            .AsNoTracking()
            .Where(a => a.Id == auctionId && !a.IsDeleted)
            .Select(a => new
            {
                a.Id,
                a.SellerId,
                Status = a.AuctionState == null ? (AuctionStatus?)null : a.AuctionState.Status,
                BidCount = a.AuctionState == null ? 0 : a.AuctionState.BidCount
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (auction?.Status is null)
            throw NotFound(auctionId);

        var status = auction.Status.Value;
        if (status is AuctionStatus.Draft or AuctionStatus.Scheduled or AuctionStatus.Running)
            throw new DomainException("AUCTION_NOT_ENDED", "The auction has not ended yet.", 409);

        var result = await _dbContext.AuctionResults
            .AsNoTracking()
            .Where(r => r.AuctionId == auctionId && !r.IsDeleted)
            .OrderByDescending(r => r.DeterminedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var settlement = await _dbContext.AuctionSettlements
            .AsNoTracking()
            .Where(s => s.AuctionId == auctionId && !s.IsDeleted)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var offer = await _dbContext.AuctionWinnerOffers
            .AsNoTracking()
            .Where(o => o.AuctionId == auctionId && !o.IsDeleted)
            .OrderByDescending(o => o.OfferedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var isSeller = callerUserId.HasValue && callerUserId.Value == auction.SellerId;
        var isWinner = callerUserId.HasValue && result?.WinnerId is { } winId && callerUserId.Value == winId;
        var isSecondChanceBidder = callerUserId.HasValue && offer is not null && callerUserId.Value == offer.BidderId;
        var canSeeWinnerAndSettlement = isPrivileged || isSeller || isWinner || (isSecondChanceBidder && offer?.Status == AuctionWinnerOfferStatus.Accepted);
        var canSeeSecondChance = isPrivileged || isSeller || isSecondChanceBidder;

        // Xác định phase hậu đấu giá
        string phase;
        if (status == AuctionStatus.Cancelled)
        {
            phase = "CANCELLED";
        }
        else if (status == AuctionStatus.Failed)
        {
            phase = result is null && auction.BidCount == 0 ? "NO_BIDS_FAILED" : "FAILED";
        }
        else if (status == AuctionStatus.Settled)
        {
            phase = "SETTLED";
        }
        else
        {
            // Status == Ended
            if (settlement is { Status: AuctionSettlementStatus.Paid })
                phase = "WINNER_PAID_AWAITING_SELLER";
            else if (offer is { Status: AuctionWinnerOfferStatus.Pending })
                phase = "SECOND_CHANCE_OFFERED";
            else if (offer is { Status: AuctionWinnerOfferStatus.Accepted })
                phase = "AWAITING_RUNNER_UP_PAYMENT";
            else
                phase = "AWAITING_WINNER_PAYMENT";
        }

        // Cọc của người thắng hiện tại (Winner hoặc Người về nhì đã nhận lời mời: cả hai đều có cọc giữ trong phiên)
        decimal winnerDepositAmount = 0m;
        if (result?.WinnerId is { } currentWinnerId)
        {
            var winnerDeposit = await _dbContext.AuctionDeposits
                .AsNoTracking()
                .Where(d => d.AuctionId == auctionId && d.BidderId == currentWinnerId && !d.IsDeleted &&
                            (d.Status == DepositStatus.Authorized || d.Status == DepositStatus.Captured))
                .Select(d => (decimal?)d.Amount)
                .FirstOrDefaultAsync(cancellationToken);

            winnerDepositAmount = winnerDeposit ?? 0m;
        }

        AuctionResultWinnerDto? winnerDto = null;
        if (result?.WinnerId is { } wId && canSeeWinnerAndSettlement)
        {
            winnerDto = new AuctionResultWinnerDto(wId, null);
        }

        AuctionResultSettlementDto? settlementDto = null;
        if (settlement is not null && canSeeWinnerAndSettlement)
        {
            var winningPrice = result?.FinalPriceAmount ?? 0m;
            var remaining = Math.Max(0m, winningPrice - winnerDepositAmount);
            var settlementStatusText = settlement.OrderId.HasValue ? settlement.Status.ToString().ToUpperInvariant() : "PENDING";
            settlementDto = new AuctionResultSettlementDto(
                settlement.OrderId,
                settlementStatusText,
                winnerDepositAmount,
                remaining,
                settlement.PaymentDeadlineAt);
        }

        AuctionResultMyDepositDto? myDepositDto = null;
        if (callerUserId.HasValue && !isSeller && !isPrivileged)
        {
            var myDeposit = await _dbContext.AuctionDeposits
                .AsNoTracking()
                .Where(d => d.AuctionId == auctionId && d.BidderId == callerUserId.Value && !d.IsDeleted)
                .Select(d => new { d.Status, d.Amount })
                .FirstOrDefaultAsync(cancellationToken);

            if (myDeposit is not null)
                myDepositDto = new AuctionResultMyDepositDto(myDeposit.Status, myDeposit.Amount);
        }

        AuctionResultSecondChanceDto? secondChanceDto = null;
        if (offer is not null && canSeeSecondChance)
        {
            var offerStatusText = offer.Status == AuctionWinnerOfferStatus.Pending ? "Offered" : offer.Status.ToString();
            secondChanceDto = new AuctionResultSecondChanceDto(
                offer.Id,
                offer.BidderId,
                offerStatusText,
                offer.Amount,
                offer.ExpiresAt);
        }

        return new AuctionResultDto(
            auctionId,
            status,
            phase,
            canSeeWinnerAndSettlement ? result?.WinnerId : null,
            canSeeWinnerAndSettlement ? result?.WinningBidId : null,
            result?.FinalPriceAmount,
            result?.DeterminedAt,
            winnerDto,
            result?.FinalPriceAmount,
            settlementDto,
            myDepositDto,
            secondChanceDto);
    }

    /// <summary>
    /// Người về nhì xem lời mời mua lại của chính mình trong một phiên đấu giá cụ thể (API 09).
    /// Trả 404 AUCTION_NOT_FOUND nếu phiên không tồn tại, 404 NO_OFFER nếu người gọi không có lời mời trong phiên này.
    /// </summary>
    public async Task<MySecondChanceOfferDto> GetMySecondChanceOfferAsync(
        Guid auctionId, Guid bidderId, CancellationToken cancellationToken = default)
    {
        if (auctionId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "Auction ID is required.");

        var auctionExists = await _dbContext.Auctions
            .AsNoTracking()
            .AnyAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (!auctionExists)
            throw NotFound(auctionId);

        var offer = await _dbContext.AuctionWinnerOffers
            .AsNoTracking()
            .Where(o => o.AuctionId == auctionId && o.BidderId == bidderId && !o.IsDeleted)
            .OrderByDescending(o => o.OfferedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (offer is null)
            throw new DomainException("NO_OFFER", "You do not have a second-chance offer for this auction.", 404);

        var now = _clock.GetUtcNow();
        AuctionSettlement? settlement = null;
        if (offer.Status == AuctionWinnerOfferStatus.Accepted)
        {
            settlement = await _dbContext.AuctionSettlements
                .AsNoTracking()
                .Where(s => s.AuctionId == auctionId && s.WinnerId == bidderId && !s.IsDeleted)
                .FirstOrDefaultAsync(cancellationToken);
        }

        string statusText;
        if (offer.Status == AuctionWinnerOfferStatus.Pending)
        {
            statusText = offer.ExpiresAt <= now ? "Expired" : "Offered";
        }
        else if (offer.Status == AuctionWinnerOfferStatus.Accepted && settlement is not null)
        {
            statusText = settlement.Status switch
            {
                AuctionSettlementStatus.Paid => "Paid",
                AuctionSettlementStatus.Expired or AuctionSettlementStatus.Failed => "PaymentExpired",
                _ => "Accepted"
            };
        }
        else
        {
            statusText = offer.Status.ToString();
        }

        var runnerUpDepositAmount = await _dbContext.AuctionDeposits
            .AsNoTracking()
            .Where(d => d.AuctionId == auctionId && d.BidderId == bidderId && !d.IsDeleted &&
                        (d.Status == DepositStatus.Authorized || d.Status == DepositStatus.Captured))
            .Select(d => (decimal?)d.Amount)
            .FirstOrDefaultAsync(cancellationToken) ?? 0m;

        var formattedPrice = offer.Amount.ToString("N0", CultureInfo.InvariantCulture);
        string notice;
        if (runnerUpDepositAmount > 0m)
        {
            var remaining = Math.Max(0m, offer.Amount - runnerUpDepositAmount);
            var formattedDeposit = runnerUpDepositAmount.ToString("N0", CultureInfo.InvariantCulture);
            var formattedRemaining = remaining.ToString("N0", CultureInfo.InvariantCulture);
            notice = $"Your deposit of {formattedDeposit} VND is held and will be deducted from the offer price of {formattedPrice} VND (remaining to pay: {formattedRemaining} VND).";
        }
        else
        {
            notice = $"You will pay the full amount of {formattedPrice} VND. Your previous deposit was already released and is not deducted from this amount.";
        }

        return new MySecondChanceOfferDto(
            offer.Id,
            statusText,
            offer.Amount,
            DepositRules.Currency,
            offer.OfferedAt,
            offer.ExpiresAt,
            settlement?.OrderId,
            settlement?.PaymentDeadlineAt,
            notice);
    }

    /// <summary>
    /// Tiếp nhận callback kết quả thanh toán từ Commerce cho phiên đã Ended.
    /// Kiểm tra dữ liệu đầu vào, dùng eventId làm khoá idempotent (gửi lại cùng nội dung thì trả kết quả cũ), đối chiếu người thắng, giá chốt và đơn hàng
    /// với kết quả của phiên, tạo hoặc cập nhật settlement. Paid chuyển phiên sang Settled.
    /// Expired hoặc Failed: nếu còn bidder hợp lệ kế tiếp (chưa từng có lời mời nào cho phiên này) thì tạo lời mời cho người đó và phiên vẫn ở Ended
    /// chờ họ phản hồi; không có ai thì phiên chuyển sang Failed và nhả sản phẩm. Cancelled chuyển phiên sang Failed và nhả sản phẩm.
    /// PaymentTimedOut xử lý như Expired. SellerAccepted, SellerRejected, SellerExpired (phiên đang Settled) xử lý ở HandleSellerDecisionAsync.
    /// Ghi lịch sử trạng thái, audit và báo realtime AuctionStatusChanged cho người đang xem phiên.
    /// </summary>
    /// <summary>How far in the future Auction's own payment deadline may be before a "payment timed out" report is refused.</summary>
    private static readonly TimeSpan PaymentDeadlineTolerance = TimeSpan.FromMinutes(2);

    public async Task<SettlementPaymentStatusResponse> HandlePaymentStatusAsync(
        Guid auctionId, SettlementPaymentStatusRequest request, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        if (auctionId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "Auction ID is required.");
        if (request.OrderId is not { } orderId || orderId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "Order ID is required.");
        if (request.WinnerId is not { } winnerId || winnerId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "Winner ID is required.");
        if (request.Status is not { } paymentStatus)
            throw Invalid("VAL_INVALID_STATUS", "Payment status is required.");
        if (string.IsNullOrWhiteSpace(request.EventId) || request.EventId.Trim().Length > 255)
            throw Invalid("VAL_INVALID_EVENT_ID", "Event ID is required and must be at most 255 characters.");
        if (paymentStatus == SettlementPaymentStatus.Paid && request.PaidAt is null)
            throw Invalid("VAL_INVALID_PAYMENT_DATE", "Paid time is required for a successful payment.");
        if (request.Amount is { } amount && amount < 0)
            throw Invalid("VAL_INVALID_AMOUNT", "Payment amount must not be negative.");

        var eventId = request.EventId.Trim();
        var requestHash = HashSettlementRequest(auctionId, request);
        var existing = await _dbContext.IdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == eventId && k.Scope == IdempotencyScope.Settlement && k.ExpiresAt > now, cancellationToken);

        if (existing is not null)
        {
            if (existing.RequestHash != requestHash)
                throw new BusinessRuleValidationException(
                    "IDEMPOTENCY_KEY_REUSED", "The event ID was already used with a different request.", 422);

            var saved = DeserializeSettlementResponse(existing.ResponseBody);
            if (saved is not null)
                return saved;
        }

        var auction = await _dbContext.Auctions
            .Include(a => a.AuctionState)
            .Include(a => a.AuctionResult)
            .Include(a => a.AuctionSettlement)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);

        if (auction?.AuctionState is null)
            throw NotFound(auctionId);

        var state = auction.AuctionState;
        var result = auction.AuctionResult;
        if (result is null || result.IsDeleted || result.WinnerId is null)
            throw new DomainException(
                "AUCTION_RESULT_NOT_AVAILABLE", "The result of this auction has not been determined yet.", 404);
        if (result.WinnerId != winnerId)
            throw new DomainException(
                "WINNER_MISMATCH", "The reported winner does not match the auction result.", 409);
        if (result.FinalPriceAmount is { } finalPrice && request.Amount is { } reportedAmount &&
            finalPrice != reportedAmount)
            throw new DomainException(
                "PAYMENT_AMOUNT_MISMATCH", "The payment amount does not match the auction result.", 409);
        // Quyết định của Seller hoặc huỷ đơn sau khi Buyer đã thanh toán (phiên đang Settled) xử lý riêng, không đi qua luồng thanh toán bên dưới.
        if (paymentStatus is SettlementPaymentStatus.SellerAccepted
            or SettlementPaymentStatus.SellerRejected
            or SettlementPaymentStatus.SellerExpired
            || (state.Status == AuctionStatus.Settled && paymentStatus == SettlementPaymentStatus.Cancelled))
        {
            return await HandleSellerDecisionAsync(
                auction, orderId, winnerId, paymentStatus, eventId, requestHash, now, cancellationToken);
        }

        if (state.Status != AuctionStatus.Ended)
            throw new DomainException(
                "AUCTION_NOT_AWAITING_SETTLEMENT", "The auction is not awaiting settlement.", 409);

        if (auction.AuctionSettlement?.OrderId is { } existingOrderId && existingOrderId != orderId)
            throw new DomainException(
                "ORDER_MISMATCH", "The reported order does not match the auction settlement.", 409);

        // Commerce đã báo thanh toán không thành công và đang chờ bidder kế tiếp phản hồi lời mời: phiên vẫn Ended nhưng settlement không còn chờ thanh toán.
        if (auction.AuctionSettlement is { Status: not AuctionSettlementStatus.PendingPayment })
            throw new DomainException(
                "AUCTION_NOT_AWAITING_SETTLEMENT", "The auction is not awaiting settlement.", 409);

        // Commerce says the payment window is over. The deadline Auction stored is the source of truth: if it has not passed, the two
        // systems disagree, and acting now could forfeit a deposit the buyer still has time to pay. Commerce retries later.
        if (paymentStatus is SettlementPaymentStatus.Expired or SettlementPaymentStatus.PaymentTimedOut
            && auction.AuctionSettlement?.PaymentDeadlineAt is { } storedDeadline
            && storedDeadline > now.Add(PaymentDeadlineTolerance))
            throw new DomainException(
                "PAYMENT_DEADLINE_NOT_REACHED", "The payment deadline stored for this auction has not passed yet.", 409);

        var previousStatus = state.Status;
        auction.AuctionSettlement ??= new AuctionSettlement
        {
            Id = Guid.NewGuid(),
            AuctionId = auctionId,
            Status = AuctionSettlementStatus.PendingPayment,
            CreatedAt = now,
            UpdatedAt = now
        };
        auction.AuctionSettlement.OrderId = orderId;
        auction.AuctionSettlement.WinnerId = result.WinnerId;
        auction.AuctionSettlement.WinningBidId = result.WinningBidId;
        auction.AuctionSettlement.Status = paymentStatus switch
        {
            SettlementPaymentStatus.Paid => AuctionSettlementStatus.Paid,
            SettlementPaymentStatus.Expired or SettlementPaymentStatus.PaymentTimedOut => AuctionSettlementStatus.Expired,
            SettlementPaymentStatus.Cancelled => AuctionSettlementStatus.Cancelled,
            _ => AuctionSettlementStatus.Failed
        };
        auction.AuctionSettlement.PaidAt = paymentStatus == SettlementPaymentStatus.Paid
            ? request.PaidAt ?? now
            : null;
        auction.AuctionSettlement.UpdatedAt = now;

        // Thanh toán Expired hoặc Failed: mời bidder hợp lệ kế tiếp (mỗi phiên chỉ có một lời mời). Không có ai thì phiên Failed như trước.
        var isSecondChanceNonPayment = string.Equals(request.OrderSource, "SECOND_CHANCE", StringComparison.OrdinalIgnoreCase)
            || await _dbContext.AuctionWinnerOffers
                .AsNoTracking()
                .AnyAsync(o => o.AuctionId == auctionId && o.BidderId == winnerId && o.Status == AuctionWinnerOfferStatus.Accepted, cancellationToken);

        AuctionWinnerOffer? offer = null;
        if (paymentStatus is SettlementPaymentStatus.Expired or SettlementPaymentStatus.PaymentTimedOut or SettlementPaymentStatus.Failed
            && !auction.AuctionSettlement.IsBuyNow
            && !auction.AuctionSettlement.IsBelowReserve)
        {
            offer = await TryCreateWinnerOfferAsync(auctionId, auction.ProductId, winnerId, now, cancellationToken);
        }

        // Đặt việc nền trong cùng transaction (Overview 4.1 & quy tắc giữ cọc Top 2):
        // - Paid: báo User reset Streak (API 25) + Capture cọc OrderPayment của người vừa thanh toán (Winner hoặc Người về nhì).
        // - Expired/Failed/PaymentTimedOut: báo User ghi nhận vi phạm (API 20) + Capture cọc Forfeit của người bỏ kèo (Winner hoặc Người về nhì).
        if (paymentStatus == SettlementPaymentStatus.Paid)
        {
            auction.StreakResetState = BackgroundSyncState.Pending;
            auction.NextRetryAt = now;
            auction.NeedsAttention = false;

            if (auction.AuctionSettlement.IsBuyNow && !string.IsNullOrWhiteSpace(auction.AuctionSettlement.BuyNowGoodsHoldId))
                auction.AuctionSettlement.BuyNowGoodsPendingAction = DepositPendingAction.Capture;

            var winnerDeposit = await _dbContext.AuctionDeposits
                .FirstOrDefaultAsync(d => d.AuctionId == auctionId && d.BidderId == winnerId && !d.IsDeleted, cancellationToken);
            if (winnerDeposit is not null && winnerDeposit.Status == DepositStatus.Authorized)
            {
                winnerDeposit.PendingAction = DepositPendingAction.Capture;
                winnerDeposit.PendingPurpose = "OrderPayment";
                winnerDeposit.NextRetryAt = now;
                winnerDeposit.NeedsAttention = false;
                winnerDeposit.UpdatedAt = now;
                winnerDeposit.RowVersion += 1;
            }
        }
        else if (paymentStatus is SettlementPaymentStatus.Expired or SettlementPaymentStatus.PaymentTimedOut or SettlementPaymentStatus.Failed)
        {
            auction.ViolationState = BackgroundSyncState.Pending;
            auction.PendingViolationBuyerId = winnerId;
            auction.PendingViolationOrderId = orderId;
            auction.PendingViolationOccurredAt = now;
            auction.PendingViolationIsSecondChance = isSecondChanceNonPayment;
            auction.NextRetryAt = now;
            auction.NeedsAttention = false;

            // Buy Now non-payment forfeits only the deposit and returns the remaining goods hold back to the buyer.
            if (auction.AuctionSettlement.IsBuyNow && !string.IsNullOrWhiteSpace(auction.AuctionSettlement.BuyNowGoodsHoldId))
                auction.AuctionSettlement.BuyNowGoodsPendingAction = DepositPendingAction.Release;

            var winnerDeposit = await _dbContext.AuctionDeposits
                .FirstOrDefaultAsync(d => d.AuctionId == auctionId && d.BidderId == winnerId && !d.IsDeleted, cancellationToken);
            if (winnerDeposit is not null && winnerDeposit.Status == DepositStatus.Authorized)
            {
                winnerDeposit.PendingAction = DepositPendingAction.Capture;
                winnerDeposit.PendingPurpose = "Forfeit";
                winnerDeposit.NextRetryAt = now;
                winnerDeposit.NeedsAttention = false;
                winnerDeposit.UpdatedAt = now;
                winnerDeposit.RowVersion += 1;
            }
        }
        else if (paymentStatus == SettlementPaymentStatus.Cancelled
                 && auction.AuctionSettlement.IsBuyNow
                 && !string.IsNullOrWhiteSpace(auction.AuctionSettlement.BuyNowGoodsHoldId))
        {
            auction.AuctionSettlement.BuyNowGoodsPendingAction = DepositPendingAction.Release;
        }

        if (offer is null)
        {
            // Phiên chỉ chuyển sang Settled khi có cả Paid và SellerAccepted. Khi mới Paid, phiên giữ ở Ended chờ Seller duyệt.
            state.Status = paymentStatus == SettlementPaymentStatus.Paid
                ? AuctionStatus.Ended
                : AuctionStatus.Failed;
            state.UpdatedAt = now;
            state.RowVersion += 1;

            // Thanh toán không thành công (Failed, Expired, Cancelled) mà không có bidder kế tiếp thì nhả sản phẩm để seller tạo lại phiên,
            // và mở khoá mọi cọc còn đang Held (ví dụ cọc của bidder khác không đủ điều kiện nhận lời mời).
            // Index unique theo ProductId bỏ qua xoá mềm nên phải xoá thật dòng này. Paid thì giữ nguyên vì sản phẩm đã bán.
            if (paymentStatus != SettlementPaymentStatus.Paid)
            {
                auction.StockReleaseState = BackgroundSyncState.Pending;
                auction.NextRetryAt = now;
                auction.NeedsAttention = false;

                // A cancelled order is not the buyer's fault, so the winner's deposit is released together with everyone else's.
                // Expired/Failed keep the winner out: that deposit is already queued for forfeit (or release for a second chance).
                if (paymentStatus == SettlementPaymentStatus.Cancelled)
                    await MarkOtherDepositsForReleaseAsync(auctionId, Guid.Empty, "ORDER_CANCELLED", now, cancellationToken);
                else
                    await MarkOtherDepositsForReleaseAsync(auctionId, winnerId, "AUCTION_FAILED", now, cancellationToken);

                var openAuction = await _dbContext.ProductOpenAuctions
                    .FirstOrDefaultAsync(p => p.AuctionId == auctionId, cancellationToken);
                if (openAuction is not null)
                    _dbContext.ProductOpenAuctions.Remove(openAuction);

                _dbContext.AuctionStatusHistories.Add(new AuctionStatusHistory
                {
                    Id = Guid.NewGuid(),
                    AuctionId = auctionId,
                    FromStatus = previousStatus,
                    ToStatus = state.Status,
                    Reason = $"Commerce payment status: {paymentStatus}.",
                    ChangedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }
        else
        {
            // Phiên vẫn Ended và sản phẩm vẫn được giữ cho tới khi bidder kế tiếp phản hồi hoặc lời mời hết hạn.
            state.UpdatedAt = now;

            _dbContext.AddAuditLog(
                AuditAction.WinnerOfferCreate, "Auction", auctionId.ToString(),
                oldValues: new { winnerId, orderId, paymentStatus = paymentStatus.ToString() },
                newValues: new
                {
                    offerId = offer.Id,
                    bidderId = offer.BidderId,
                    bidId = offer.BidId,
                    amount = offer.Amount,
                    expiresAt = offer.ExpiresAt
                });
        }

        _dbContext.AddAuditLog(
            AuditAction.AuctionSettle, "Auction", auctionId.ToString(),
            oldValues: new { status = previousStatus.ToString(), orderId },
            newValues: new { status = state.Status.ToString(), orderId, paymentStatus = paymentStatus.ToString(), offerId = offer?.Id });

        var response = new SettlementPaymentStatusResponse(
            auctionId, orderId, winnerId, previousStatus, state.Status, paymentStatus, now);
        _dbContext.IdempotencyKeys.Add(new IdempotencyKey
        {
            Id = Guid.NewGuid(),
            Key = eventId,
            Scope = IdempotencyScope.Settlement,
            RequestHash = requestHash,
            ResponseStatus = 200,
            ResponseBody = JsonSerializer.Serialize(response, JsonOptions),
            ExpiresAt = now.Add(SettlementIdempotencyLifetime),
            CreatedAt = now,
            UpdatedAt = now
        });

        // Queue outbox notifications before SaveAsync so they commit atomically with the settlement state.
        if (paymentStatus == SettlementPaymentStatus.Paid && result.WinningBidId is { } paidBidId)
        {
            _notifications.NotifyAuctionOrderPaid(new AuctionOrderPaidNotification(
                auctionId, result.Id, paidBidId, orderId, winnerId, auction.SellerId,
                auction.AuctionSettlement?.PaidAt ?? request.PaidAt ?? now, state.Status.ToString(),
                request.OrderNo, request.TotalAmount, request.SellerRespondDeadlineAt, null));
        }
        else if (offer is null && state.Status == AuctionStatus.Failed)
        {
            _notifications.NotifyAuctionFailed(new AuctionFailedNotification(
                auctionId, auction.SellerId, auction.SnapshotProductTitle,
                $"Commerce payment status: {paymentStatus}.", now, null));
        }
        else if (offer is not null)
        {
            _notifications.NotifyReplacementWinnerOffer(new ReplacementWinnerOfferNotification(
                auctionId, offer.Id, offer.BidId, offer.BidderId, winnerId, auction.SellerId,
                auction.SnapshotProductTitle, offer.Amount, offer.OfferedAt, offer.ExpiresAt, null));
        }

        await SaveAsync(null, cancellationToken);

        // Fast-path sau commit: thực thi việc nền cọc (Capture OrderPayment / Forfeit) và gọi User API 20 / API 25.
        await _deposits.ProcessDueDepositRetriesAsync(10, cancellationToken);

        if (paymentStatus == SettlementPaymentStatus.Paid)
        {
            await TryResetPaymentStreakAsync(
                auction, orderId, winnerId, isSecondChanceNonPayment, auction.AuctionSettlement.PaidAt ?? now, cancellationToken);
        }
        else if (paymentStatus is SettlementPaymentStatus.Expired or SettlementPaymentStatus.PaymentTimedOut or SettlementPaymentStatus.Failed)
        {
            await TryRecordNonPaymentViolationAsync(
                auction, orderId, winnerId, isSecondChanceNonPayment, auction.PendingViolationOccurredAt ?? now, cancellationToken);
        }

        // Đã lưu: báo trạng thái mới (Settled hoặc Failed) cho người đang xem phiên. Tạo lời mời thì phiên vẫn Ended nên không báo.
        // Callback gửi lại đã return ở nhánh idempotent nên không báo trùng.
        if (offer is null)
        {
            // Đã thanh toán thì chưa gọi Catalog: chờ Seller chấp nhận (SellerAccepted) mới chốt bán. Thất bại, hết hạn hoặc huỷ mà không còn bidder kế tiếp thì mở khoá.
            if (paymentStatus != SettlementPaymentStatus.Paid)
                _catalogSync.UnlockProduct(auction.ProductId, auctionId);

            await _notifier.AuctionStatusChangedAsync(
                auctionId, new AuctionStatusChangedMessage(auctionId, state.Status.ToString(), now), CancellationToken.None);
        }

        return response;
    }

    /// <summary>
    /// Xử lý quyết định của Seller do Commerce báo sau khi Buyer đã thanh toán (phiên đang Settled, settlement Paid).
    /// SellerAccepted: phiên giữ Settled, chốt bán sản phẩm ở Catalog (Auctioned, vĩnh viễn). SellerRejected hoặc SellerExpired: phiên chuyển Failed,
    /// settlement Cancelled, nhả sản phẩm ở Auction và mở khoá ở Catalog; không mời bidder kế tiếp vì lỗi do Seller.
    /// Seller đã chấp nhận thì không còn từ chối được (Catalog đã chốt Auctioned và không mở khoá được). Gọi lại SellerAccepted là an toàn.
    /// </summary>
    private async Task<SettlementPaymentStatusResponse> HandleSellerDecisionAsync(
        AuctionEntity auction, Guid orderId, Guid winnerId, SettlementPaymentStatus decision,
        string eventId, string requestHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var auctionId = auction.Id;
        var state = auction.AuctionState!;
        var settlement = auction.AuctionSettlement;

        if ((state.Status != AuctionStatus.Ended && state.Status != AuctionStatus.Settled)
            || settlement is null
            || settlement.Status != AuctionSettlementStatus.Paid)
            throw new DomainException(
                "AUCTION_NOT_AWAITING_SETTLEMENT", "The auction is not awaiting the seller's decision.", 409);
        if (settlement.OrderId != orderId)
            throw new DomainException(
                "ORDER_MISMATCH", "The reported order does not match the auction settlement.", 409);

        // Phiên không lưu riêng việc Seller đã chấp nhận, nên tra dấu vết audit để không cho từ chối sau khi đã chấp nhận.
        var auctionIdText = auctionId.ToString();
        var alreadyAccepted = await _dbContext.AuditLogs
            .AsNoTracking()
            .AnyAsync(a => a.Action == AuditAction.SellerAccept && a.EntityName == "Auction" && a.EntityId == auctionIdText, cancellationToken);

        var previousStatus = state.Status;
        if (decision == SettlementPaymentStatus.SellerAccepted)
        {
            if (state.Status != AuctionStatus.Settled)
            {
                state.Status = AuctionStatus.Settled;
                state.UpdatedAt = now;
                state.RowVersion += 1;

                _dbContext.AuctionStatusHistories.Add(new AuctionStatusHistory
                {
                    Id = Guid.NewGuid(),
                    AuctionId = auctionId,
                    FromStatus = previousStatus,
                    ToStatus = AuctionStatus.Settled,
                    Reason = "Seller accepted the paid auction order.",
                    ChangedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            // Khi phiên về trạng thái cuối Settled: mở khoá mọi cọc còn đang Held (ví dụ cọc của người về nhì).
            await MarkOtherDepositsForReleaseAsync(auctionId, winnerId, "AUCTION_SETTLED", now, cancellationToken);

            if (!alreadyAccepted)
            {
                _dbContext.AddAuditLog(
                    AuditAction.SellerAccept, "Auction", auctionIdText,
                    oldValues: new { status = previousStatus.ToString(), orderId },
                    newValues: new { status = state.Status.ToString(), orderId, sellerDecision = decision.ToString() });
            }
        }
        else
        {
            var failureReason = decision switch
            {
                SettlementPaymentStatus.SellerRejected => "The seller rejected the order.",
                SettlementPaymentStatus.SellerExpired => "The seller did not respond to the order in time.",
                _ => "The order was cancelled after payment."
            };

            settlement.Status = AuctionSettlementStatus.Cancelled;
            settlement.UpdatedAt = now;

            state.Status = AuctionStatus.Failed;
            state.UpdatedAt = now;
            state.RowVersion += 1;

            auction.StockReleaseState = BackgroundSyncState.Pending;
            auction.NextRetryAt = now;
            auction.NeedsAttention = false;

            // Hoàn phần cọc đã Capture về ví Buyer qua Wallet W5 (PendingAction = Refund, v0.4 mục 4.6).
            if (settlement.IsBuyNow && !string.IsNullOrWhiteSpace(settlement.BuyNowGoodsHoldId))
                settlement.BuyNowGoodsPendingAction = DepositPendingAction.Refund;

            var winnerDeposit = await _dbContext.AuctionDeposits
                .FirstOrDefaultAsync(d => d.AuctionId == auctionId && d.BidderId == winnerId && !d.IsDeleted, cancellationToken);
            if (winnerDeposit is not null && (winnerDeposit.Status == DepositStatus.Captured || winnerDeposit.Status == DepositStatus.Authorized))
            {
                winnerDeposit.PendingAction = winnerDeposit.Status == DepositStatus.Captured
                    ? DepositPendingAction.Refund
                    : DepositPendingAction.Release;
                winnerDeposit.ReleasePending = winnerDeposit.Status != DepositStatus.Captured;
                winnerDeposit.ReleaseReason = decision == SettlementPaymentStatus.SellerRejected ? "SELLER_REJECTED" : "SELLER_ACCEPT_EXPIRED";
                winnerDeposit.NextRetryAt = now;
                winnerDeposit.NeedsAttention = false;
                winnerDeposit.UpdatedAt = now;
                winnerDeposit.RowVersion += 1;
            }

            // Mở khoá mọi cọc còn đang Held của các bidder khác (ví dụ cọc của người về nhì).
            await MarkOtherDepositsForReleaseAsync(auctionId, winnerId, "AUCTION_FAILED", now, cancellationToken);

            // Nhả sản phẩm để seller tạo lại phiên. Index unique theo ProductId bỏ qua xoá mềm nên phải xoá thật dòng này.
            var openAuction = await _dbContext.ProductOpenAuctions
                .FirstOrDefaultAsync(p => p.AuctionId == auctionId, cancellationToken);
            if (openAuction is not null)
                _dbContext.ProductOpenAuctions.Remove(openAuction);

            _dbContext.AuctionStatusHistories.Add(new AuctionStatusHistory
            {
                Id = Guid.NewGuid(),
                AuctionId = auctionId,
                FromStatus = previousStatus,
                ToStatus = state.Status,
                Reason = failureReason,
                ChangedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });
            _dbContext.AddAuditLog(
                AuditAction.AuctionSettle, "Auction", auctionIdText,
                oldValues: new { status = previousStatus.ToString(), orderId, alreadyAccepted },
                newValues: new { status = state.Status.ToString(), orderId, sellerDecision = decision.ToString() });
        }

        var response = new SettlementPaymentStatusResponse(
            auctionId, orderId, winnerId, previousStatus, state.Status, decision, now);
        _dbContext.IdempotencyKeys.Add(new IdempotencyKey
        {
            Id = Guid.NewGuid(),
            Key = eventId,
            Scope = IdempotencyScope.Settlement,
            RequestHash = requestHash,
            ResponseStatus = 200,
            ResponseBody = JsonSerializer.Serialize(response, JsonOptions),
            ExpiresAt = now.Add(SettlementIdempotencyLifetime),
            CreatedAt = now,
            UpdatedAt = now
        });

        if (decision != SettlementPaymentStatus.SellerAccepted)
        {
            _notifications.NotifyAuctionFailed(new AuctionFailedNotification(
                auctionId, auction.SellerId, auction.SnapshotProductTitle,
                decision switch
                {
                    SettlementPaymentStatus.SellerRejected => "The seller rejected the order.",
                    SettlementPaymentStatus.SellerExpired => "The seller did not respond to the order in time.",
                    _ => "The order was cancelled after payment."
                },
                now, null));
        }

        await SaveAsync(null, cancellationToken);

        await _deposits.ProcessDueDepositRetriesAsync(10, cancellationToken);

        // Đã lưu. Chấp nhận: chốt bán ở Catalog. Từ chối, quá hạn hoặc đơn bị huỷ sau khi đã SellerAccepted: gọi revert-auctioned (hoặc unlock-auction nếu chưa chốt).
        if (decision == SettlementPaymentStatus.SellerAccepted)
        {
            _catalogSync.MarkProductAuctioned(auction.ProductId, auctionId);
            if (previousStatus != state.Status)
            {
                await _notifier.AuctionStatusChangedAsync(
                    auctionId, new AuctionStatusChangedMessage(auctionId, state.Status.ToString(), now), CancellationToken.None);
            }
        }
        else
        {
            if (alreadyAccepted)
                _catalogSync.RevertProductAuctioned(auction.ProductId, auctionId);
            else
                _catalogSync.UnlockProduct(auction.ProductId, auctionId);

            await _notifier.AuctionStatusChangedAsync(
                auctionId, new AuctionStatusChangedMessage(auctionId, state.Status.ToString(), now), CancellationToken.None);
        }

        return response;
    }

    /// <summary>
    /// Dành cho Catalog (R148): trả phiên đấu giá gần nhất của một sản phẩm kèm isOpen (chưa kết thúc: Draft, Scheduled, Running, Ended)
    /// và isSold (Settled). Sản phẩm chưa từng đấu giá trả về auctionId và status rỗng, isOpen và isSold là false.
    /// </summary>
    public async Task<ProductAuctionStatusDto> GetProductAuctionStatusAsync(
        Guid productId, CancellationToken cancellationToken = default)
    {
        var latest = await _dbContext.Auctions
            .AsNoTracking()
            .Where(a => a.ProductId == productId && !a.IsDeleted && a.AuctionState != null)
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Select(a => new { a.Id, Status = a.AuctionState!.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null)
            return new ProductAuctionStatusDto(productId, null, null, IsOpen: false, IsSold: false);

        // Open means the auction is not finished yet. Ended still counts as open: the winner has not paid,
        // so the product stays locked until the auction is Settled, Failed or Cancelled.
        var isOpen = latest.Status is AuctionStatus.Draft or AuctionStatus.Scheduled
            or AuctionStatus.Running or AuctionStatus.Ended;

        return new ProductAuctionStatusDto(
            productId, latest.Id, latest.Status, isOpen, IsSold: latest.Status == AuctionStatus.Settled);
    }

    /// <summary>
    /// Dành cho Catalog (R149): trả giá khởi điểm, bước giá, giá hiện tại, người đang dẫn đầu (id và tên che), số bid,
    /// giờ bắt đầu, giờ kết thúc và số lần gia hạn của một phiên. Trả 404 nếu phiên không tồn tại.
    /// </summary>
    public async Task<AuctionInternalSummaryDto> GetInternalSummaryAsync(
        Guid auctionId, CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.Auctions
            .AsNoTracking()
            .Where(a => a.Id == auctionId && !a.IsDeleted && a.AuctionState != null)
            .Select(a => new
            {
                a.Id,
                a.ProductId,
                a.SellerId,
                a.StartPriceAmount,
                a.BidStepAmount,
                StartsAt = a.ScheduledStartAt,
                Status = a.AuctionState!.Status,
                CurrentPriceAmount = a.AuctionState.CurrentPriceAmount,
                LeaderId = a.AuctionState.CurrentWinnerId,
                BidCount = a.AuctionState.BidCount,
                EndsAt = a.AuctionState.ScheduledEndAt,
                ExtensionCount = a.AuctionState.ExtensionCount
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
            throw NotFound(auctionId);

        var maskedName = row.LeaderId is { } leaderId
            ? await TryGetMaskedNameAsync(leaderId, cancellationToken)
            : null;

        return new AuctionInternalSummaryDto(
            row.Id, row.ProductId, row.SellerId, row.Status, row.StartPriceAmount, row.BidStepAmount,
            row.CurrentPriceAmount, row.LeaderId, maskedName, row.BidCount, row.StartsAt, row.EndsAt,
            row.ExtensionCount);
    }

    /// <summary>Lấy tên đã che của một người dùng từ User service. User service lỗi thì trả null thay vì ném lỗi, vì tên che chỉ là dữ liệu hiển thị.</summary>
    private async Task<string?> TryGetMaskedNameAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var infos = await _users.GetPublicInfosAsync(new[] { userId }, cancellationToken);
            return infos.TryGetValue(userId, out var info) ? info.MaskedName : null;
        }
        catch (DomainException)
        {
            return null;
        }
    }

    /// <summary>
    /// Truy vấn dùng chung cho danh sách phiên: lọc theo seller (nếu có) và theo trạng thái, sắp xếp theo giờ bắt đầu mới nhất, phân trang,
    /// rồi lấy ảnh chính từ Catalog. Catalog lỗi chỉ làm thiếu ảnh, không làm hỏng danh sách.
    /// </summary>
    private async Task<PagedData<AuctionSummaryDto>> ListAuctionsAsync(
        int page, int pageSize, AuctionListFilter listFilter, CancellationToken cancellationToken)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);
        var statusFilter = ParseStatus(listFilter.Status);

        if (listFilter.MinPrice is < 0m || listFilter.MaxPrice is < 0m)
            throw Invalid("VAL_INVALID_PRICE", "Price filter must be zero or greater.");
        if (listFilter.MinPrice is { } min && listFilter.MaxPrice is { } max && min > max)
            throw Invalid("VAL_INVALID_PRICE", "Minimum price cannot be greater than maximum price.");
        if (listFilter.EndingWithinMinutes is <= 0)
            throw Invalid("VAL_INVALID_SCHEDULE", "EndingWithinMinutes must be greater than zero.");

        var query = _dbContext.Auctions
            .AsNoTracking()
            .Where(a => !a.IsDeleted && a.AuctionState != null);

        if (listFilter.SellerId is { } owner && owner != Guid.Empty)
            query = query.Where(a => a.SellerId == owner);

        if (listFilter.ProductId is { } productId && productId != Guid.Empty)
            query = query.Where(a => a.ProductId == productId);

        if (statusFilter is { } filter)
            query = query.Where(a => a.AuctionState!.Status == filter);

        if (!string.IsNullOrWhiteSpace(listFilter.Keyword))
        {
            var kw = listFilter.Keyword.Trim().ToLowerInvariant();
            query = query.Where(a => a.SnapshotProductTitle != null && a.SnapshotProductTitle.ToLower().Contains(kw));
        }

        if (listFilter.MinPrice is { } minPrice)
            query = query.Where(a => (a.AuctionState!.CurrentPriceAmount ?? a.StartPriceAmount) >= minPrice);

        if (listFilter.MaxPrice is { } maxPrice)
            query = query.Where(a => (a.AuctionState!.CurrentPriceAmount ?? a.StartPriceAmount) <= maxPrice);

        if (listFilter.EndingWithinMinutes is { } minutes)
        {
            var now = _clock.GetUtcNow();
            var cutoff = now.AddMinutes(minutes);
            query = query.Where(a => a.AuctionState!.Status == AuctionStatus.Running
                && a.AuctionState.ScheduledEndAt > now
                && a.AuctionState.ScheduledEndAt <= cutoff);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var sortKey = listFilter.SortBy?.Trim().ToLowerInvariant();
        IOrderedQueryable<AuctionEntity> ordered = sortKey switch
        {
            "endingsoon" => query.OrderBy(a => a.AuctionState!.ScheduledEndAt).ThenBy(a => a.Id),
            "newest" => query.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id),
            "priceasc" => query.OrderBy(a => a.AuctionState!.CurrentPriceAmount ?? a.StartPriceAmount).ThenBy(a => a.Id),
            "pricedesc" => query.OrderByDescending(a => a.AuctionState!.CurrentPriceAmount ?? a.StartPriceAmount).ThenBy(a => a.Id),
            "mostbids" => query.OrderByDescending(a => a.AuctionState!.BidCount).ThenBy(a => a.Id),
            _ => query.OrderByDescending(a => a.ScheduledStartAt).ThenBy(a => a.Id)
        };

        var rows = await ordered
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
                a.ReservePriceAmount,
                a.BuyNowPriceAmount,
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
                r.ScheduledStartAt, r.ScheduledEndAt, r.CreatedAt,
                r.BuyNowPriceAmount,
                IsReserveMet: r.ReservePriceAmount is null || (r.CurrentPriceAmount.HasValue && r.CurrentPriceAmount.Value >= r.ReservePriceAmount.Value),
                BuyNowAvailable: AuctionRules.IsBuyNowAvailable(r.Status, r.ReservePriceAmount, r.BuyNowPriceAmount, r.CurrentPriceAmount, r.BidCount)))
            .ToList();

        return new PagedData<AuctionSummaryDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    /// <summary>
    /// Lấy chi tiết một phiên: dữ liệu từ DB cộng mô tả, thông số và ảnh từ Catalog. Catalog lỗi chỉ làm thiếu phần hiển thị đó. Trả 404 nếu không có phiên.
    /// Kèm RowVersion hiện tại của phiên để FE gửi làm If-Match khi đặt giá.
    /// </summary>
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
                a.ReservePriceAmount,
                a.BuyNowPriceAmount,
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
                EndedAt = a.AuctionState.EndedAt,
                RowVersion = a.AuctionState.RowVersion
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

        var isReserveMet = row.ReservePriceAmount is null
            || (row.CurrentPriceAmount.HasValue && row.CurrentPriceAmount.Value >= row.ReservePriceAmount.Value);
        var buyNowAvailable = AuctionRules.IsBuyNowAvailable(
            row.Status, row.ReservePriceAmount, row.BuyNowPriceAmount, row.CurrentPriceAmount, row.BidCount);

        return new AuctionDetailDto(
            row.Id, row.ProductId, row.SkuId, row.SellerId, row.SnapshotProductTitle ?? product?.Name,
            product?.Description, images.FirstOrDefault()?.Url, images, ParseSpecifications(product?.SpecificationsJson),
            row.Status, row.StartPriceAmount, row.BidStepAmount, row.CurrentPriceAmount, row.BidCount,
            row.ScheduledStartAt, row.OriginalEndAt, row.ScheduledEndAt, row.StartedAt, row.EndedAt,
            row.ExtensionCount, row.MaxExtensionCount, row.AntiSnipingExtensionMinutes,
            row.RecreatedFromAuctionId, row.CreatedAt, row.RowVersion,
            row.BuyNowPriceAmount,
            HasReservePrice: row.ReservePriceAmount.HasValue,
            IsReserveMet: isReserveMet,
            BuyNowAvailable: buyNowAvailable);
    }

    // ------------------------------------------------------------------ Commands

    /// <summary>
    /// Seller tạo phiên mới (trạng thái Scheduled). Kiểm tra giá và lịch, hỏi User service seller có đủ điều kiện không, kiểm tra sản phẩm
    /// chưa có phiên đang mở và hợp lệ với Catalog, lấy cấu hình chống bắn tỉa hiện hành rồi lưu phiên cùng lịch sử trạng thái và audit.
    /// </summary>
    public async Task<AuctionCreatedResponse> CreateAsync(
        ActorContext actor, CreateAuctionRequest request, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        // 1. Input checks (no database, no other service).
        if (request.SkuId is not { } skuId || skuId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "SKU ID is required.");
        AuctionRules.ValidateStartPrice(request.StartPriceAmount);
        if (request.BidStepAmount is not null)
            AuctionRules.ValidateBidStep(request.BidStepAmount);
        AuctionRules.ValidateReserveAndBuyNowPrices(request.StartPriceAmount!.Value, request.ReservePriceAmount, request.BuyNowPriceAmount);
        AuctionRules.ValidateSchedule(request.ScheduledStartAt, request.ScheduledEndAt, now);

        // 2. User service & reserve restriction: the caller must be an approved seller with enough reputation.
        await EnsureSellerEligibleAsync(actor.UserId, cancellationToken);
        await EnsureSellerCanSetReservePriceAsync(actor.UserId, request.ReservePriceAmount, now, cancellationToken);

        // 3. Catalog: the variant must be an active auction item of this seller. Catalog also tells which product it belongs to,
        //    then one open auction per product is checked.
        var eligible = await EnsureVariantEligibleAsync(actor.UserId, skuId, cancellationToken);
        var productId = eligible.ProductId;
        await EnsureProductHasNoOpenAuctionAsync(productId, cancellationToken);
        var title = eligible.Title;
        var policy = await GetPolicyAsync(cancellationToken);

        // Seller không nhập bước giá thì dùng % mặc định của giá khởi điểm (Admin cấu hình); số tiền được lưu cố định vào phiên.
        var bidStep = AuctionRules.ResolveBidStep(request.StartPriceAmount, request.BidStepAmount, policy.DefaultBidStepPercent);

        var created = NewAuction(
            actor.UserId, productId, skuId, title, recreatedFromAuctionId: null,
            request.StartPriceAmount!.Value, bidStep,
            request.ReservePriceAmount, request.BuyNowPriceAmount,
            request.ScheduledStartAt!.Value, request.ScheduledEndAt!.Value, policy, now);

        _dbContext.Auctions.Add(created.Auction);
        _dbContext.AuctionStatusHistories.Add(created.History);
        _dbContext.AddAuditLog(
            AuditAction.AuctionCreate, "Auction", created.Auction.Id.ToString(),
            newValues: Snapshot(created.Auction, AuctionStatus.Scheduled),
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        // Giữ sản phẩm ở Catalog (InAuction) và tạo phiên cùng thành công hoặc cùng thất bại: khoá trước, lưu phiên sau.
        await _catalog.LockProductForAuctionAsync(productId, cancellationToken);
        try
        {
            await SaveAsync(productId, cancellationToken);
        }
        catch
        {
            // Lưu phiên thất bại: mở khoá lại sản phẩm (gửi nền, có thử lại).
            _catalogSync.UnlockProduct(productId, created.Auction.Id);
            throw;
        }

        var a = created.Auction;
        return new AuctionCreatedResponse(
            a.Id, a.ProductId, a.SkuId, a.SnapshotProductTitle, AuctionStatus.Scheduled,
            a.StartPriceAmount, a.BidStepAmount, a.ScheduledStartAt, a.OriginalEndAt,
            a.AntiSnipingExtensionMinutes, a.MaxExtensionCount, a.CreatedAt,
            a.ReservePriceAmount, a.BuyNowPriceAmount);
    }

    /// <summary>
    /// Seller sửa giá, bước giá hoặc lịch của phiên của mình khi phiên còn Scheduled và chưa tới giờ bắt đầu.
    /// Giá trị gửi lên được gộp với giá trị hiện có rồi kiểm tra lại như khi tạo; ghi audit giá trị cũ và mới.
    /// </summary>
    public async Task<AuctionUpdatedResponse> UpdateAsync(
        ActorContext actor, Guid auctionId, UpdateAuctionRequest request, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        if (request.StartPriceAmount is null && request.BidStepAmount is null &&
            request.ScheduledStartAt is null && request.ScheduledEndAt is null &&
            request.ReservePriceAmount is null && request.BuyNowPriceAmount is null)
            throw Invalid("VAL_NO_FIELDS_TO_UPDATE", "At least one field must be provided.");

        var auction = await LoadForWriteAsync(auctionId, cancellationToken);
        EnsureOwner(auction, actor, "update");
        var state = auction.AuctionState!;

        AuctionRules.EnsureNotStarted(state.Status, auction.ScheduledStartAt, now,
            "AUCTION_NOT_EDITABLE", "Auction can only be edited before it starts.", 409);

        var startPrice = request.StartPriceAmount ?? auction.StartPriceAmount;
        var bidStep = request.BidStepAmount ?? auction.BidStepAmount;
        var reservePrice = request.ReservePriceAmount ?? auction.ReservePriceAmount;
        var buyNowPrice = request.BuyNowPriceAmount ?? auction.BuyNowPriceAmount;
        var start = request.ScheduledStartAt ?? auction.ScheduledStartAt;
        var end = request.ScheduledEndAt ?? state.ScheduledEndAt;

        // The same checks as when creating the auction.
        AuctionRules.ValidatePrices(startPrice, bidStep);
        AuctionRules.ValidateReserveAndBuyNowPrices(startPrice, reservePrice, buyNowPrice);
        if (request.ReservePriceAmount is not null)
            await EnsureSellerCanSetReservePriceAsync(actor.UserId, request.ReservePriceAmount, now, cancellationToken);
        AuctionRules.ValidateSchedule(start, end, now);

        var oldValues = Snapshot(auction, state.Status);
        var changed = new List<string>();
        if (startPrice != auction.StartPriceAmount) changed.Add("startPriceAmount");
        if (bidStep != auction.BidStepAmount) changed.Add("bidStepAmount");
        if (reservePrice != auction.ReservePriceAmount) changed.Add("reservePriceAmount");
        if (buyNowPrice != auction.BuyNowPriceAmount) changed.Add("buyNowPriceAmount");
        if (start != auction.ScheduledStartAt) changed.Add("scheduledStartAt");
        if (end != state.ScheduledEndAt) changed.Add("scheduledEndAt");

        auction.StartPriceAmount = startPrice;
        auction.BidStepAmount = bidStep;
        auction.ReservePriceAmount = reservePrice;
        auction.BuyNowPriceAmount = buyNowPrice;
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
            auction.ScheduledStartAt, state.ScheduledEndAt, now,
            auction.ReservePriceAmount, auction.BuyNowPriceAmount);
    }

    /// <summary>
    /// Seller huỷ phiên của mình (API 24): được phép khi phiên Scheduled, hoặc đang Running nhưng chưa có lượt đặt giá nào.
    /// Đổi trạng thái sang Cancelled, đặt ReleasePending = true cho mọi cọc của phiên, nhả sản phẩm, ghi lịch sử, audit và báo realtime AuctionCancelled.
    /// </summary>
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

        if (state.Status == AuctionStatus.Cancelled)
            return new AuctionCancelledResponse(auction.Id, AuctionStatus.Cancelled, AuctionStatus.Cancelled, reason, state.UpdatedAt, 0, "QUEUED");

        if (state.Status is AuctionStatus.Ended or AuctionStatus.Settled or AuctionStatus.Failed)
            throw new DomainException("AUCTION_ALREADY_ENDED", "The auction has already ended and cannot be cancelled.", 409);

        if (state.BidCount > 0)
            throw new DomainException("AUCTION_HAS_BIDS", "A seller cannot cancel an auction that already has bids.", 409);

        if (state.Status is not (AuctionStatus.Scheduled or AuctionStatus.Running))
            throw new BusinessRuleValidationException(
                "INVALID_STATE_TRANSITION", "Auction can only be cancelled when it is scheduled or running without bids.", 422);

        var fromStatus = state.Status;
        var oldValues = Snapshot(auction, fromStatus);

        state.Status = AuctionStatus.Cancelled;
        state.UpdatedAt = now;
        state.RowVersion += 1;
        auction.StockReleaseState = BackgroundSyncState.Pending;
        auction.NextRetryAt = now;
        auction.NeedsAttention = false;
        auction.UpdatedAt = now;
        auction.UpdatedBy = actor.UserId;

        var depositsToRelease = await MarkDepositsForReleaseAsync(auction.Id, "AUCTION_CANCELLED", now, actor, cancellationToken);

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
            oldValues: oldValues, newValues: new { status = AuctionStatus.Cancelled.ToString(), reason, depositsToRelease },
            changedColumns: new[] { "status" },
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        _notifications.NotifyAuctionCancelled(new AuctionCancelledNotification(
            auction.Id, auction.SellerId, auction.SnapshotProductTitle, reason, now, false, actor.TraceId));

        await SaveAsync(null, cancellationToken);

        // Giải phóng cọc ngay sau commit (fast path); lỗi mạng giữ ReleasePending = true để retry sau.
        if (depositsToRelease > 0)
            await _deposits.ReleasePendingDepositsForAuctionAsync(auction.Id, cancellationToken);

        // Phiên đã được huỷ và lưu: mở khoá sản phẩm ở Catalog (gửi nền, có thử lại).
        _catalogSync.UnlockProduct(auction.ProductId, auction.Id);

        // Báo cho người đang xem phiên để họ dừng đặt giá.
        await _notifier.AuctionCancelledAsync(
            auction.Id, new AuctionCancelledMessage(auction.Id, now), CancellationToken.None);

        return new AuctionCancelledResponse(auction.Id, fromStatus, state.Status, reason, now, depositsToRelease, "QUEUED");
    }

    /// <summary>
    /// Admin huỷ cưỡng chế phiên chưa kết thúc (Draft, Scheduled, Running), kể cả phiên đã có người đặt giá (API 24). Bắt buộc có lý do.
    /// Đổi trạng thái sang Cancelled, đặt ReleasePending = true cho mọi cọc của phiên, nhả sản phẩm, ghi lịch sử, audit (đánh dấu forced) và báo realtime AuctionCancelled. Không trừ uy tín seller.
    /// </summary>
    public async Task<AuctionCancelledResponse> ForceCancelAsync(
        ActorContext actor, Guid auctionId, ForceCancelAuctionRequest request, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
            throw Invalid("REASON_REQUIRED", "Reason is required.");
        if (reason.Length > MaxReasonLength)
            throw Invalid("VAL_INVALID_REASON", $"Reason must be at most {MaxReasonLength} characters.");

        var auction = await LoadForWriteAsync(auctionId, cancellationToken);
        var state = auction.AuctionState!;

        if (state.Status == AuctionStatus.Cancelled)
            return new AuctionCancelledResponse(auction.Id, AuctionStatus.Cancelled, AuctionStatus.Cancelled, reason, state.UpdatedAt, 0, "QUEUED");

        if (state.Status is AuctionStatus.Ended or AuctionStatus.Settled or AuctionStatus.Failed)
            throw new DomainException("AUCTION_ALREADY_ENDED", "Only an auction that has not ended can be cancelled.", 409);

        // Admin huỷ được phiên chưa kết thúc, kể cả phiên đang chạy đã có người đặt giá.
        if (state.Status is not (AuctionStatus.Draft or AuctionStatus.Scheduled or AuctionStatus.Running))
            throw new BusinessRuleValidationException(
                "INVALID_STATE_TRANSITION", "Only an auction that has not ended can be cancelled.", 422);

        var fromStatus = state.Status;
        var oldValues = Snapshot(auction, fromStatus);

        state.Status = AuctionStatus.Cancelled;
        state.UpdatedAt = now;
        state.RowVersion += 1;
        auction.StockReleaseState = BackgroundSyncState.Pending;
        auction.NextRetryAt = now;
        auction.NeedsAttention = false;
        auction.UpdatedAt = now;
        auction.UpdatedBy = actor.UserId;

        var depositsToRelease = await MarkDepositsForReleaseAsync(auction.Id, "ADMIN_FORCE_CANCEL", now, actor, cancellationToken);

        // Nhả sản phẩm để có thể đưa vào phiên mới. Index unique theo ProductId bỏ qua xoá mềm nên phải xoá thật dòng này.
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
            oldValues: oldValues, newValues: new { status = AuctionStatus.Cancelled.ToString(), reason, forced = true, depositsToRelease },
            changedColumns: new[] { "status" },
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        _notifications.NotifyAuctionCancelled(new AuctionCancelledNotification(
            auction.Id, auction.SellerId, auction.SnapshotProductTitle, reason, now, true, actor.TraceId));

        // When admin/staff manually force-cancels an auction, resolve any pending reports as ActionTaken and thank reporters.
        var pendingReports = await _dbContext.AuctionReports
            .Where(r => r.AuctionId == auction.Id && !r.IsDeleted && r.Status == AuctionReportStatus.Pending)
            .ToListAsync(cancellationToken);
        if (pendingReports.Count > 0)
        {
            var thankYouMessage = $"Thank you for reporting \"{auction.SnapshotProductTitle ?? "this auction"}\". An administrator verified the issue and cancelled the auction.";
            foreach (var report in pendingReports)
            {
                report.Status = AuctionReportStatus.ActionTaken;
                report.AdminNote = reason;
                report.ReviewedBy = actor.UserId;
                report.ReviewedAt = now;
                report.UpdatedAt = now;
            }

            _notifications.NotifyAuctionReportResolved(new AuctionReportResolvedNotification(
                auction.Id,
                auction.SnapshotProductTitle,
                AuctionReportStatus.ActionTaken.ToString(),
                thankYouMessage,
                reason,
                pendingReports.Select(r => r.ReporterId).Distinct().ToList(),
                now,
                actor.TraceId));
        }

        await SaveAsync(null, cancellationToken);

        // Giải phóng cọc ngay sau commit (fast path); lỗi mạng giữ ReleasePending = true để retry sau.
        if (depositsToRelease > 0)
            await _deposits.ReleasePendingDepositsForAuctionAsync(auction.Id, cancellationToken);

        // Phiên đã bị huỷ và lưu: mở khoá sản phẩm ở Catalog (gửi nền, có thử lại).
        _catalogSync.UnlockProduct(auction.ProductId, auction.Id);

        // Báo cho người đang xem phiên để họ dừng đặt giá. Không trừ uy tín seller.
        await _notifier.AuctionCancelledAsync(
            auction.Id, new AuctionCancelledMessage(auction.Id, now), CancellationToken.None);

        return new AuctionCancelledResponse(auction.Id, fromStatus, state.Status, reason, now, depositsToRelease, "QUEUED");
    }

    /// <summary>Đánh dấu các cọc đang Authorized hoặc Pending của một phiên sang trạng thái Released và bật ReleasePending trong cùng transaction.</summary>
    private async Task<int> MarkDepositsForReleaseAsync(
        Guid auctionId, string releaseReason, DateTimeOffset now, ActorContext actor, CancellationToken cancellationToken)
    {
        var deposits = await _dbContext.AuctionDeposits
            .Where(d => d.AuctionId == auctionId && !d.IsDeleted &&
                        (d.Status == DepositStatus.Authorized || d.Status == DepositStatus.Pending))
            .ToListAsync(cancellationToken);

        foreach (var deposit in deposits)
        {
            var oldStatus = deposit.Status;
            deposit.Status = DepositStatus.Released;
            deposit.ReleaseReason = releaseReason;
            // Always set ReleasePending = true and PendingAction = Release: a Pending deposit without GatewayReference may still have locked funds at Wallet if W1 timed out.
            deposit.ReleasePending = true;
            deposit.PendingAction = DepositPendingAction.Release;
            deposit.NextRetryAt = now;
            deposit.NeedsAttention = false;
            deposit.UpdatedAt = now;
            deposit.RowVersion += 1;

            _dbContext.AddAuditLog(
                AuditAction.DepositRelease, "AuctionDeposit", deposit.Id.ToString(),
                oldValues: new { status = oldStatus },
                newValues: new { depositId = deposit.Id, status = deposit.Status, releaseReason, releasePending = deposit.ReleasePending },
                userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);
        }

        return deposits.Count;
    }

    /// <summary>Mở khoá mọi cọc còn đang Authorized hoặc Pending của các bidder khác ngoài excludedBidderId khi phiên về trạng thái cuối (Settled hoặc Failed).</summary>
    private async Task<int> MarkOtherDepositsForReleaseAsync(
        Guid auctionId, Guid excludedBidderId, string releaseReason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deposits = await _dbContext.AuctionDeposits
            .Where(d => d.AuctionId == auctionId && !d.IsDeleted && d.BidderId != excludedBidderId &&
                        (d.Status == DepositStatus.Authorized || d.Status == DepositStatus.Pending))
            .ToListAsync(cancellationToken);

        foreach (var deposit in deposits)
        {
            var oldStatus = deposit.Status;
            deposit.Status = DepositStatus.Released;
            deposit.ReleaseReason = releaseReason;
            deposit.ReleasePending = true;
            deposit.PendingAction = DepositPendingAction.Release;
            deposit.NextRetryAt = now;
            deposit.NeedsAttention = false;
            deposit.UpdatedAt = now;
            deposit.RowVersion += 1;

            _dbContext.AddAuditLog(
                AuditAction.DepositRelease, "AuctionDeposit", deposit.Id.ToString(),
                oldValues: new { status = oldStatus },
                newValues: new { depositId = deposit.Id, status = deposit.Status, releaseReason, releasePending = deposit.ReleasePending });
        }

        return deposits.Count;
    }

    /// <summary>
    /// Bidder được mời chấp nhận trở thành người thắng thay người thắng trước (ví dụ người thắng trước bị ban hoặc không thanh toán).
    /// Chỉ đúng bidder được mời mới phản hồi được; lời mời phải còn Pending, chưa quá hạn và phiên phải còn Ended.
    /// Khi chấp nhận: lời mời chuyển Accepted; AuctionResult và trạng thái phiên chuyển sang bidder này với giá của bid của họ;
    /// settlement được đặt lại cho bidder này (PendingPayment, hạn thanh toán 24 giờ, bỏ OrderId cũ để Commerce tạo đơn mới).
    /// Phiên vẫn ở Ended cho tới khi Commerce báo kết quả thanh toán. Ghi audit giá trị cũ và mới.
    /// </summary>
    public async Task<WinnerOfferResponse> AcceptWinnerOfferAsync(
        ActorContext actor, Guid auctionId, Guid offerId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var (auction, offer) = await LoadPendingOfferAsync(actor, auctionId, offerId, now, cancellationToken);
        var state = auction.AuctionState!;

        var result = auction.AuctionResult;
        if (result is null || result.IsDeleted)
            throw new DomainException(
                "AUCTION_RESULT_NOT_AVAILABLE", "The result of this auction has not been determined yet.", 404);

        var profile = await _users.GetAuctionDepositProfileAsync(actor.UserId, bypassCache: true, cancellationToken);
        DepositProfileGuard.EnsureCanJoin(profile);

        // Kiểm tra lại với Catalog xem sản phẩm có bị Admin cấm/xoá giữa chừng trước khi cho người về nhì tạo đơn thanh toán hay không.
        var productEligibility = await _catalog.CheckProductAuctionEligibilityAsync(
            auction.ProductId, allowInAuction: true, cancellationToken);
        if (!productEligibility.IsEligible)
            throw NotEligible(ReasonMessage(productEligibility.Reason));

        var oldValues = new
        {
            winnerId = result.WinnerId,
            winningBidId = result.WinningBidId,
            finalPriceAmount = result.FinalPriceAmount,
            orderId = auction.AuctionSettlement?.OrderId
        };

        offer.Status = AuctionWinnerOfferStatus.Accepted;
        offer.SettlementState = BackgroundSyncState.Pending;
        offer.NextRetryAt = now;
        offer.NeedsAttention = false;
        offer.RespondedAt = now;
        offer.UpdatedAt = now;

        // Bidder được mời trở thành người thắng với giá của chính bid của họ.
        result.WinnerId = offer.BidderId;
        result.WinningBidId = offer.BidId;
        result.FinalPriceAmount = offer.Amount;
        result.DeterminedAt = now;
        result.UpdatedAt = now;

        state.CurrentWinnerId = offer.BidderId;
        state.CurrentPriceAmount = offer.Amount;
        state.UpdatedAt = now;
        state.RowVersion += 1;

        // Settlement chỉ có một dòng cho mỗi phiên nên đặt lại cho người thắng mới và mở cửa sổ thanh toán mới.
        var paymentDeadline = now.Add(WinnerPaymentWindow);
        var settlement = auction.AuctionSettlement ??= new AuctionSettlement
        {
            Id = Guid.NewGuid(),
            AuctionId = auctionId,
            Status = AuctionSettlementStatus.PendingPayment,
            CreatedAt = now,
            UpdatedAt = now
        };
        settlement.WinnerId = offer.BidderId;
        settlement.WinningBidId = offer.BidId;
        settlement.Status = AuctionSettlementStatus.PendingPayment;
        settlement.OrderId = null;
        settlement.PaidAt = null;
        settlement.PaymentDeadlineAt = paymentDeadline;
        settlement.UpdatedAt = now;

        _dbContext.AddAuditLog(
            AuditAction.WinnerOfferAccept, "Auction", auctionId.ToString(),
            oldValues: oldValues,
            newValues: new
            {
                offerId,
                winnerId = offer.BidderId,
                winningBidId = offer.BidId,
                finalPriceAmount = offer.Amount,
                paymentDeadlineAt = paymentDeadline
            },
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        await SaveAsync(null, cancellationToken);

        // Người thắng mới đã nhận lời mời: đẩy settlement mới sang Commerce để tạo đơn (gửi nền, có thử lại).
        _commerceSync.PushSettlement(auctionId);

        return new WinnerOfferResponse(
            offer.Id, auctionId, offer.Status, state.Status, offer.Amount, now, paymentDeadline);
    }

    /// <summary>
    /// Bidder được mời từ chối lời mời trở thành người thắng. Lý do là tuỳ chọn (tối đa 1000 ký tự).
    /// Lời mời chuyển Declined; phiên chuyển từ Ended sang Failed, ghi lịch sử trạng thái, nhả sản phẩm (xoá dòng phiên mở)
    /// để seller có thể tạo lại phiên, ghi audit và báo realtime AuctionStatusChanged cho người đang xem phiên.
    /// </summary>
    public async Task<WinnerOfferResponse> DeclineWinnerOfferAsync(
        ActorContext actor, Guid auctionId, Guid offerId, DeclineWinnerOfferRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        var reason = request.Reason?.Trim();
        if (reason is { Length: > MaxReasonLength })
            throw Invalid("VAL_INVALID_REASON", $"Reason must be at most {MaxReasonLength} characters.");

        var (auction, offer) = await LoadPendingOfferAsync(actor, auctionId, offerId, now, cancellationToken);
        var state = auction.AuctionState!;
        var fromStatus = state.Status;

        offer.Status = AuctionWinnerOfferStatus.Declined;
        offer.RespondedAt = now;
        offer.ResponseReason = string.IsNullOrEmpty(reason) ? null : reason;
        offer.UpdatedAt = now;

        state.Status = AuctionStatus.Failed;
        state.UpdatedAt = now;
        state.RowVersion += 1;
        auction.StockReleaseState = BackgroundSyncState.Pending;
        auction.NextRetryAt = now;
        auction.NeedsAttention = false;

        // Người về nhì từ chối lời mời: không tính là bỏ kèo, mở khoá toàn bộ cọc còn đang giữ của phiên (cọc của người về nhì).
        var depositsToRelease = await MarkDepositsForReleaseAsync(auctionId, "SECOND_CHANCE_DECLINED", now, actor, cancellationToken);

        // Nhả sản phẩm để có thể tạo lại phiên. Index unique theo ProductId bỏ qua xoá mềm nên phải xoá thật dòng này.
        var openAuction = await _dbContext.ProductOpenAuctions
            .FirstOrDefaultAsync(p => p.AuctionId == auctionId, cancellationToken);
        if (openAuction is not null)
            _dbContext.ProductOpenAuctions.Remove(openAuction);

        _dbContext.AuctionStatusHistories.Add(new AuctionStatusHistory
        {
            Id = Guid.NewGuid(),
            AuctionId = auctionId,
            FromStatus = fromStatus,
            ToStatus = AuctionStatus.Failed,
            Reason = "The next bidder declined the winner offer.",
            ChangedBy = actor.UserId,
            ChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        _dbContext.AddAuditLog(
            AuditAction.WinnerOfferDecline, "Auction", auctionId.ToString(),
            oldValues: new { status = fromStatus.ToString(), offerStatus = AuctionWinnerOfferStatus.Pending.ToString() },
            newValues: new { status = AuctionStatus.Failed.ToString(), offerId, offerStatus = AuctionWinnerOfferStatus.Declined.ToString(), reason = offer.ResponseReason },
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        _notifications.NotifyAuctionFailed(new AuctionFailedNotification(
            auctionId, auction.SellerId, auction.SnapshotProductTitle,
            "The invited replacement bidder declined the winner offer.", now, actor.TraceId));

        await SaveAsync(null, cancellationToken);

        if (depositsToRelease > 0)
            await _deposits.ReleasePendingDepositsForAuctionAsync(auctionId, cancellationToken);

        // Đã lưu: mở khoá sản phẩm ở Catalog (gửi nền, có thử lại), rồi báo cho người đang xem phiên rằng phiên đã chuyển sang Failed.
        _catalogSync.UnlockProduct(auction.ProductId, auctionId);

        await _notifier.AuctionStatusChangedAsync(
            auctionId, new AuctionStatusChangedMessage(auctionId, state.Status.ToString(), now), CancellationToken.None);

        return new WinnerOfferResponse(offer.Id, auctionId, offer.Status, state.Status, offer.Amount, now, null);
    }

    /// <summary>
    /// Người về nhì chấp nhận mua theo giá bid của chính mình (API 07: POST /api/v1/auctions/{id}/second-chance/accept).
    /// Bắt buộc có Idempotency-Key và acceptedPrice khớp với giá mời. Nếu đã Accepted cùng người thì trả 200 idempotent.
    /// </summary>
    public async Task<SecondChanceAcceptResponse> AcceptSecondChanceOfferAsync(
        ActorContext actor, Guid auctionId, string? idempotencyKey, AcceptSecondChanceRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        if (auctionId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "Auction ID is required.");
        BidRules.ValidateIdempotencyKey(idempotencyKey);
        if (request.AcceptedPrice is not { } acceptedPrice || acceptedPrice <= 0m)
            throw Invalid("VALIDATION_ERROR", "Accepted price must be greater than zero.");

        var key = idempotencyKey!.Trim();
        var requestHash = HashSecondChanceRequest("ACCEPT", auctionId, actor.UserId, acceptedPrice.ToString("0.00", CultureInfo.InvariantCulture));
        var existingKey = await _dbContext.IdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == key && k.Scope == IdempotencyScope.SecondChanceOffer && k.ExpiresAt > now, cancellationToken);
        if (existingKey is not null)
        {
            if (existingKey.RequestHash != requestHash)
                throw new BusinessRuleValidationException(
                    "IDEMPOTENCY_KEY_REUSED", "The Idempotency-Key was already used with a different request.", 422);

            if (!string.IsNullOrWhiteSpace(existingKey.ResponseBody))
            {
                var savedResp = JsonSerializer.Deserialize<SecondChanceAcceptResponse>(existingKey.ResponseBody, JsonOptions);
                if (savedResp is not null)
                    return savedResp;
            }
        }

        var auction = await _dbContext.Auctions
            .Include(a => a.AuctionState)
            .Include(a => a.AuctionResult)
            .Include(a => a.AuctionSettlement)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (auction?.AuctionState is null)
            throw NotFound(auctionId);

        var offer = await _dbContext.AuctionWinnerOffers
            .FirstOrDefaultAsync(o => o.AuctionId == auctionId && !o.IsDeleted, cancellationToken);
        if (offer is null)
            throw new DomainException("OFFER_NOT_FOUND", "The auction does not have a second-chance offer.", 404);

        if (offer.BidderId != actor.UserId)
            throw new DomainException("NOT_SECOND_CHANCE_BIDDER", "Only the invited second-chance bidder can respond to this offer.", 403);

        if (offer.Status == AuctionWinnerOfferStatus.Accepted)
        {
            var existingDeadline = auction.AuctionSettlement?.PaymentDeadlineAt ?? offer.RespondedAt?.Add(WinnerPaymentWindow) ?? now.Add(WinnerPaymentWindow);
            var existingOrder = auction.AuctionSettlement?.OrderId;
            return new SecondChanceAcceptResponse(
                offer.Id,
                "Accepted",
                existingOrder.HasValue ? "CREATED" : "PENDING",
                existingOrder,
                offer.Amount,
                existingDeadline);
        }

        if (offer.Status == AuctionWinnerOfferStatus.Declined)
            throw new DomainException("OFFER_ALREADY_RESOLVED", "The second-chance offer has already been declined.", 409);

        if (offer.Status == AuctionWinnerOfferStatus.Expired || offer.ExpiresAt <= now)
            throw new DomainException("OFFER_EXPIRED", "The second-chance offer has expired.", 409);

        if (auction.AuctionState.Status != AuctionStatus.Ended)
            throw new BusinessRuleValidationException(
                "INVALID_STATE_TRANSITION", "The auction is not awaiting a second-chance response.", 422);

        if (acceptedPrice != offer.Amount)
            throw new BusinessRuleValidationException(
                "PRICE_MISMATCH", "Accepted price does not match the offer price.", 422);

        var profile = await _users.GetAuctionDepositProfileAsync(actor.UserId, bypassCache: true, cancellationToken);
        DepositProfileGuard.EnsureCanJoin(profile);

        var productEligibility = await _catalog.CheckProductAuctionEligibilityAsync(
            auction.ProductId, allowInAuction: true, cancellationToken);
        if (!productEligibility.IsEligible)
            throw NotEligible(ReasonMessage(productEligibility.Reason));

        var result = auction.AuctionResult;
        if (result is null || result.IsDeleted)
            throw new DomainException(
                "AUCTION_RESULT_NOT_AVAILABLE", "The result of this auction has not been determined yet.", 404);

        var state = auction.AuctionState;
        var oldValues = new
        {
            winnerId = result.WinnerId,
            winningBidId = result.WinningBidId,
            finalPriceAmount = result.FinalPriceAmount,
            orderId = auction.AuctionSettlement?.OrderId
        };

        offer.Status = AuctionWinnerOfferStatus.Accepted;
        offer.SettlementState = BackgroundSyncState.Pending;
        offer.NextRetryAt = now;
        offer.NeedsAttention = false;
        offer.RespondedAt = now;
        offer.UpdatedAt = now;

        result.WinnerId = offer.BidderId;
        result.WinningBidId = offer.BidId;
        result.FinalPriceAmount = offer.Amount;
        result.DeterminedAt = now;
        result.UpdatedAt = now;

        state.CurrentWinnerId = offer.BidderId;
        state.CurrentPriceAmount = offer.Amount;
        state.UpdatedAt = now;
        state.RowVersion += 1;

        var paymentDeadline = now.Add(WinnerPaymentWindow);
        var settlement = auction.AuctionSettlement ??= new AuctionSettlement
        {
            Id = Guid.NewGuid(),
            AuctionId = auctionId,
            Status = AuctionSettlementStatus.PendingPayment,
            CreatedAt = now,
            UpdatedAt = now
        };
        settlement.WinnerId = offer.BidderId;
        settlement.WinningBidId = offer.BidId;
        settlement.Status = AuctionSettlementStatus.PendingPayment;
        settlement.OrderId = null;
        settlement.PaidAt = null;
        settlement.PaymentDeadlineAt = paymentDeadline;
        settlement.UpdatedAt = now;

        var response = new SecondChanceAcceptResponse(
            offer.Id,
            "Accepted",
            "PENDING",
            null,
            offer.Amount,
            paymentDeadline);

        _dbContext.IdempotencyKeys.Add(new IdempotencyKey
        {
            Id = Guid.NewGuid(),
            Key = key,
            Scope = IdempotencyScope.SecondChanceOffer,
            RequestHash = requestHash,
            ResponseStatus = 200,
            ResponseBody = JsonSerializer.Serialize(response, JsonOptions),
            ExpiresAt = now.Add(SettlementIdempotencyLifetime),
            CreatedAt = now,
            UpdatedAt = now
        });

        _dbContext.AddAuditLog(
            AuditAction.WinnerOfferAccept, "Auction", auctionId.ToString(),
            oldValues: oldValues,
            newValues: new
            {
                offerId = offer.Id,
                winnerId = offer.BidderId,
                winningBidId = offer.BidId,
                finalPriceAmount = offer.Amount,
                paymentDeadlineAt = paymentDeadline
            },
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        await SaveAsync(null, cancellationToken);

        _commerceSync.PushSettlement(auctionId);

        return response;
    }

    /// <summary>
    /// Người về nhì từ chối lời mời mua lại (API 08: POST /api/v1/auctions/{id}/second-chance/decline).
    /// Phiên chuyển ngay sang Failed, nhả kho ở Catalog và không tính là vi phạm Streak.
    /// </summary>
    public async Task<SecondChanceDeclineResponse> DeclineSecondChanceOfferAsync(
        ActorContext actor, Guid auctionId, string? idempotencyKey, DeclineWinnerOfferRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        if (auctionId == Guid.Empty)
            throw Invalid("VAL_INVALID_IDENTIFIER", "Auction ID is required.");
        BidRules.ValidateIdempotencyKey(idempotencyKey);

        var reason = request.Reason?.Trim();
        if (reason is { Length: > MaxReasonLength })
            throw Invalid("VAL_INVALID_REASON", $"Reason must be at most {MaxReasonLength} characters.");

        var key = idempotencyKey!.Trim();
        var requestHash = HashSecondChanceRequest("DECLINE", auctionId, actor.UserId, reason ?? string.Empty);
        var existingKey = await _dbContext.IdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == key && k.Scope == IdempotencyScope.SecondChanceOffer && k.ExpiresAt > now, cancellationToken);
        if (existingKey is not null)
        {
            if (existingKey.RequestHash != requestHash)
                throw new BusinessRuleValidationException(
                    "IDEMPOTENCY_KEY_REUSED", "The Idempotency-Key was already used with a different request.", 422);

            if (!string.IsNullOrWhiteSpace(existingKey.ResponseBody))
            {
                var savedResp = JsonSerializer.Deserialize<SecondChanceDeclineResponse>(existingKey.ResponseBody, JsonOptions);
                if (savedResp is not null)
                    return savedResp;
            }
        }

        var auction = await _dbContext.Auctions
            .Include(a => a.AuctionState)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (auction?.AuctionState is null)
            throw NotFound(auctionId);

        var offer = await _dbContext.AuctionWinnerOffers
            .FirstOrDefaultAsync(o => o.AuctionId == auctionId && !o.IsDeleted, cancellationToken);
        if (offer is null)
            throw new DomainException("OFFER_NOT_FOUND", "The auction does not have a second-chance offer.", 404);

        if (offer.BidderId != actor.UserId)
            throw new DomainException("NOT_SECOND_CHANCE_BIDDER", "Only the invited second-chance bidder can respond to this offer.", 403);

        var state = auction.AuctionState;
        if (offer.Status == AuctionWinnerOfferStatus.Declined)
        {
            return new SecondChanceDeclineResponse(offer.Id, "Declined", state.Status.ToString());
        }

        if (offer.Status == AuctionWinnerOfferStatus.Accepted)
            throw new DomainException("OFFER_ALREADY_RESOLVED", "The second-chance offer has already been accepted.", 409);

        if (offer.Status == AuctionWinnerOfferStatus.Expired || offer.ExpiresAt <= now)
            throw new DomainException("OFFER_EXPIRED", "The second-chance offer has expired.", 409);

        if (state.Status != AuctionStatus.Ended)
            throw new BusinessRuleValidationException(
                "INVALID_STATE_TRANSITION", "The auction is not awaiting a second-chance response.", 422);

        var fromStatus = state.Status;
        offer.Status = AuctionWinnerOfferStatus.Declined;
        offer.RespondedAt = now;
        offer.ResponseReason = string.IsNullOrEmpty(reason) ? null : reason;
        offer.UpdatedAt = now;

        state.Status = AuctionStatus.Failed;
        state.UpdatedAt = now;
        state.RowVersion += 1;
        auction.StockReleaseState = BackgroundSyncState.Pending;
        auction.NextRetryAt = now;
        auction.NeedsAttention = false;

        // Người về nhì từ chối lời mời: không tính là bỏ kèo, mở khoá toàn bộ cọc còn đang giữ của phiên (cọc của người về nhì).
        var depositsToRelease = await MarkDepositsForReleaseAsync(auctionId, "SECOND_CHANCE_DECLINED", now, actor, cancellationToken);

        var openAuction = await _dbContext.ProductOpenAuctions
            .FirstOrDefaultAsync(p => p.AuctionId == auctionId, cancellationToken);
        if (openAuction is not null)
            _dbContext.ProductOpenAuctions.Remove(openAuction);

        _dbContext.AuctionStatusHistories.Add(new AuctionStatusHistory
        {
            Id = Guid.NewGuid(),
            AuctionId = auctionId,
            FromStatus = fromStatus,
            ToStatus = AuctionStatus.Failed,
            Reason = "The second-chance bidder declined the offer.",
            ChangedBy = actor.UserId,
            ChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        var response = new SecondChanceDeclineResponse(offer.Id, "Declined", state.Status.ToString());
        _dbContext.IdempotencyKeys.Add(new IdempotencyKey
        {
            Id = Guid.NewGuid(),
            Key = key,
            Scope = IdempotencyScope.SecondChanceOffer,
            RequestHash = requestHash,
            ResponseStatus = 200,
            ResponseBody = JsonSerializer.Serialize(response, JsonOptions),
            ExpiresAt = now.Add(SettlementIdempotencyLifetime),
            CreatedAt = now,
            UpdatedAt = now
        });

        _dbContext.AddAuditLog(
            AuditAction.WinnerOfferDecline, "Auction", auctionId.ToString(),
            oldValues: new { status = fromStatus.ToString(), offerStatus = AuctionWinnerOfferStatus.Pending.ToString() },
            newValues: new { status = AuctionStatus.Failed.ToString(), offerId = offer.Id, offerStatus = AuctionWinnerOfferStatus.Declined.ToString(), reason = offer.ResponseReason },
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        _notifications.NotifyAuctionFailed(new AuctionFailedNotification(
            auctionId, auction.SellerId, auction.SnapshotProductTitle,
            "The invited second-chance bidder declined the offer.", now, actor.TraceId));

        await SaveAsync(null, cancellationToken);

        if (depositsToRelease > 0)
            await _deposits.ReleasePendingDepositsForAuctionAsync(auctionId, cancellationToken);

        _catalogSync.UnlockProduct(auction.ProductId, auctionId);

        await _notifier.AuctionStatusChangedAsync(
            auctionId, new AuctionStatusChangedMessage(auctionId, state.Status.ToString(), now), CancellationToken.None);

        return response;
    }

    /// <summary>
    /// Lấy các lời mời trở thành người thắng của chính bidder, mới nhất trước, có phân trang và lọc theo trạng thái lời mời (tuỳ chọn).
    /// Mỗi dòng có IsExpired để client biết lời mời còn Pending nhưng đã quá hạn.
    /// </summary>
    public async Task<PagedData<WinnerOfferDto>> ListMyWinnerOffersAsync(
        Guid bidderId, int page = 1, int pageSize = 20, string? status = null, CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);
        var statusFilter = ParseOfferStatus(status);
        var now = _clock.GetUtcNow();

        var query = _dbContext.AuctionWinnerOffers
            .AsNoTracking()
            .Where(o => o.BidderId == bidderId && !o.IsDeleted && !o.Auction!.IsDeleted);

        if (statusFilter is { } filter)
            query = query.Where(o => o.Status == filter);

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(o => o.OfferedAt)
            .ThenByDescending(o => o.Id)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(o => new
            {
                OfferId = o.Id,
                o.AuctionId,
                Title = o.Auction!.SnapshotProductTitle,
                o.BidId,
                o.Amount,
                o.Status,
                o.OfferedAt,
                o.ExpiresAt,
                o.RespondedAt,
                o.ResponseReason
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(r => new WinnerOfferDto(
                r.OfferId, r.AuctionId, r.Title, r.BidId, r.Amount, r.Status,
                r.Status == AuctionWinnerOfferStatus.Pending && r.ExpiresAt <= now,
                r.OfferedAt, r.ExpiresAt, r.RespondedAt, r.ResponseReason))
            .ToList();

        return new PagedData<WinnerOfferDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    /// <summary>
    /// Dành cho Commerce: trả thông tin chốt kết quả của một phiên để tạo order, gồm sản phẩm, SKU, seller, người thắng,
    /// bid thắng, giá chốt, hạn thanh toán, trạng thái settlement và trạng thái phiên. Người thắng luôn lấy từ AuctionResult
    /// (nguồn mà callback thanh toán đối chiếu). Phiên không tồn tại trả 404 AUCTION_NOT_FOUND; phiên chưa chốt người thắng
    /// trả 404 SETTLEMENT_NOT_AVAILABLE.
    /// </summary>
    public async Task<AuctionSettlementDto> GetSettlementAsync(Guid auctionId, CancellationToken cancellationToken = default)
    {
        var row = await SelectSettlementRows(
                WithSettlement(_dbContext.Auctions.AsNoTracking()).Where(a => a.Id == auctionId))
            .FirstOrDefaultAsync(cancellationToken);

        if (row is not null)
            return ToSettlementDto(row);

        var exists = await _dbContext.Auctions
            .AsNoTracking()
            .AnyAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (!exists)
            throw NotFound(auctionId);

        throw new DomainException(
            "SETTLEMENT_NOT_AVAILABLE", "The settlement of this auction is not available yet.", 404);
    }

    /// <summary>
    /// Dành cho Commerce: liệt kê các phiên đã chốt người thắng, cũ nhất trước (để không bỏ sót phiên nào), có phân trang.
    /// Lọc theo trạng thái settlement (tuỳ chọn; PendingPayment chỉ tính phiên còn Ended, tức đang chờ thanh toán) và theo
    /// thời điểm chốt từ determinedFrom trở đi (tuỳ chọn, để quét tăng dần).
    /// </summary>
    public async Task<PagedData<AuctionSettlementDto>> ListSettlementsAsync(
        int page = 1, int pageSize = 20, string? status = null, DateTimeOffset? determinedFrom = null,
        CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);
        var statusFilter = ParseSettlementStatus(status);

        var query = WithSettlement(_dbContext.Auctions.AsNoTracking());

        if (statusFilter is { } filter)
        {
            query = query.Where(a => a.AuctionSettlement!.Status == filter);
            if (filter == AuctionSettlementStatus.PendingPayment)
                query = query.Where(a => a.AuctionState!.Status == AuctionStatus.Ended);
        }

        if (determinedFrom is { } from)
            query = query.Where(a => a.AuctionResult!.DeterminedAt >= from);

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await SelectSettlementRows(
                query.OrderBy(a => a.AuctionResult!.DeterminedAt)
                    .ThenBy(a => a.Id)
                    .Skip((safePage - 1) * safePageSize)
                    .Take(safePageSize))
            .ToListAsync(cancellationToken);

        var items = rows.Select(ToSettlementDto).ToList();

        return new PagedData<AuctionSettlementDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    /// <summary>
    /// Seller tạo lại phiên mới từ một phiên Failed của mình với giá và lịch mới. Kiểm tra seller, sản phẩm và lịch như khi tạo;
    /// phiên mới ghi lại phiên gốc trong RecreatedFromAuctionId.
    /// </summary>
    public async Task<AuctionRecreatedResponse> RecreateAsync(
        ActorContext actor, Guid auctionId, RecreateAuctionRequest request, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        AuctionRules.ValidateStartPrice(request.StartPriceAmount);
        if (request.BidStepAmount is not null)
            AuctionRules.ValidateBidStep(request.BidStepAmount);
        AuctionRules.ValidateSchedule(request.ScheduledStartAt, request.ScheduledEndAt, now);

        var source = await LoadForWriteAsync(auctionId, cancellationToken);
        EnsureOwner(source, actor, "recreate");

        if (source.AuctionState!.Status != AuctionStatus.Failed)
            throw new BusinessRuleValidationException(
                "INVALID_RECREATE_SOURCE", "Only a failed auction can be recreated.", 422);

        await EnsureSellerEligibleAsync(actor.UserId, cancellationToken);
        await EnsureSellerCanSetReservePriceAsync(actor.UserId, request.ReservePriceAmount, now, cancellationToken);
        await EnsureProductHasNoOpenAuctionAsync(source.ProductId, cancellationToken);

        AuctionRules.ValidateReserveAndBuyNowPrices(request.StartPriceAmount!.Value, request.ReservePriceAmount, request.BuyNowPriceAmount);

        if (source.SkuId is not { } sourceSkuId)
            throw NotEligible("The auction has no SKU to check.");
        var eligible = await EnsureVariantEligibleAsync(actor.UserId, sourceSkuId, cancellationToken);
        if (eligible.ProductId != source.ProductId)
            throw NotEligible("The SKU no longer belongs to the product of the original auction.");
        var title = eligible.Title;
        var policy = await GetPolicyAsync(cancellationToken);
        var bidStep = AuctionRules.ResolveBidStep(request.StartPriceAmount, request.BidStepAmount, policy.DefaultBidStepPercent);

        var created = NewAuction(
            actor.UserId, source.ProductId, source.SkuId, title ?? source.SnapshotProductTitle, source.Id,
            request.StartPriceAmount!.Value, bidStep,
            request.ReservePriceAmount, request.BuyNowPriceAmount,
            request.ScheduledStartAt!.Value, request.ScheduledEndAt!.Value, policy, now);

        _dbContext.Auctions.Add(created.Auction);
        _dbContext.AuctionStatusHistories.Add(created.History);
        _dbContext.AddAuditLog(
            AuditAction.AuctionCreate, "Auction", created.Auction.Id.ToString(),
            newValues: Snapshot(created.Auction, AuctionStatus.Scheduled),
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        // Giữ sản phẩm ở Catalog (InAuction) và tạo phiên cùng thành công hoặc cùng thất bại: khoá trước, lưu phiên sau.
        await _catalog.LockProductForAuctionAsync(source.ProductId, cancellationToken);
        try
        {
            await SaveAsync(source.ProductId, cancellationToken);
        }
        catch
        {
            // Lưu phiên thất bại: mở khoá lại sản phẩm (gửi nền, có thử lại).
            _catalogSync.UnlockProduct(source.ProductId, created.Auction.Id);
            throw;
        }

        var a = created.Auction;
        return new AuctionRecreatedResponse(
            a.Id, source.Id, a.ProductId, a.SkuId, AuctionStatus.Scheduled,
            a.StartPriceAmount, a.BidStepAmount, a.ScheduledStartAt, a.OriginalEndAt, a.CreatedAt,
            a.ReservePriceAmount, a.BuyNowPriceAmount);
    }

    // ------------------------------------------------------------------ Checks with other services

    /// <summary>Hỏi User service seller có được duyệt và đủ uy tín để tạo phiên không. Chưa được duyệt trả 403 SELLER_NOT_APPROVED, các lý do khác đổi thành lỗi tương ứng.</summary>
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
    /// Hỏi Catalog xem SKU (variant) có tồn tại, đang hoạt động, là hàng đấu giá và thuộc về seller không. Không hợp lệ thì ném PRODUCT_NOT_ELIGIBLE (422).
    /// Client chỉ gửi SKU; sản phẩm (ProductId) và tên sản phẩm được lấy từ Catalog.
    /// </summary>
    private async Task<EligibleVariant> EnsureVariantEligibleAsync(
        Guid sellerId, Guid skuId, CancellationToken cancellationToken)
    {
        var variantIds = new[] { skuId };

        var check = (await _catalog.CheckVariantsForAuctionAsync(variantIds, cancellationToken))
            .FirstOrDefault(c => c.VariantId == skuId);
        if (check is null)
            throw NotEligible("The product variant was not found.");
        if (check.ProductId == Guid.Empty)
            throw new DomainException("CATALOG_RESPONSE_INVALID", "Catalog returned a variant without a product.", 502);
        if (check.SaleType is not null && !string.Equals(check.SaleType, "Auction", StringComparison.OrdinalIgnoreCase))
            throw NotEligible(ReasonMessage("NOT_FOR_AUCTION"));
        if (!check.IsPurchasable)
            throw NotEligible(ReasonMessage(check.Reason));

        var variant = (await _catalog.GetVariantsAsync(variantIds, cancellationToken))
            .FirstOrDefault(v => v.VariantId == skuId);
        if (variant is null)
            throw NotEligible("The product variant was not found.");
        // Both answers come from Catalog: they must agree on the product.
        if (variant.ProductId != check.ProductId)
            throw new DomainException("CATALOG_RESPONSE_INVALID", "Catalog returned inconsistent product data for the variant.", 502);
        if (variant.SellerId != sellerId)
            throw NotEligible("The product does not belong to the seller.");

        var name = variant.ProductName?.Trim();
        var title = string.IsNullOrEmpty(name) ? null : name.Length > MaxTitleLength ? name[..MaxTitleLength] : name;
        return new EligibleVariant(check.ProductId, title);
    }

    /// <summary>Kết quả kiểm tra SKU với Catalog: sản phẩm chứa SKU và tên sản phẩm (đã cắt theo độ dài tối đa).</summary>
    private sealed record EligibleVariant(Guid ProductId, string? Title);

    /// <summary>Tạo lỗi PRODUCT_NOT_ELIGIBLE (422) với thông điệp cho trước.</summary>
    private static DomainException NotEligible(string message) => new("PRODUCT_NOT_ELIGIBLE", message, 422);

    /// <summary>Đổi mã lý do không hợp lệ của Catalog (NOT_FOR_AUCTION, PRODUCT_NOT_ACTIVE, VARIANT_NOT_ACTIVE, DELETED) thành thông điệp tiếng Anh gửi cho client.</summary>
    private static string ReasonMessage(string? reason) => reason switch
    {
        "NOT_FOR_AUCTION" => "The product is not an auction product.",
        "PRODUCT_NOT_ACTIVE" => "The product is not active.",
        "VARIANT_NOT_ACTIVE" => "The product variant is not active.",
        "DELETED" => "The product has been deleted.",
        "BANNED" or "PRODUCT_BANNED" => "The product has been banned.",
        "SUSPENDED" or "PRODUCT_SUSPENDED" => "The product has been suspended.",
        _ => "The product is not eligible for auction."
    };

    /// <summary>Lấy ảnh chính theo SKU từ Catalog cho danh sách phiên. Catalog lỗi (5xx) thì trả danh sách rỗng vì ảnh chỉ là dữ liệu hiển thị.</summary>
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

    /// <summary>Lấy mô tả, thông số và ảnh của sản phẩm từ Catalog. Catalog lỗi (5xx) thì trả null vì đây chỉ là dữ liệu hiển thị.</summary>
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

    /// <summary>Chuyển chuỗi JSON thông số kỹ thuật thành JsonElement. Chuỗi rỗng hoặc không phải JSON hợp lệ thì trả null.</summary>
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

    /// <summary>Nạp phiên cùng trạng thái phiên (có theo dõi thay đổi) để ghi. Không có thì ném 404 AUCTION_NOT_FOUND.</summary>
    private async Task<AuctionEntity> LoadForWriteAsync(Guid auctionId, CancellationToken cancellationToken)
    {
        var auction = await _dbContext.Auctions
            .Include(a => a.AuctionState)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);

        if (auction?.AuctionState is null)
            throw NotFound(auctionId);

        return auction;
    }

    /// <summary>Chỉ seller sở hữu phiên mới được sửa hoặc huỷ phiên đó, người khác nhận 403 FORBIDDEN. Admin dùng API huỷ cưỡng chế.</summary>
    private static void EnsureOwner(AuctionEntity auction, ActorContext actor, string verb)
    {
        if (auction.SellerId != actor.UserId)
            throw new DomainException("FORBIDDEN", $"Only the seller of the auction can {verb} it.", 403);
    }

    /// <summary>Đảm bảo sản phẩm chưa có phiên đang mở. Đã có thì ném 409 PRODUCT_ALREADY_IN_AUCTION.</summary>
    private async Task EnsureProductHasNoOpenAuctionAsync(Guid productId, CancellationToken cancellationToken)
    {
        var hasOpenAuction = await _dbContext.ProductOpenAuctions
            .AsNoTracking()
            .AnyAsync(p => p.ProductId == productId, cancellationToken);

        if (hasOpenAuction)
            throw new DomainException("PRODUCT_ALREADY_IN_AUCTION", "The product already has an open auction.", 409);
    }

    /// <summary>
    /// Lưu thay đổi và đổi lỗi DB thành lỗi nghiệp vụ: xung đột đồng thời (RowVersion) thành 409 CONCURRENT_UPDATE;
    /// vi phạm index unique theo sản phẩm (hai seller cùng giành một sản phẩm) thành 409 PRODUCT_ALREADY_IN_AUCTION.
    /// </summary>
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

    /// <summary>
    /// Đọc cấu hình chống bắn tỉa hiện hành: dòng DEFAULT_AUCTION_POLICY đang active có version cao nhất. Thiếu cấu hình thì ném lỗi.
    /// Chỉ đọc số phút gia hạn và số lần gia hạn tối đa; ngưỡng uy tín thuộc User service.
    /// </summary>
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

        // Only the anti-sniping settings and the default bid step are read. Reputation settings belong to the User service.
        return new AuctionPolicy(
            ReadInt(root, "antiSnipingExtensionMinutes"),
            ReadInt(root, "maxExtensionCount"),
            ReadOptionalPercent(root, "defaultBidStepPercent", AuctionRules.FallbackDefaultBidStepPercent));
    }

    /// <summary>Đọc một số phần trăm (lớn hơn 0 và tối đa 100) theo tên từ JSON cấu hình. Thiếu trường thì dùng giá trị mặc định; có nhưng sai thì ném lỗi cấu hình không hợp lệ.</summary>
    private static decimal ReadOptionalPercent(JsonElement root, string name, decimal fallback)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
            return fallback;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var percent) && percent > 0m && percent <= 100m)
            return percent;

        throw new InvalidOperationException($"Auction policy configuration is invalid: '{name}' must be a number above 0 and at most 100.");
    }

    /// <summary>Đọc một số nguyên theo tên từ JSON cấu hình. Thiếu hoặc sai kiểu thì ném lỗi cấu hình không hợp lệ.</summary>
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

    /// <summary>
    /// Dựng phiên mới ở trạng thái Scheduled, gồm trạng thái phiên (RowVersion = 1), dòng ProductOpenAuction giữ sản phẩm,
    /// cấu hình chống bắn tỉa sao từ policy, và dòng lịch sử trạng thái đầu tiên. Chỉ tạo đối tượng, chưa lưu vào DB.
    /// </summary>
    private static (AuctionEntity Auction, AuctionStatusHistory History) NewAuction(
        Guid sellerId, Guid productId, Guid? skuId, string? title, Guid? recreatedFromAuctionId,
        decimal startPriceAmount, decimal bidStepAmount, decimal? reservePriceAmount, decimal? buyNowPriceAmount,
        DateTimeOffset start, DateTimeOffset end,
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
            ReservePriceAmount = reservePriceAmount,
            BuyNowPriceAmount = buyNowPriceAmount,
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

    /// <summary>Tạo bản chụp các thông tin chính của phiên (trạng thái, giá, lịch...) để ghi vào audit log làm giá trị cũ hoặc mới.</summary>
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

    /// <summary>Tạo lỗi nghiệp vụ 400 với mã lỗi và thông điệp cho trước.</summary>
    private static DomainException Invalid(string errorCode, string message) => new(errorCode, message, 400);

    /// <summary>Băm nội dung callback thanh toán (phiên, đơn, người thắng, trạng thái, số tiền, giờ thanh toán) để phát hiện cùng eventId nhưng khác nội dung.</summary>
    private static string HashSettlementRequest(Guid auctionId, SettlementPaymentStatusRequest request)
    {
        var text = string.Join('|',
            auctionId.ToString("N"),
            request.OrderId?.ToString("N"),
            request.WinnerId?.ToString("N"),
            request.Status?.ToString(),
            request.Amount?.ToString("0.00", CultureInfo.InvariantCulture),
            request.PaidAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        if (request.OrderNo is not null || request.TotalAmount is not null || request.SellerRespondDeadlineAt is not null)
        {
            text += "|v2|" + string.Join('|',
                request.OrderNo,
                request.TotalAmount?.ToString("0.00", CultureInfo.InvariantCulture),
                request.SellerRespondDeadlineAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Đọc lại kết quả callback đã lưu cho eventId. Nội dung rỗng hoặc hỏng thì trả null.</summary>
    private static SettlementPaymentStatusResponse? DeserializeSettlementResponse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SettlementPaymentStatusResponse>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Tạo lỗi 404 AUCTION_NOT_FOUND cho phiên không tồn tại.</summary>
    private static EntityNotFoundException NotFound(Guid auctionId) => new("AUCTION_NOT_FOUND", "Auction", auctionId);

    /// <summary>
    /// Nạp phiên (kèm trạng thái, kết quả, settlement) và lời mời để bidder phản hồi, rồi kiểm tra điều kiện:
    /// lời mời phải thuộc phiên (404 WINNER_OFFER_NOT_FOUND), đúng bidder được mời (403 FORBIDDEN),
    /// còn Pending (409 WINNER_OFFER_NOT_PENDING), chưa quá hạn (410 WINNER_OFFER_EXPIRED) và phiên còn Ended (422 INVALID_STATE_TRANSITION).
    /// </summary>
    private async Task<(AuctionEntity Auction, AuctionWinnerOffer Offer)> LoadPendingOfferAsync(
        ActorContext actor, Guid auctionId, Guid offerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var auction = await _dbContext.Auctions
            .Include(a => a.AuctionState)
            .Include(a => a.AuctionResult)
            .Include(a => a.AuctionSettlement)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);

        if (auction?.AuctionState is null)
            throw NotFound(auctionId);

        var offer = await _dbContext.AuctionWinnerOffers
            .FirstOrDefaultAsync(o => o.Id == offerId && o.AuctionId == auctionId && !o.IsDeleted, cancellationToken);

        if (offer is null)
            throw new EntityNotFoundException("WINNER_OFFER_NOT_FOUND", "Winner offer", offerId);

        if (offer.BidderId != actor.UserId)
            throw new DomainException("FORBIDDEN", "Only the invited bidder can respond to this offer.", 403);

        if (offer.Status != AuctionWinnerOfferStatus.Pending)
            throw new DomainException(
                "WINNER_OFFER_NOT_PENDING", "The winner offer was already answered or has expired.", 409);

        if (offer.ExpiresAt <= now)
            throw new DomainException("WINNER_OFFER_EXPIRED", "The winner offer has expired.", 410);

        if (auction.AuctionState.Status != AuctionStatus.Ended)
            throw new BusinessRuleValidationException(
                "INVALID_STATE_TRANSITION", "The auction is not awaiting a winner.", 422);

        return (auction, offer);
    }

    /// <summary>
    /// Tìm bidder hợp lệ kế tiếp để mời mua thay người thắng trước và thêm lời mời Pending vào DbContext (chưa lưu).
    /// Mỗi phiên chỉ có một lời mời (chỉ mục unique theo AuctionId), nên phiên đã từng có lời mời thì trả null.
    /// Người được xét là bidder khác người thắng trước, mỗi người tính bid hợp lệ cao nhất của họ (bằng giá thì bid đến trước), từ cao xuống thấp,
    /// tối đa MaxOfferCandidates người; người đầu tiên được User service xác nhận còn đủ điều kiện sẽ nhận lời mời theo giá bid đó, hạn phản hồi 24 giờ.
    /// User service không trả lời thì ném lỗi 503 và không lưu gì, Commerce gửi lại callback với cùng eventId. Không có ai đủ điều kiện thì trả null.
    /// </summary>
    private async Task<AuctionWinnerOffer?> TryCreateWinnerOfferAsync(
        Guid auctionId, Guid productId, Guid previousWinnerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var alreadyOffered = await _dbContext.AuctionWinnerOffers
            .AnyAsync(o => o.AuctionId == auctionId, cancellationToken);
        if (alreadyOffered)
            return null;

        // Kiểm tra sản phẩm bên Catalog vẫn còn hợp lệ (không bị Admin ban/suspend/delete giữa chừng) trước khi mời người về nhì.
        var productEligibility = await _catalog.CheckProductAuctionEligibilityAsync(
            productId, allowInAuction: true, cancellationToken);
        if (!productEligibility.IsEligible)
            return null;

        var bids = await _dbContext.BidHistories
            .AsNoTracking()
            .Where(h => h.AuctionId == auctionId && !h.IsInvalidated && !h.IsDeleted && h.BidderId != previousWinnerId)
            .OrderByDescending(h => h.Amount)
            .ThenBy(h => h.SequenceNo)
            .Select(h => new { h.BidId, h.BidderId, h.Amount })
            .ToListAsync(cancellationToken);

        var candidates = bids
            .GroupBy(b => b.BidderId)
            .Select(g => g.First())
            .Take(MaxOfferCandidates)
            .ToList();

        foreach (var candidate in candidates)
        {
            // Quy tắc giữ cọc: Người về nhì hợp lệ bắt buộc phải còn cọc Authorized (đang Held) trong phiên.
            var hasHeldDeposit = await _dbContext.AuctionDeposits
                .AsNoTracking()
                .AnyAsync(d => d.AuctionId == auctionId && d.BidderId == candidate.BidderId && !d.IsDeleted && d.Status == DepositStatus.Authorized, cancellationToken);
            if (!hasHeldDeposit)
                continue;

            UserAuctionDepositProfile profile;
            try
            {
                profile = await _users.GetAuctionDepositProfileAsync(candidate.BidderId, bypassCache: true, cancellationToken);
            }
            catch (DomainException ex) when (ex.StatusCode == 404)
            {
                continue;
            }

            if (!string.Equals(profile.AccountStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase) || profile.IsRestricted)
                continue;

            var offer = new AuctionWinnerOffer
            {
                Id = Guid.NewGuid(),
                AuctionId = auctionId,
                BidId = candidate.BidId,
                BidderId = candidate.BidderId,
                Amount = candidate.Amount,
                Status = AuctionWinnerOfferStatus.Pending,
                OfferedAt = now,
                ExpiresAt = now.AddHours(AuctionRules.WinnerOfferWindowHours),
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.AuctionWinnerOffers.Add(offer);
            return offer;
        }

        return null;
    }

    /// <summary>Băm nội dung yêu cầu phản hồi lời mời người về nhì để kiểm tra Idempotency-Key.</summary>
    private static string HashSecondChanceRequest(string action, Guid auctionId, Guid userId, string payload)
    {
        var text = $"{action}|{auctionId:N}|{userId:N}|{payload}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Gọi UserService (API 20) để ghi nhận vi phạm không thanh toán của Winner hoặc Người về nhì. Thành công đổi ViolationState = Done; lỗi thì cập nhật backoff để job quét retry.</summary>
    private async Task TryRecordNonPaymentViolationAsync(
        AuctionEntity auction, Guid orderId, Guid buyerId, bool isSecondChance, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        try
        {
            var deposit = await _dbContext.AuctionDeposits
                .AsNoTracking()
                .Where(d => d.AuctionId == auction.Id && d.BidderId == buyerId && !d.IsDeleted)
                .Select(d => (decimal?)d.Amount)
                .FirstOrDefaultAsync(cancellationToken);
            var depositForfeitedAmount = deposit ?? 0m;

            var violationType = isSecondChance ? "SecondChanceNonPayment" : "WinnerNonPayment";
            await _users.RecordAuctionViolationAsync(
                new RecordAuctionViolationCommand(
                    buyerId,
                    violationType,
                    auction.Id,
                    orderId,
                    occurredAt,
                    depositForfeitedAmount),
                cancellationToken);

            auction.ViolationState = BackgroundSyncState.Done;
            ClearPendingViolation(auction);
            if (auction.StreakResetState != BackgroundSyncState.Pending && auction.StockReleaseState != BackgroundSyncState.Pending)
            {
                auction.LastError = null;
                auction.NextRetryAt = null;
                auction.NeedsAttention = false;
            }
            auction.UpdatedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DomainException ex)
        {
            RecordAuctionSyncFailure(auction, ex.Message, now);
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
            }
        }
    }

    internal static void ClearPendingViolation(AuctionEntity auction)
    {
        auction.PendingViolationBuyerId = null;
        auction.PendingViolationOrderId = null;
        auction.PendingViolationOccurredAt = null;
        auction.PendingViolationIsSecondChance = false;
    }

    /// <summary>Gọi UserService (API 25) để reset ViolationStreak về 0 khi Buyer thanh toán đúng hạn. Thành công đổi StreakResetState = Done; lỗi thì cập nhật backoff để job quét retry.</summary>
    private async Task TryResetPaymentStreakAsync(
        AuctionEntity auction, Guid orderId, Guid buyerId, bool isSecondChance, DateTimeOffset paidAt, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        try
        {
            var source = isSecondChance ? "SECOND_CHANCE" : "WINNER";
            await _users.RecordAuctionPaymentAsync(
                new RecordAuctionPaymentCommand(
                    buyerId,
                    auction.Id,
                    orderId,
                    source,
                    paidAt),
                cancellationToken);

            auction.StreakResetState = BackgroundSyncState.Done;
            if (auction.ViolationState != BackgroundSyncState.Pending && auction.StockReleaseState != BackgroundSyncState.Pending)
            {
                auction.LastError = null;
                auction.NextRetryAt = null;
                auction.NeedsAttention = false;
            }
            auction.UpdatedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DomainException ex)
        {
            RecordAuctionSyncFailure(auction, ex.Message, now);
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
            }
        }
    }

    private static void RecordAuctionSyncFailure(AuctionEntity auction, string error, DateTimeOffset now)
    {
        auction.AttemptCount += 1;
        auction.LastError = error.Length > 500 ? error[..500] : error;
        if (auction.AttemptCount > 10)
        {
            auction.NeedsAttention = true;
            auction.NextRetryAt = null;
        }
        else
        {
            var backoffMinutes = auction.AttemptCount switch
            {
                <= 1 => 1,
                2 => 2,
                3 => 5,
                4 => 10,
                _ => 30
            };
            auction.NextRetryAt = now.AddMinutes(backoffMinutes);
        }
        auction.UpdatedAt = now;
    }

    /// <summary>Đổi chuỗi tên trạng thái lời mời thành AuctionWinnerOfferStatus. Chuỗi rỗng trả null (không lọc); chỉ nhận tên, không nhận số; sai thì ném 400 VAL_INVALID_STATUS.</summary>
    private static AuctionWinnerOfferStatus? ParseOfferStatus(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (!int.TryParse(trimmed, out _) &&
            Enum.TryParse<AuctionWinnerOfferStatus>(trimmed, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw Invalid("VAL_INVALID_STATUS", "Status is not a valid offer status.");
    }

    /// <summary>Giới hạn truy vấn vào các phiên đã chốt người thắng: chưa xoá, có trạng thái phiên, có AuctionResult và có settlement.</summary>
    private static IQueryable<AuctionEntity> WithSettlement(IQueryable<AuctionEntity> query) =>
        query.Where(a => !a.IsDeleted
            && a.AuctionState != null
            && a.AuctionResult != null
            && a.AuctionSettlement != null);

    /// <summary>Chiếu các phiên đã chốt (đã lọc bằng WithSettlement) sang dòng dữ liệu phẳng để dựng AuctionSettlementDto. Người thắng và bid thắng lấy từ AuctionResult.</summary>
    private static IQueryable<SettlementRow> SelectSettlementRows(IQueryable<AuctionEntity> query) =>
        query.Select(a => new SettlementRow
        {
            AuctionId = a.Id,
            ProductId = a.ProductId,
            SkuId = a.SkuId,
            SellerId = a.SellerId,
            Title = a.SnapshotProductTitle,
            WinnerId = a.AuctionResult!.WinnerId,
            WinningBidId = a.AuctionResult.WinningBidId,
            FinalPriceAmount = a.AuctionResult.FinalPriceAmount,
            SettlementStatus = a.AuctionSettlement!.Status,
            Status = a.AuctionState!.Status,
            PaymentDeadlineAt = a.AuctionSettlement.PaymentDeadlineAt,
            OrderId = a.AuctionSettlement.OrderId,
            PaidAt = a.AuctionSettlement.PaidAt,
            DeterminedAt = a.AuctionResult.DeterminedAt
        });

    /// <summary>Đổi một dòng dữ liệu phẳng thành AuctionSettlementDto. Tên sản phẩm trống thì trả chuỗi rỗng.</summary>
    private static AuctionSettlementDto ToSettlementDto(SettlementRow r) => new(
        r.AuctionId, r.ProductId, r.SkuId, r.SellerId, r.Title ?? string.Empty, r.WinnerId, r.WinningBidId,
        r.FinalPriceAmount, r.SettlementStatus, r.Status, r.PaymentDeadlineAt, r.OrderId, r.PaidAt, r.DeterminedAt);

    /// <summary>Đổi chuỗi tên trạng thái settlement thành AuctionSettlementStatus. Chuỗi rỗng trả null (không lọc); chỉ nhận tên, không nhận số; sai thì ném 400 VAL_INVALID_STATUS.</summary>
    private static AuctionSettlementStatus? ParseSettlementStatus(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (!int.TryParse(trimmed, out _) &&
            Enum.TryParse<AuctionSettlementStatus>(trimmed, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw Invalid("VAL_INVALID_STATUS", "Status is not a valid settlement status.");
    }

    /// <summary>Đổi chuỗi tên trạng thái thành AuctionStatus. Chuỗi rỗng trả null (không lọc); chỉ nhận tên, không nhận số; sai thì ném 400 VAL_INVALID_STATUS.</summary>
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

    /// <summary>Dòng dữ liệu phẳng của một phiên đã chốt, dùng làm kết quả chiếu từ DB trước khi đổi thành AuctionSettlementDto.</summary>
    private sealed class SettlementRow
    {
        public Guid AuctionId { get; init; }
        public Guid ProductId { get; init; }
        public Guid? SkuId { get; init; }
        public Guid SellerId { get; init; }
        public string? Title { get; init; }
        public Guid? WinnerId { get; init; }
        public Guid? WinningBidId { get; init; }
        public decimal? FinalPriceAmount { get; init; }
        public AuctionSettlementStatus SettlementStatus { get; init; }
        public AuctionStatus Status { get; init; }
        public DateTimeOffset? PaymentDeadlineAt { get; init; }
        public Guid? OrderId { get; init; }
        public DateTimeOffset? PaidAt { get; init; }
        public DateTimeOffset DeterminedAt { get; init; }
    }

    /// <summary>Cấu hình chống bắn tỉa dùng khi tạo phiên: số phút gia hạn mỗi lần và số lần gia hạn tối đa.</summary>
    private sealed record AuctionPolicy(int AntiSnipingExtensionMinutes, int MaxExtensionCount, decimal DefaultBidStepPercent);
}