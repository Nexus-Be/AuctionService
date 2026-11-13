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

public sealed class AuctionLifecycleService : IAuctionLifecycleService
{
    /// <summary>Thời hạn thanh toán của người thắng, tính từ lúc phiên kết thúc (theo business rule: 24 giờ).</summary>
    private static readonly TimeSpan PaymentWindow = TimeSpan.FromHours(24);

    private readonly IAuctionDbContext _dbContext;
    private readonly TimeProvider _clock;
    private readonly IAuctionNotifier _notifier;
    private readonly INotificationClient _notifications;
    private readonly ICatalogSync _catalogSync;
    private readonly ICommerceSync _commerceSync;
    private readonly IDepositService _deposits;

    /// <summary>Khởi tạo service với DbContext, đồng hồ hệ thống, bộ thông báo realtime và service cọc.</summary>
    public AuctionLifecycleService(
        IAuctionDbContext dbContext, TimeProvider clock, IAuctionNotifier notifier, INotificationClient notifications,
        ICatalogSync catalogSync, ICommerceSync commerceSync, IDepositService deposits)
    {
        _dbContext = dbContext;
        _clock = clock;
        _notifier = notifier;
        _notifications = notifications;
        _catalogSync = catalogSync;
        _commerceSync = commerceSync;
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

        var winningBid = await _dbContext.BidHistories
            .AsNoTracking()
            .Where(h => h.AuctionId == auctionId && !h.IsInvalidated && !h.IsDeleted)
            .OrderByDescending(h => h.Amount)
            .ThenBy(h => h.SequenceNo)
            .Select(h => new { h.BidId, h.BidderId, h.Amount })
            .FirstOrDefaultAsync(cancellationToken);

        state.EndedAt = now;
        state.UpdatedAt = now;
        state.RowVersion += 1;

        AuctionStatus toStatus;
        string reason;
        object newValues;
        AuctionWinnerNotification? winnerNotice = null;

        if (winningBid is null)
        {
            toStatus = AuctionStatus.Failed;
            reason = "Auction ended without a valid bid.";
            state.Status = toStatus;
            newValues = new { status = toStatus.ToString(), endedAt = now };

            // Nhả sản phẩm để seller tạo lại phiên. Index unique theo ProductId bỏ qua xoá mềm nên phải xoá thật dòng này.
            var openAuction = await _dbContext.ProductOpenAuctions
                .FirstOrDefaultAsync(p => p.AuctionId == auctionId, cancellationToken);
            if (openAuction is not null)
                _dbContext.ProductOpenAuctions.Remove(openAuction);
        }
        else
        {
            toStatus = AuctionStatus.Ended;
            reason = "Auction time expired.";
            state.Status = toStatus;
            state.CurrentWinnerId = winningBid.BidderId;
            state.CurrentPriceAmount = winningBid.Amount;

            var paymentDeadline = now.Add(PaymentWindow);
            newValues = new
            {
                status = toStatus.ToString(),
                endedAt = now,
                winnerId = winningBid.BidderId,
                winningBidId = winningBid.BidId,
                finalPriceAmount = winningBid.Amount,
                paymentDeadlineAt = paymentDeadline
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
            settlement.Status = AuctionSettlementStatus.PendingPayment;
            settlement.PaymentDeadlineAt = paymentDeadline;
            settlement.UpdatedAt = now;

            // Gửi thông báo cho người thắng sau khi kết quả đã được lưu (xem bên dưới).
            winnerNotice = new AuctionWinnerNotification(
                auctionId, auction.SnapshotProductTitle, result.Id, winningBid.BidId, winningBid.BidderId,
                auction.SellerId, winningBid.Amount, now, paymentDeadline);
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

        // Đánh dấu giải phóng cọc cho các bidder không thắng (hoặc tất cả bidder nếu phiên không có bid hợp lệ).
        var depositsToRelease = await _dbContext.AuctionDeposits
            .Where(d => d.AuctionId == auctionId && !d.IsDeleted &&
                        (d.Status == DepositStatus.Authorized || d.Status == DepositStatus.Pending) &&
                        (winningBid == null || d.BidderId != winningBid.BidderId))
            .ToListAsync(cancellationToken);

        var releaseReason = winningBid is null ? "AUCTION_FAILED" : "AUCTION_ENDED_NOT_WINNER";
        foreach (var deposit in depositsToRelease)
        {
            var oldStatus = deposit.Status;
            deposit.Status = DepositStatus.Released;
            deposit.ReleaseReason = releaseReason;
            // Always set ReleasePending = true: a Pending deposit without GatewayReference may still have locked funds at Wallet if W1 timed out.
            deposit.ReleasePending = true;
            deposit.UpdatedAt = now;
            deposit.RowVersion += 1;

            _dbContext.AddAuditLog(
                AuditAction.DepositRelease, "AuctionDeposit", deposit.Id.ToString(),
                oldValues: new { status = oldStatus },
                newValues: new { depositId = deposit.Id, status = deposit.Status, releaseReason, releasePending = deposit.ReleasePending });
        }

        if (!await TrySaveAsync(cancellationToken))
            return false;

        if (depositsToRelease.Count > 0)
            await _deposits.ReleasePendingDepositsForAuctionAsync(auctionId, cancellationToken);

        await _notifier.AuctionStatusChangedAsync(
            auctionId, new AuctionStatusChangedMessage(auctionId, toStatus.ToString(), now), CancellationToken.None);

        // Báo cho người thắng qua Notification service (gửi nền, không chờ, lỗi chỉ ghi log).
        // Phiên không có bid hợp lệ thì phiên Failed: mở khoá sản phẩm ở Catalog (gửi nền, có thử lại).
        if (winnerNotice is not null)
        {
            _notifications.NotifyAuctionWinner(winnerNotice);

            // Đẩy settlement sang Commerce để tạo đơn cho người thắng (gửi nền, có thử lại).
            _commerceSync.PushSettlement(auctionId);
        }
        else
        {
            _catalogSync.UnlockProduct(auction.ProductId, auctionId);
            _notifications.NotifyAuctionFailed(new AuctionFailedNotification(
                auctionId, auction.SellerId, auction.SnapshotProductTitle,
                "Auction ended without a valid bid.", now, null));
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
        if (failAuction)
        {
            state!.Status = AuctionStatus.Failed;
            state.UpdatedAt = now;
            state.RowVersion += 1;

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

        if (!await TrySaveAsync(cancellationToken))
            return false;

        if (failAuction)
        {
            // Đã lưu: mở khoá sản phẩm ở Catalog (gửi nền, có thử lại), rồi báo realtime phiên đã Failed.
            _catalogSync.UnlockProduct(auction!.ProductId, offer.AuctionId);
            _notifications.NotifyAuctionFailed(new AuctionFailedNotification(
                offer.AuctionId, auction.SellerId, auction.SnapshotProductTitle,
                "The replacement winner offer expired without an answer.", now, null));

            await _notifier.AuctionStatusChangedAsync(
                offer.AuctionId, new AuctionStatusChangedMessage(offer.AuctionId, AuctionStatus.Failed.ToString(), now), CancellationToken.None);
        }

        return true;
    }

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
            return false;
        }
    }
}