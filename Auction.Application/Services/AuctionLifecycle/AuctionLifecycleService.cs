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
using global::Auction.Domain.Entities;
using global::Auction.Domain.Enums;

public sealed class AuctionLifecycleService : IAuctionLifecycleService
{
    /// <summary>Thời hạn thanh toán của người thắng, tính từ lúc phiên kết thúc (theo business rule: 24 giờ).</summary>
    private static readonly TimeSpan PaymentWindow = TimeSpan.FromHours(24);

    private readonly IAuctionDbContext _dbContext;
    private readonly TimeProvider _clock;
    private readonly IAuctionNotifier _notifier;

    /// <summary>Khởi tạo service với DbContext, đồng hồ hệ thống và bộ thông báo realtime.</summary>
    public AuctionLifecycleService(IAuctionDbContext dbContext, TimeProvider clock, IAuctionNotifier notifier)
    {
        _dbContext = dbContext;
        _clock = clock;
        _notifier = notifier;
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

            var result = auction.AuctionResult ??= new AuctionResult
            {
                Id = Guid.NewGuid(),
                AuctionId = auctionId,
                DeterminedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };
            result.WinnerId = winningBid.BidderId;
            result.WinningBidId = winningBid.BidId;
            result.FinalPriceAmount = winningBid.Amount;
            result.DeterminedAt = now;
            result.UpdatedAt = now;

            var settlement = auction.AuctionSettlement ??= new AuctionSettlement
            {
                Id = Guid.NewGuid(),
                AuctionId = auctionId,
                Status = AuctionSettlementStatus.PendingPayment,
                CreatedAt = now,
                UpdatedAt = now
            };
            settlement.WinnerId = winningBid.BidderId;
            settlement.WinningBidId = winningBid.BidId;
            settlement.Status = AuctionSettlementStatus.PendingPayment;
            settlement.PaymentDeadlineAt = paymentDeadline;
            settlement.UpdatedAt = now;
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

        if (!await TrySaveAsync(cancellationToken))
            return false;

        await _notifier.AuctionStatusChangedAsync(
            auctionId, new AuctionStatusChangedMessage(auctionId, toStatus.ToString(), now), CancellationToken.None);

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
