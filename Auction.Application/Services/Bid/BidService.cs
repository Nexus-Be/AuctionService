namespace Auction.Application.Services.Bid;

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
using global::Auction.Domain.Entities;
using global::Auction.Domain.Enums;
using global::Auction.Domain.Exceptions;
using global::Auction.Domain.Rules;

public sealed class BidService : IBidService
{
    private const string AnonymousName = "Anonymous user";
    private static readonly TimeSpan IdempotencyLifetime = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAuctionDbContext _dbContext;
    private readonly IUserClient _users;
    private readonly TimeProvider _clock;
    private readonly IAuctionNotifier _notifier;
    private readonly INotificationClient _notifications;

    /// <summary>Khởi tạo service với DbContext, User client, đồng hồ hệ thống, bộ thông báo realtime và client gửi thông báo sang Notification service.</summary>
    public BidService(
        IAuctionDbContext dbContext, IUserClient users, TimeProvider clock, IAuctionNotifier notifier, INotificationClient notifications)
    {
        _dbContext = dbContext;
        _users = users;
        _clock = clock;
        _notifier = notifier;
        _notifications = notifications;
    }

    // ------------------------------------------------------------------ History

    /// <summary>
    /// Lấy lịch sử đặt giá công khai của một phiên (mới nhất trước, có phân trang). Tên người đặt giá được che bởi User service;
    /// User service lỗi thì hiển thị là người dùng ẩn danh.
    /// </summary>
    public async Task<PagedData<BidDto>> ListBidsByAuctionIdAsync(Guid auctionId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);

        var auctionExists = await _dbContext.Auctions
            .AsNoTracking()
            .AnyAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (!auctionExists)
            throw new EntityNotFoundException("AUCTION_NOT_FOUND", "Auction", auctionId);

        var query = _dbContext.BidHistories
            .AsNoTracking()
            .Where(h => h.AuctionId == auctionId && !h.IsDeleted);

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(h => h.SequenceNo)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(h => new
            {
                h.BidId,
                h.SequenceNo,
                h.BidderId,
                h.Amount,
                h.PlacedAt,
                h.IsOutbid,
                h.IsWinning,
                h.IsInvalidated
            })
            .ToListAsync(cancellationToken);

        // Names are masked by the User service. Without them the bidder is shown as anonymous:
        // the stored display name is never exposed because it is not masked.
        var names = await TryGetNamesAsync(rows.Select(r => r.BidderId).Distinct().ToList(), cancellationToken);

        var items = rows
            .Select(r => new BidDto(
                r.BidId, r.SequenceNo, r.BidderId,
                names.TryGetValue(r.BidderId, out var info) ? info.MaskedName : AnonymousName,
                r.Amount, r.PlacedAt, r.IsOutbid, r.IsWinning, r.IsInvalidated))
            .ToList();

        return new PagedData<BidDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    /// <summary>Lấy các lượt đặt giá của chính người dùng qua mọi phiên (mới nhất trước, có phân trang), kèm tên và trạng thái phiên.</summary>
    public async Task<PagedData<MyBidDto>> ListMyBidsAsync(Guid bidderId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);

        var query = _dbContext.BidHistories
            .AsNoTracking()
            .Where(h => h.BidderId == bidderId && !h.IsDeleted && !h.Auction!.IsDeleted);

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(h => h.PlacedAt)
            .ThenByDescending(h => h.SequenceNo)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(h => new
            {
                h.BidId,
                h.AuctionId,
                Title = h.Auction!.SnapshotProductTitle,
                Status = h.Auction.AuctionState == null ? (AuctionStatus?)null : h.Auction.AuctionState.Status,
                h.SequenceNo,
                h.Amount,
                h.PlacedAt,
                h.IsOutbid,
                h.IsWinning,
                h.IsInvalidated
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(r => new MyBidDto(
                r.BidId, r.AuctionId, r.Title, r.Status, r.SequenceNo,
                r.Amount, r.PlacedAt, r.IsOutbid, r.IsWinning, r.IsInvalidated))
            .ToList();

        return new PagedData<MyBidDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    // ------------------------------------------------------------------ Place a bid

    /// <summary>
    /// Đặt giá cho một phiên. Bắt buộc có Idempotency-Key và If-Match (RowVersion); gửi lại cùng key và cùng nội dung thì trả kết quả lần đầu, khác nội dung thì 422.
    /// Chặn seller đặt giá phiên của chính mình; kiểm tra phiên đang chạy, giá tối thiểu và quyền đặt giá với User service.
    /// Khi hợp lệ: lưu bid, đánh dấu bid dẫn đầu cũ là bị vượt, cập nhật giá hiện tại, gia hạn chống bắn tỉa nếu đặt sát giờ kết thúc,
    /// lưu idempotency và audit, rồi báo realtime BidPlaced sau khi lưu thành công. Response và sự kiện kèm giá cũ (PreviousPriceAmount)
    /// và RowVersion mới để FE dùng làm If-Match cho lần đặt giá tiếp theo.
    /// </summary>
    public async Task<PlaceBidResponse> PlaceBidAsync(
        ActorContext actor, Guid auctionId, string? idempotencyKey, string? ifMatch,
        PlaceBidRequest request, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        // 1. Input checks (no database, no other service).
        if (auctionId == Guid.Empty)
            throw new DomainException("VAL_INVALID_IDENTIFIER", "Auction ID is required.", 400);
        BidRules.ValidateIdempotencyKey(idempotencyKey);
        var expectedRowVersion = BidRules.ParseRowVersion(ifMatch);
        BidRules.ValidateAmount(request.Amount);

        var amount = request.Amount!.Value;
        var key = idempotencyKey!.Trim();
        var requestHash = HashRequest(auctionId, actor.UserId, amount);

        // 2. Same key again: return the first result, or refuse when the request is different.
        var existing = await _dbContext.IdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == key && k.Scope == IdempotencyScope.Bid && k.ExpiresAt > now, cancellationToken);
        if (existing is not null)
        {
            if (existing.RequestHash != requestHash)
                throw new BusinessRuleValidationException(
                    "IDEMPOTENCY_KEY_REUSED", "The Idempotency-Key was already used with a different request.", 422);

            var saved = DeserializeResponse(existing.ResponseBody);
            if (saved is not null)
                return saved;
        }

        // 3. The auction must exist and be open for bids.
        var auction = await _dbContext.Auctions
            .Include(a => a.AuctionState)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (auction?.AuctionState is null)
            throw new EntityNotFoundException("AUCTION_NOT_FOUND", "Auction", auctionId);

        var state = auction.AuctionState;
        // A seller can bid on auctions of other sellers, never on their own.
        if (auction.SellerId == actor.UserId)
            throw new DomainException(
                "SELLER_CANNOT_BID_OWN_AUCTION", "A seller cannot bid on their own auction.", 403);

        BidRules.EnsureBiddable(state.Status, state.ScheduledEndAt, now);

        // 4. The bidder saw an older version of the auction: someone else bid first.
        if (state.RowVersion != expectedRowVersion)
            throw new DomainException(
                "CONCURRENT_UPDATE", "The auction changed since it was loaded. Reload it and try again.", 409);

        // 5. User must confirm the bidder (reputation, account status). No answer means no bid.
        var eligibility = await _users.VerifyBidEligibilityAsync(actor.UserId, cancellationToken);
        if (!eligibility.Eligible)
            throw UserEligibilityErrors.FromFailureReason(
                eligibility.FailureReason, "place a bid", "BIDDER_NOT_ELIGIBLE", "The user is not eligible to place a bid.");

        // 6. The amount must reach the minimum.
        var minNextBid = BidRules.MinNextBid(auction.StartPriceAmount, auction.BidStepAmount, state.CurrentPriceAmount);
        BidRules.EnsureMeetsMinimum(amount, minNextBid);

        // 7. Write everything in one SaveChanges.
        var previousWinner = await _dbContext.BidHistories
            .Where(h => h.AuctionId == auctionId && h.IsWinning)
            .ToListAsync(cancellationToken);

        // Người đang dẫn đầu trước lượt này (nếu có) sẽ nhận thông báo bị vượt giá sau khi lượt đặt giá được lưu.
        var outbidBid = previousWinner.OrderByDescending(h => h.Amount).FirstOrDefault();
        foreach (var history in previousWinner)
        {
            history.IsWinning = false;
            history.IsOutbid = true;
            history.UpdatedAt = now;
        }

        var bidSeq = state.BidCount + 1;
        var bid = new Bid
        {
            Id = Guid.NewGuid(),
            AuctionId = auctionId,
            BidSeq = bidSeq,
            BidderId = actor.UserId,
            Amount = amount,
            PlacedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        _dbContext.Bids.Add(bid);
        _dbContext.BidHistories.Add(new BidHistory
        {
            Id = Guid.NewGuid(),
            AuctionId = auctionId,
            BidId = bid.Id,
            SequenceNo = bidSeq,
            BidderId = actor.UserId,
            Amount = amount,
            PlacedAt = now,
            IsWinning = true,
            CreatedAt = now,
            UpdatedAt = now
        });

        var oldValues = new
        {
            currentPriceAmount = state.CurrentPriceAmount,
            bidCount = state.BidCount,
            scheduledEndAt = state.ScheduledEndAt,
            extensionCount = state.ExtensionCount
        };

        var previousPriceAmount = state.CurrentPriceAmount;
        state.CurrentPriceAmount = amount;
        state.CurrentWinnerId = actor.UserId;
        state.BidCount = bidSeq;
        state.RowVersion += 1;
        state.UpdatedAt = now;

        // Anti-sniping: a bid close to the end moves the end time, a limited number of times.
        var extensionWindow = TimeSpan.FromMinutes(auction.AntiSnipingExtensionMinutes);
        if (auction.AntiSnipingExtensionMinutes > 0 &&
            state.ExtensionCount < auction.MaxExtensionCount &&
            state.ScheduledEndAt - now <= extensionWindow)
        {
            var previousEnd = state.ScheduledEndAt;
            state.ScheduledEndAt = previousEnd.Add(extensionWindow);
            state.ExtensionCount += 1;

            _dbContext.AuctionExtensions.Add(new AuctionExtension
            {
                Id = Guid.NewGuid(),
                AuctionId = auctionId,
                TriggerBidId = bid.Id,
                SequenceNo = state.ExtensionCount,
                PreviousEndAt = previousEnd,
                NewEndAt = state.ScheduledEndAt,
                ExtendedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var response = new PlaceBidResponse(
            bid.Id, auctionId, bidSeq, amount, now, amount, state.BidCount, state.ScheduledEndAt, state.ExtensionCount,
            previousPriceAmount, state.RowVersion);

        _dbContext.IdempotencyKeys.Add(new IdempotencyKey
        {
            Id = Guid.NewGuid(),
            Key = key,
            Scope = IdempotencyScope.Bid,
            RequestHash = requestHash,
            ResponseStatus = 201,
            ResponseBody = JsonSerializer.Serialize(response, JsonOptions),
            ExpiresAt = now.Add(IdempotencyLifetime),
            CreatedAt = now,
            UpdatedAt = now
        });

        _dbContext.AddAuditLog(
            AuditAction.BidPlace, "Bid", bid.Id.ToString(),
            oldValues: oldValues,
            newValues: new
            {
                auctionId,
                bidId = bid.Id,
                bidSeq,
                amount,
                currentPriceAmount = state.CurrentPriceAmount,
                bidCount = state.BidCount,
                scheduledEndAt = state.ScheduledEndAt,
                extensionCount = state.ExtensionCount
            },
            userId: actor.UserId.ToString(),
            ipAddress: actor.ClientIp,
            traceId: actor.TraceId);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another bid, or the same Idempotency-Key, was saved first.
            throw new DomainException(
                "CONCURRENT_UPDATE", "The auction changed while the bid was being saved. Reload it and try again.", 409);
        }

        // Lượt đặt giá đã được lưu: báo cho người đang xem phiên. Không chạy khi replay idempotent vì nhánh đó đã return trước đó.
        await _notifier.BidPlacedAsync(
            auctionId,
            new BidPlacedMessage(
                auctionId, bid.Id, bidSeq, amount, amount,
                state.BidCount, now, state.ScheduledEndAt, state.ExtensionCount,
                previousPriceAmount, state.RowVersion),
            CancellationToken.None);

        // Báo cho người vừa bị vượt giá qua Notification service (gửi nền, không chờ, lỗi chỉ ghi log).
        // Lượt đầu tiên không có ai bị vượt, và người dẫn đầu tự nâng giá của mình thì không cần báo.
        if (outbidBid is not null && outbidBid.BidderId != actor.UserId)
        {
            _notifications.NotifyBidOutbid(new BidOutbidNotification(
                auctionId, auction.SnapshotProductTitle, bid.Id, outbidBid.BidId, outbidBid.BidderId,
                actor.UserId, auction.SellerId, outbidBid.Amount, amount, now, state.ScheduledEndAt, actor.TraceId));
        }

        return response;
    }

    // ------------------------------------------------------------------ Helpers

    /// <summary>Lấy tên đã che của nhiều người dùng từ User service. User service lỗi thì trả tập rỗng vì tên chỉ là dữ liệu hiển thị.</summary>
    private async Task<IReadOnlyDictionary<Guid, UserPublicInfo>> TryGetNamesAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, UserPublicInfo>();

        try
        {
            return await _users.GetPublicInfosAsync(userIds, cancellationToken);
        }
        catch (DomainException ex) when (ex.StatusCode >= 500)
        {
            // Names are display data only: the history is still returned (the failure is logged by the client).
            return new Dictionary<Guid, UserPublicInfo>();
        }
    }

    /// <summary>Băm nội dung yêu cầu đặt giá (phiên, người dùng, số tiền) để phát hiện cùng Idempotency-Key nhưng khác nội dung.</summary>
    private static string HashRequest(Guid auctionId, Guid userId, decimal amount)
    {
        var text = $"{auctionId:N}|{userId:N}|{amount.ToString("0.00", CultureInfo.InvariantCulture)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Đọc lại kết quả đặt giá đã lưu cho một Idempotency-Key. Nội dung rỗng hoặc hỏng thì trả null.</summary>
    private static PlaceBidResponse? DeserializeResponse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonSerializer.Deserialize<PlaceBidResponse>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}