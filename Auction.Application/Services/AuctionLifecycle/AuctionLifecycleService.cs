namespace Auction.Application.Services.AuctionLifecycle;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using global::Auction.Application.Common.Extensions;
using global::Auction.Application.Interfaces;
using global::Auction.Application.Services.Auction;
using global::Auction.Application.Services.Deposit;
using global::Auction.Domain.Entities;
using global::Auction.Domain.Enums;
using global::Auction.Domain.Exceptions;
using global::Auction.Domain.Rules;

public sealed class AuctionLifecycleService : IAuctionLifecycleService
{
    /// <summary>Thời hạn thanh toán của người thắng, tính từ lúc phiên kết thúc (theo business rule: 24 giờ).</summary>
    private static readonly TimeSpan PaymentWindow = TimeSpan.FromHours(24);

    private readonly IAuctionDbContext _dbContext;
    private readonly TimeProvider _clock;
    private readonly IAuctionNotifier _notifier;
    private readonly INotificationClient _notifications;
    private readonly ICatalogSync _catalogSync;
    private readonly ICatalogClient _catalog;
    private readonly ICommerceSync _commerceSync;
    private readonly IUserClient _users;
    private readonly IDepositService _deposits;

    /// <summary>Khởi tạo service với DbContext, đồng hồ hệ thống, bộ thông báo realtime và service cọc.</summary>
    public AuctionLifecycleService(
        IAuctionDbContext dbContext, TimeProvider clock, IAuctionNotifier notifier, INotificationClient notifications,
        ICatalogSync catalogSync, ICatalogClient catalog, ICommerceSync commerceSync, IUserClient users, IDepositService deposits)
    {
        _dbContext = dbContext;
        _clock = clock;
        _notifier = notifier;
        _notifications = notifications;
        _catalogSync = catalogSync;
        _catalog = catalog;
        _commerceSync = commerceSync;
        _users = users;
        _deposits = deposits;
    }

    /// <summary>Tìm các phiên Scheduled đã tới giờ bắt đầu. Chỉ đọc mã phiên, việc chuyển trạng thái làm riêng từng phiên.</summary>
    public async Task<IReadOnlyList<Guid>> FindAuctionsToStartAsync(int limit, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        return await _dbContext.Auctions
            .AsNoTracking()
            .Where(a => !a.IsDeleted
                && a.AuctionState != null
                && a.AuctionState.Status == AuctionStatus.Scheduled
                && a.ScheduledStartAt <= now)
            .OrderBy(a => a.ScheduledStartAt)
            .Select(a => a.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Tìm các phiên Running đã hết giờ kết thúc. Giờ kết thúc lấy từ trạng thái phiên nên đã gồm các lần gia hạn chống bắn tỉa.</summary>
    public async Task<IReadOnlyList<Guid>> FindAuctionsToEndAsync(int limit, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        return await _dbContext.Auctions
            .AsNoTracking()
            .Where(a => !a.IsDeleted
                && a.AuctionState != null
                && a.AuctionState.Status == AuctionStatus.Running
                && a.AuctionState.ScheduledEndAt <= now)
            .OrderBy(a => a.AuctionState!.ScheduledEndAt)
            .Select(a => a.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Chuyển một phiên từ Scheduled sang Running khi đã tới giờ bắt đầu: đặt StartedAt, ghi lịch sử trạng thái và audit,
    /// rồi báo realtime AuctionStatusChanged. Dùng RowVersion để chống xử lý trùng: nếu seller huỷ hoặc nơi khác đổi phiên cùng lúc
    /// thì lưu thất bại và hàm trả false, không ném lỗi.
    /// </summary>
    public async Task<bool> StartAuctionAsync(Guid auctionId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        var auction = await _dbContext.Auctions
            .Include(a => a.AuctionState)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);

        var state = auction?.AuctionState;
        if (auction is null || state is null
            || state.Status != AuctionStatus.Scheduled
            || auction.ScheduledStartAt > now)
        {
            return false;
        }

        state.Status = AuctionStatus.Running;
        state.StartedAt = now;
        state.UpdatedAt = now;
        state.RowVersion += 1;

        _dbContext.AuctionStatusHistories.Add(new AuctionStatusHistory
        {
            Id = Guid.NewGuid(),
            AuctionId = auctionId,
            FromStatus = AuctionStatus.Scheduled,
            ToStatus = AuctionStatus.Running,
            Reason = "Auction started automatically as scheduled.",
            ChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        _dbContext.AddAuditLog(
            AuditAction.AuctionStart, "Auction", auctionId.ToString(),
            oldValues: new { status = AuctionStatus.Scheduled.ToString() },
            newValues: new { status = AuctionStatus.Running.ToString(), startedAt = now });

        if (!await TrySaveAsync(cancellationToken))
            return false;

        await _notifier.AuctionStatusChangedAsync(
            auctionId, new AuctionStatusChangedMessage(auctionId, AuctionStatus.Running.ToString(), now), CancellationToken.None);

        return true;
    }

    /// <summary>
    /// Kết thúc một phiên Running khi hết giờ. Người thắng là bid hợp lệ cao nhất (bằng giá thì bid đến trước thắng).
    /// Có người thắng: phiên sang Ended, tạo AuctionResult và settlement (PendingPayment, hạn thanh toán 24 giờ),
    /// sản phẩm vẫn được giữ. Không có bid hợp lệ: phiên sang Failed và nhả sản phẩm để seller tạo lại phiên.
    /// Ghi lịch sử trạng thái, audit và báo realtime. RowVersion chống hai worker kết thúc cùng một phiên và chống
    /// kết thúc khi vừa có bid mới hoặc gia hạn: lưu thất bại thì trả false và phiên được xét lại ở lượt sau.
    /// </summary>
    public async Task<bool> EndAuctionAsync(Guid auctionId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        var auction = await _dbContext.Auctions
            .Include(a => a.AuctionState)
            .Include(a => a.AuctionResult)
            .Include(a => a.AuctionSettlement)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);

        var state = auction?.AuctionState;
        if (auction is null || state is null
            || state.Status != AuctionStatus.Running
            || state.ScheduledEndAt > now)
        {
            return false;
        }

        var validBids = await _dbContext.BidHistories
            .AsNoTracking()
            .Where(h => h.AuctionId == auctionId && !h.IsInvalidated && !h.IsDeleted)
            .OrderByDescending(h => h.Amount)
            .ThenBy(h => h.SequenceNo)
            .Select(h => new { h.BidId, h.BidderId, h.Amount })
            .ToListAsync(cancellationToken);

        var topBidders = validBids
            .GroupBy(b => b.BidderId)
            .Select(g => g.First())
            .Take(2)
            .ToList();

        var winningBid = topBidders.FirstOrDefault();
        var runnerUpBidderId = topBidders.Count > 1 ? topBidders[1].BidderId : (Guid?)null;

        state.EndedAt = now;
        state.UpdatedAt = now;
        state.RowVersion += 1;

        AuctionStatus toStatus;
        string reason;
        object newValues;
        AuctionWinnerNotification? winnerNotice = null;
        bool isBelowReserve = false;

        if (winningBid is null)
        {
            toStatus = AuctionStatus.Failed;
            reason = "Auction ended without a valid bid.";
            state.Status = toStatus;
            auction.StockReleaseState = BackgroundSyncState.Pending;
            auction.NextRetryAt = now;
            auction.NeedsAttention = false;
            newValues = new { status = toStatus.ToString(), endedAt = now };

            // Nhả sản phẩm để seller tạo lại phiên. Index unique theo ProductId bỏ qua xoá mềm nên phải xoá thật dòng này.
            var openAuction = await _dbContext.ProductOpenAuctions
                .FirstOrDefaultAsync(p => p.AuctionId == auctionId, cancellationToken);
            if (openAuction is not null)
                _dbContext.ProductOpenAuctions.Remove(openAuction);
        }
        else
        {
            isBelowReserve = auction.ReservePriceAmount is { } reserve && winningBid.Amount < reserve;
            if (isBelowReserve)
                runnerUpBidderId = null; // Below reserve: release Hạng 2+ immediately, hold only Hạng 1 during 12h seller approval.

            toStatus = AuctionStatus.Ended;
            reason = isBelowReserve ? "Auction ended below reserve price; awaiting seller approval." : "Auction time expired.";
            state.Status = toStatus;
            state.CurrentWinnerId = winningBid.BidderId;
            state.CurrentPriceAmount = winningBid.Amount;

            var paymentDeadline = isBelowReserve ? (DateTimeOffset?)null : now.Add(PaymentWindow);
            var sellerApprovalDeadline = isBelowReserve
                ? now.AddHours(AuctionRules.SellerReserveApprovalWindowHours)
                : (DateTimeOffset?)null;

            newValues = new
            {
                status = toStatus.ToString(),
                endedAt = now,
                winnerId = winningBid.BidderId,
                winningBidId = winningBid.BidId,
                finalPriceAmount = winningBid.Amount,
                isBelowReserve,
                paymentDeadlineAt = paymentDeadline,
                sellerApprovalDeadlineAt = sellerApprovalDeadline
            };

            // Thêm vào DbSet tường minh: entity mới đã có sẵn Id (Guid) nên nếu chỉ gán qua navigation,
            // EF coi là dòng đã tồn tại (Modified) và sinh UPDATE (0 dòng) thay vì INSERT.
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
            result.WinnerId = winningBid.BidderId;
            result.WinningBidId = winningBid.BidId;
            result.FinalPriceAmount = winningBid.Amount;
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
            settlement.WinnerId = winningBid.BidderId;
            settlement.WinningBidId = winningBid.BidId;
            settlement.IsBelowReserve = isBelowReserve;
            settlement.Status = isBelowReserve
                ? AuctionSettlementStatus.AwaitingSellerApproval
                : AuctionSettlementStatus.PendingPayment;
            settlement.PaymentDeadlineAt = paymentDeadline;
            settlement.SellerApprovalDeadlineAt = sellerApprovalDeadline;
            settlement.UpdatedAt = now;

            if (!isBelowReserve)
            {
                // Gửi thông báo cho người thắng sau khi kết quả đã được lưu (xem bên dưới).
                winnerNotice = new AuctionWinnerNotification(
                    auctionId, auction.SnapshotProductTitle, result.Id, winningBid.BidId, winningBid.BidderId,
                    auction.SellerId, winningBid.Amount, now, paymentDeadline!.Value);
            }
        }

        _dbContext.AuctionStatusHistories.Add(new AuctionStatusHistory
        {
            Id = Guid.NewGuid(),
            AuctionId = auctionId,
            FromStatus = AuctionStatus.Running,
            ToStatus = toStatus,
            Reason = reason,
            ChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        _dbContext.AddAuditLog(
            AuditAction.AuctionEnd, "Auction", auctionId.ToString(),
            oldValues: new { status = AuctionStatus.Running.ToString() },
            newValues: newValues);

        // Quy tắc giữ cọc Top 2: Giữ nguyên cọc của Winner (hạng 1) và Người về nhì tiềm năng (hạng 2);
        // chỉ mở khoá cọc từ hạng 3 trở xuống (hoặc mở khoá toàn bộ nếu phiên không có bid hợp lệ).
        var depositsToRelease = await _dbContext.AuctionDeposits
            .Where(d => d.AuctionId == auctionId && !d.IsDeleted &&
                        (d.Status == DepositStatus.Authorized || d.Status == DepositStatus.Pending) &&
                        (winningBid == null ||
                         (d.BidderId != winningBid.BidderId && (runnerUpBidderId == null || d.BidderId != runnerUpBidderId.Value))))
            .ToListAsync(cancellationToken);

        var releaseReason = winningBid is null ? "AUCTION_FAILED" : "AUCTION_ENDED_NOT_WINNER";
        foreach (var deposit in depositsToRelease)
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
                newValues: new { depositId = deposit.Id, status = deposit.Status, releaseReason, releasePending = deposit.ReleasePending });
        }

        // Queue outbox notification before TrySaveAsync so it commits atomically with the auction result.
        if (winnerNotice is not null)
        {
            _notifications.NotifyAuctionWinner(winnerNotice);
            _notifications.NotifyAuctionSellerResult(new AuctionSellerResultNotification(
                auctionId, winnerNotice.ResultId, auction.SellerId, "WinnerSelected", auction.SnapshotProductTitle,
                now, winnerNotice.WinningBidId, winnerNotice.WinnerUserId, null, winnerNotice.WinningAmount, winnerNotice.PaymentDeadline));
        }
        else if (isBelowReserve && winningBid is not null && auction.ReservePriceAmount is { } reservePrice)
        {
            _notifications.NotifyReserveApprovalNeeded(new ReserveApprovalNeededNotification(
                auctionId, auction.SellerId, auction.SnapshotProductTitle, winningBid.Amount, reservePrice, now.AddHours(AuctionRules.SellerReserveApprovalWindowHours)));
        }
        else if (winningBid is null)
        {
            _notifications.NotifyAuctionFailed(new AuctionFailedNotification(
                auctionId, auction.SellerId, auction.SnapshotProductTitle,
                "Auction ended without a valid bid.", now, null));
        }

        if (!await TrySaveAsync(cancellationToken))
            return false;

        if (depositsToRelease.Count > 0)
            await _deposits.ReleasePendingDepositsForAuctionAsync(auctionId, cancellationToken);

        await _notifier.AuctionStatusChangedAsync(
            auctionId, new AuctionStatusChangedMessage(auctionId, toStatus.ToString(), now, isBelowReserve ? "AwaitingSellerApproval" : null), CancellationToken.None);

        // Đã lưu: đẩy settlement sang Commerce nếu đạt giá sàn, hoặc mở khoá sản phẩm ở Catalog nếu không có bid hợp lệ.
        if (winnerNotice is not null)
        {
            _commerceSync.PushSettlement(auctionId);
        }
        else if (winningBid is null)
        {
            _catalogSync.UnlockProduct(auction.ProductId, auctionId);
        }

        return true;
    }

    /// <summary>Tìm các lời mời mua thay còn Pending đã quá hạn phản hồi. Chỉ đọc mã lời mời, việc đóng lời mời làm riêng từng cái.</summary>
    public async Task<IReadOnlyList<Guid>> FindWinnerOffersToExpireAsync(int limit, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        return await _dbContext.AuctionWinnerOffers
            .AsNoTracking()
            .Where(o => !o.IsDeleted
                && o.Status == AuctionWinnerOfferStatus.Pending
                && o.ExpiresAt <= now)
            .OrderBy(o => o.ExpiresAt)
            .Select(o => o.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Đóng một lời mời quá hạn: lời mời sang Expired. Phiên đang Ended thì sang Failed, nhả sản phẩm (xoá dòng phiên mở) để seller tạo lại phiên,
    /// ghi lịch sử trạng thái, audit và báo realtime AuctionStatusChanged. RowVersion của phiên chống việc đóng lời mời cùng lúc bidder chấp nhận:
    /// lưu thất bại thì trả false và lời mời được xét lại ở lượt sau.
    /// </summary>
    public async Task<bool> ExpireWinnerOfferAsync(Guid offerId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        var offer = await _dbContext.AuctionWinnerOffers
            .FirstOrDefaultAsync(o => o.Id == offerId && !o.IsDeleted, cancellationToken);
        if (offer is null
            || offer.Status != AuctionWinnerOfferStatus.Pending
            || offer.ExpiresAt > now)
        {
            return false;
        }

        var auction = await _dbContext.Auctions
            .Include(a => a.AuctionState)
            .FirstOrDefaultAsync(a => a.Id == offer.AuctionId && !a.IsDeleted, cancellationToken);
        var state = auction?.AuctionState;

        offer.Status = AuctionWinnerOfferStatus.Expired;
        offer.UpdatedAt = now;

        var failAuction = state is not null && state.Status == AuctionStatus.Ended;
        var depositsToReleaseCount = 0;
        if (failAuction)
        {
            state!.Status = AuctionStatus.Failed;
            state.UpdatedAt = now;
            state.RowVersion += 1;
            auction!.StockReleaseState = BackgroundSyncState.Pending;
            auction.NextRetryAt = now;
            auction.NeedsAttention = false;

            // Lời mời quá hạn mà chưa nhận: không tính là bỏ kèo, mở khoá toàn bộ cọc còn đang giữ của phiên (trừ các cọc đang chờ Capture/Refund/Release).
            var remainingDeposits = await _dbContext.AuctionDeposits
                .Where(d => d.AuctionId == offer.AuctionId && !d.IsDeleted &&
                            d.PendingAction == DepositPendingAction.None &&
                            (d.Status == DepositStatus.Authorized || d.Status == DepositStatus.Pending))
                .ToListAsync(cancellationToken);
            foreach (var deposit in remainingDeposits)
            {
                var oldDepositStatus = deposit.Status;
                deposit.Status = DepositStatus.Released;
                deposit.ReleaseReason = "SECOND_CHANCE_EXPIRED";
                deposit.ReleasePending = true;
                deposit.PendingAction = DepositPendingAction.Release;
                deposit.NextRetryAt = now;
                deposit.NeedsAttention = false;
                deposit.UpdatedAt = now;
                deposit.RowVersion += 1;

                _dbContext.AddAuditLog(
                    AuditAction.DepositRelease, "AuctionDeposit", deposit.Id.ToString(),
                    oldValues: new { status = oldDepositStatus },
                    newValues: new { depositId = deposit.Id, status = deposit.Status, releaseReason = deposit.ReleaseReason, releasePending = deposit.ReleasePending });
            }
            depositsToReleaseCount = remainingDeposits.Count;

            // Nhả sản phẩm để seller tạo lại phiên. Index unique theo ProductId bỏ qua xoá mềm nên phải xoá thật dòng này.
            var openAuction = await _dbContext.ProductOpenAuctions
                .FirstOrDefaultAsync(p => p.AuctionId == offer.AuctionId, cancellationToken);
            if (openAuction is not null)
                _dbContext.ProductOpenAuctions.Remove(openAuction);

            _dbContext.AuctionStatusHistories.Add(new AuctionStatusHistory
            {
                Id = Guid.NewGuid(),
                AuctionId = offer.AuctionId,
                FromStatus = AuctionStatus.Ended,
                ToStatus = AuctionStatus.Failed,
                Reason = "The winner offer expired without an answer.",
                ChangedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        _dbContext.AddAuditLog(
            AuditAction.WinnerOfferExpire, "Auction", offer.AuctionId.ToString(),
            oldValues: new { offerId, offerStatus = AuctionWinnerOfferStatus.Pending.ToString() },
            newValues: new
            {
                offerId,
                offerStatus = AuctionWinnerOfferStatus.Expired.ToString(),
                status = state?.Status.ToString()
            });

        if (failAuction)
        {
            _notifications.NotifyAuctionFailed(new AuctionFailedNotification(
                offer.AuctionId, auction!.SellerId, auction.SnapshotProductTitle,
                "The replacement winner offer expired without an answer.", now, null));
        }

        if (!await TrySaveAsync(cancellationToken))
            return false;

        if (failAuction)
        {
            if (depositsToReleaseCount > 0)
                await _deposits.ReleasePendingDepositsForAuctionAsync(offer.AuctionId, cancellationToken);

            // Đã lưu: mở khoá sản phẩm ở Catalog (gửi nền, có thử lại), rồi báo realtime phiên đã Failed.
            _catalogSync.UnlockProduct(auction!.ProductId, offer.AuctionId);

            await _notifier.AuctionStatusChangedAsync(
                offer.AuctionId, new AuctionStatusChangedMessage(offer.AuctionId, AuctionStatus.Failed.ToString(), now), CancellationToken.None);
        }

        return true;
    }

    /// <summary>
    /// Job quét định kỳ (Overview 4.1):
    /// 1) Gọi _deposits.ProcessDueDepositRetriesAsync để thử lại Release/Capture/Refund cọc và đồng bộ cọc Pending bị timeout.
    /// 2) Quét các phiên có ViolationState, StreakResetState hoặc StockReleaseState = Pending (NextRetryAt &lt;= now, NeedsAttention = false).
    /// 3) Quét các lời mời người về nhì có SettlementState = Pending (NextRetryAt &lt;= now, NeedsAttention = false).
    /// </summary>
    public async Task ProcessDueBackgroundTasksAsync(int limit, CancellationToken cancellationToken = default)
    {
        await _deposits.ProcessDueDepositRetriesAsync(limit, cancellationToken);
        await ExpireDueSellerApprovalsAsync(limit, cancellationToken);

        var now = _clock.GetUtcNow();

        var dueAuctions = await _dbContext.Auctions
            .Include(a => a.AuctionResult)
            .Include(a => a.AuctionSettlement)
            .Where(a => !a.IsDeleted
                        && !a.NeedsAttention
                        && (a.ViolationState == BackgroundSyncState.Pending
                            || a.StreakResetState == BackgroundSyncState.Pending
                            || a.StockReleaseState == BackgroundSyncState.Pending)
                        && (a.NextRetryAt == null || a.NextRetryAt <= now))
            .OrderBy(a => a.NextRetryAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        foreach (var auction in dueAuctions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessSingleAuctionBackgroundSyncAsync(auction, now, cancellationToken);
        }

        var dueOffers = await _dbContext.AuctionWinnerOffers
            .Where(o => !o.IsDeleted
                        && !o.NeedsAttention
                        && o.SettlementState == BackgroundSyncState.Pending
                        && (o.NextRetryAt == null || o.NextRetryAt <= now))
            .OrderBy(o => o.NextRetryAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        foreach (var offer in dueOffers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessSingleOfferSettlementSyncAsync(offer, now, cancellationToken);
        }
    }

    private async Task ProcessSingleAuctionBackgroundSyncAsync(
        Auction auction, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string? firstError = null;
        var settlement = auction.AuctionSettlement;
        var result = auction.AuctionResult;
        var buyerId = settlement?.WinnerId ?? result?.WinnerId;
        var orderId = settlement?.OrderId ?? Guid.Empty;
        var isSecondChance = buyerId.HasValue && await _dbContext.AuctionWinnerOffers
            .AsNoTracking()
            .AnyAsync(o => o.AuctionId == auction.Id && o.BidderId == buyerId.Value && o.Status == AuctionWinnerOfferStatus.Accepted, cancellationToken);

        if (auction.ViolationState == BackgroundSyncState.Pending)
        {
            // Prefer the snapshot taken when the violation was decided; older rows without it fall back to the settlement.
            var violationBuyerId = auction.PendingViolationBuyerId ?? buyerId;
            var violationOrderId = auction.PendingViolationOrderId ?? orderId;
            var violationIsSecondChance = auction.PendingViolationBuyerId.HasValue ? auction.PendingViolationIsSecondChance : isSecondChance;
            var violationOccurredAt = auction.PendingViolationOccurredAt ?? settlement?.UpdatedAt ?? now;

            if (violationBuyerId is null || violationOrderId == Guid.Empty)
            {
                auction.ViolationState = BackgroundSyncState.Done;
                auction.PendingViolationBuyerId = null; auction.PendingViolationOrderId = null; auction.PendingViolationOccurredAt = null; auction.PendingViolationIsSecondChance = false;
            }
            else
            {
                try
                {
                    var depAmount = await _dbContext.AuctionDeposits
                        .AsNoTracking()
                        .Where(d => d.AuctionId == auction.Id && d.BidderId == violationBuyerId.Value && !d.IsDeleted)
                        .Select(d => (decimal?)d.Amount)
                        .FirstOrDefaultAsync(cancellationToken);
                    var forfeited = depAmount ?? 0m;

                    var violationType = violationIsSecondChance ? "SecondChanceNonPayment" : "WinnerNonPayment";
                    await _users.RecordAuctionViolationAsync(
                        new RecordAuctionViolationCommand(
                            violationBuyerId.Value,
                            violationType,
                            auction.Id,
                            violationOrderId,
                            violationOccurredAt,
                            forfeited),
                        cancellationToken);
                    auction.ViolationState = BackgroundSyncState.Done;
                    auction.PendingViolationBuyerId = null; auction.PendingViolationOrderId = null; auction.PendingViolationOccurredAt = null; auction.PendingViolationIsSecondChance = false;
                }
                catch (DomainException ex) when (ex.StatusCode == 404)
                {
                    auction.ViolationState = BackgroundSyncState.Done;
                    auction.PendingViolationBuyerId = null; auction.PendingViolationOrderId = null; auction.PendingViolationOccurredAt = null; auction.PendingViolationIsSecondChance = false;
                }
                catch (DomainException ex)
                {
                    firstError ??= ex.Message;
                }
            }
        }

        if (auction.StreakResetState == BackgroundSyncState.Pending)
        {
            if (buyerId is null || orderId == Guid.Empty)
            {
                auction.StreakResetState = BackgroundSyncState.Done;
            }
            else
            {
                try
                {
                    var source = isSecondChance ? "SECOND_CHANCE" : "WINNER";
                    await _users.RecordAuctionPaymentAsync(
                        new RecordAuctionPaymentCommand(
                            buyerId.Value,
                            auction.Id,
                            orderId,
                            source,
                            settlement?.PaidAt ?? now),
                        cancellationToken);
                    auction.StreakResetState = BackgroundSyncState.Done;
                }
                catch (DomainException ex) when (ex.StatusCode == 404)
                {
                    auction.StreakResetState = BackgroundSyncState.Done;
                }
                catch (DomainException ex)
                {
                    firstError ??= ex.Message;
                }
            }
        }

        if (auction.StockReleaseState == BackgroundSyncState.Pending)
        {
            try
            {
                var heldByAnotherAuction = await _dbContext.ProductOpenAuctions
                    .AsNoTracking()
                    .AnyAsync(p => p.ProductId == auction.ProductId, cancellationToken);
                if (!heldByAnotherAuction)
                    await _catalog.UnlockProductFromAuctionAsync(auction.ProductId, cancellationToken);

                auction.StockReleaseState = BackgroundSyncState.Done;
            }
            catch (DomainException ex)
            {
                firstError ??= ex.Message;
            }
        }

        var stillPending = auction.ViolationState == BackgroundSyncState.Pending
                           || auction.StreakResetState == BackgroundSyncState.Pending
                           || auction.StockReleaseState == BackgroundSyncState.Pending;
        if (!stillPending)
        {
            auction.AttemptCount = 0;
            auction.LastError = null;
            auction.NextRetryAt = null;
            auction.NeedsAttention = false;
        }
        else if (firstError is not null)
        {
            auction.AttemptCount += 1;
            auction.LastError = firstError.Length > 500 ? firstError[..500] : firstError;
            if (auction.AttemptCount > 10)
            {
                auction.NeedsAttention = true;
                auction.NextRetryAt = null;
            }
            else
            {
                auction.NextRetryAt = now.AddMinutes(GetBackoffMinutes(auction.AttemptCount));
            }
        }

        auction.UpdatedAt = now;
        await TrySaveAsync(cancellationToken);
    }

    private async Task ProcessSingleOfferSettlementSyncAsync(
        AuctionWinnerOffer offer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settlement = await _dbContext.AuctionSettlements
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.AuctionId == offer.AuctionId && !s.IsDeleted, cancellationToken);

        if (settlement is not null && (settlement.OrderId is not null || settlement.Status != AuctionSettlementStatus.PendingPayment))
        {
            offer.SettlementState = BackgroundSyncState.Done;
            offer.AttemptCount = 0;
            offer.LastError = null;
            offer.NextRetryAt = null;
            offer.NeedsAttention = false;
            offer.UpdatedAt = now;
            await TrySaveAsync(cancellationToken);
            return;
        }

        _commerceSync.PushSettlement(offer.AuctionId);
        offer.AttemptCount += 1;
        if (offer.AttemptCount > 10)
        {
            offer.NeedsAttention = true;
            offer.NextRetryAt = null;
        }
        else
        {
            offer.NextRetryAt = now.AddMinutes(GetBackoffMinutes(offer.AttemptCount));
        }
        offer.UpdatedAt = now;
        await TrySaveAsync(cancellationToken);
    }

    private static int GetBackoffMinutes(int attempt) => attempt switch
    {
        <= 1 => 1,
        2 => 2,
        3 => 5,
        4 => 10,
        _ => 30
    };

    /// <summary>Lưu thay đổi. Xung đột đồng thời (RowVersion của phiên đã bị đổi) thì trả false thay vì ném lỗi, các lỗi khác vẫn ném ra.</summary>
    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Clear the shared ChangeTracker so dirty entities from a failed item do not poison subsequent items in the batch.
            if (_dbContext is DbContext efContext)
            {
                efContext.ChangeTracker.Clear();
            }
            else
            {
                var unsavedOutbox = _dbContext.NotificationOutbox.Local.ToList();
                if (unsavedOutbox.Count > 0)
                    _dbContext.NotificationOutbox.RemoveRange(unsavedOutbox);
            }
            return false;
        }
    }

    /// <summary>Hết hạn các phiên đang AwaitingSellerApproval quá 12 giờ: chuyển Failed (ReserveNotMet), hoàn cọc Hạng 1 và tính 1 lần từ chối cho Seller.</summary>
    private async Task ExpireDueSellerApprovalsAsync(int limit, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var dueSettlements = await _dbContext.AuctionSettlements
            .Where(s => !s.IsDeleted
                        && s.Status == AuctionSettlementStatus.AwaitingSellerApproval
                        && s.SellerApprovalDeadlineAt != null
                        && s.SellerApprovalDeadlineAt <= now)
            .OrderBy(s => s.SellerApprovalDeadlineAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        foreach (var settlement in dueSettlements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var auction = await _dbContext.Auctions
                .Include(a => a.AuctionState)
                .FirstOrDefaultAsync(a => a.Id == settlement.AuctionId && !a.IsDeleted, cancellationToken);
            if (auction?.AuctionState is null || auction.AuctionState.Status != AuctionStatus.Ended)
                continue;

            var state = auction.AuctionState;
            settlement.Status = AuctionSettlementStatus.Expired;
            settlement.UpdatedAt = now;

            state.Status = AuctionStatus.Failed;
            state.UpdatedAt = now;
            state.RowVersion += 1;

            auction.StockReleaseState = BackgroundSyncState.Pending;
            auction.NextRetryAt = now;
            auction.NeedsAttention = false;
            auction.UpdatedAt = now;

            var openAuction = await _dbContext.ProductOpenAuctions
                .FirstOrDefaultAsync(p => p.AuctionId == auction.Id, cancellationToken);
            if (openAuction is not null)
                _dbContext.ProductOpenAuctions.Remove(openAuction);

            var depositsToRelease = await _dbContext.AuctionDeposits
                .Where(d => d.AuctionId == auction.Id && !d.IsDeleted &&
                            (d.Status == DepositStatus.Authorized || d.Status == DepositStatus.Pending))
                .ToListAsync(cancellationToken);
            foreach (var deposit in depositsToRelease)
            {
                deposit.Status = DepositStatus.Released;
                deposit.ReleasePending = true;
                deposit.PendingAction = DepositPendingAction.Release;
                deposit.ReleaseReason = "RESERVE_NOT_MET";
                deposit.NextRetryAt = now;
                deposit.NeedsAttention = false;
                deposit.UpdatedAt = now;
                deposit.RowVersion += 1;
            }

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
            if (penalty.ReserveRestrictedUntil is { } restrictedUntil && restrictedUntil <= now)
            {
                penalty.ConsecutiveDeclineCount = 0;
                penalty.ReserveRestrictedUntil = null;
            }
            penalty.ConsecutiveDeclineCount += 1;
            if (penalty.ConsecutiveDeclineCount >= AuctionRules.MaxConsecutiveReserveDeclines)
                penalty.ReserveRestrictedUntil = now.AddDays(AuctionRules.ReserveRestrictionDays);
            penalty.UpdatedAt = now;

            _dbContext.AuctionStatusHistories.Add(new AuctionStatusHistory
            {
                Id = Guid.NewGuid(),
                AuctionId = auction.Id,
                FromStatus = AuctionStatus.Ended,
                ToStatus = AuctionStatus.Failed,
                Reason = "ReserveNotMet: Seller did not approve the below-reserve bid within 12 hours.",
                ChangedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });

            _notifications.NotifyAuctionFailed(new AuctionFailedNotification(
                auction.Id, auction.SellerId, auction.SnapshotProductTitle,
                "ReserveNotMet: Seller approval timed out after 12 hours.", now, null));
            _notifications.NotifyReserveRestriction(new ReserveRestrictionNotification(
                auction.Id, auction.SellerId, penalty.ConsecutiveDeclineCount,
                penalty.ConsecutiveDeclineCount >= AuctionRules.MaxConsecutiveReserveDeclines, penalty.ReserveRestrictedUntil, now));

            if (await TrySaveAsync(cancellationToken))
            {
                if (depositsToRelease.Count > 0)
                    await _deposits.ReleasePendingDepositsForAuctionAsync(auction.Id, cancellationToken);
                _catalogSync.UnlockProduct(auction.ProductId, auction.Id);
                await _notifier.AuctionStatusChangedAsync(
                    auction.Id, new AuctionStatusChangedMessage(auction.Id, AuctionStatus.Failed.ToString(), now, "ReserveNotMet"), CancellationToken.None);
            }
        }
    }
}