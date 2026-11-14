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
    private readonly TimeProvider _clock;
    private readonly IAuctionNotifier _notifier;
    private readonly INotificationClient _notifications;
    private readonly ICatalogSync _catalogSync;
    private readonly ICommerceSync _commerceSync;
    private readonly IDepositService _deposits;

    /// <summary>Khởi tạo service với DbContext, các client gọi Catalog và User, đồng hồ hệ thống, bộ thông báo realtime và service cọc.</summary>
    public AuctionService(
        IAuctionDbContext dbContext, ICatalogClient catalog, IUserClient users, TimeProvider clock, IAuctionNotifier notifier,
        INotificationClient notifications, ICatalogSync catalogSync, ICommerceSync commerceSync, IDepositService deposits)
    {
        _dbContext = dbContext;
        _catalog = catalog;
        _users = users;
        _clock = clock;
        _notifier = notifier;
        _notifications = notifications;
        _catalogSync = catalogSync;
        _commerceSync = commerceSync;
        _deposits = deposits;
    }

    // ------------------------------------------------------------------ Queries

    /// <summary>Lấy danh sách phiên đấu giá có phân trang, lọc theo trạng thái (tuỳ chọn). Dùng cho trang danh sách công khai.</summary>
    public Task<PagedData<AuctionSummaryDto>> ListActiveAuctionsAsync(
        int page = 1, int pageSize = 20, string? status = null, CancellationToken cancellationToken = default)
        => ListAuctionsAsync(sellerId: null, page, pageSize, status, cancellationToken);

    /// <summary>Lấy danh sách phiên của riêng một seller (có phân trang, lọc theo trạng thái). Dùng cho trang quản lý của seller.</summary>
    public Task<PagedData<AuctionSummaryDto>> ListSellerAuctionsAsync(
        Guid sellerId, int page = 1, int pageSize = 20, string? status = null, CancellationToken cancellationToken = default)
        => ListAuctionsAsync(sellerId, page, pageSize, status, cancellationToken);

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
            var auctionIdText = auctionId.ToString();
            var sellerAccepted = await _dbContext.AuditLogs
                .AsNoTracking()
                .AnyAsync(a => a.Action == AuditAction.SellerAccept && a.EntityName == "Auction" && a.EntityId == auctionIdText, cancellationToken);
            phase = sellerAccepted ? "SETTLED" : "WINNER_PAID_AWAITING_SELLER";
        }
        else
        {
            // Status == Ended
            if (offer is { Status: AuctionWinnerOfferStatus.Pending })
                phase = "SECOND_CHANCE_OFFERED";
            else if (offer is { Status: AuctionWinnerOfferStatus.Accepted })
                phase = "AWAITING_RUNNER_UP_PAYMENT";
            else
                phase = "AWAITING_WINNER_PAYMENT";
        }

        // Cọc của Winner (chỉ tính khi người thắng hiện tại là người thắng đầu tiên; người về nhì có depositAmount = 0)
        decimal winnerDepositAmount = 0m;
        if (result?.WinnerId is { } currentWinnerId &&
            (offer is null || offer.Status != AuctionWinnerOfferStatus.Accepted || offer.BidderId != currentWinnerId))
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

        var formattedPrice = offer.Amount.ToString("N0", CultureInfo.InvariantCulture);
        var notice = $"You will pay the full amount of {formattedPrice} VND. Your previous deposit was already released and is not deducted from this amount.";

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
            ? request.PaidAt
            : null;
        auction.AuctionSettlement.UpdatedAt = now;

        // Thanh toán Expired hoặc Failed: mời bidder hợp lệ kế tiếp (mỗi phiên chỉ có một lời mời). Không có ai thì phiên Failed như trước.
        var isSecondChanceNonPayment = await _dbContext.AuctionWinnerOffers
            .AsNoTracking()
            .AnyAsync(o => o.AuctionId == auctionId && o.BidderId == winnerId && o.Status == AuctionWinnerOfferStatus.Accepted, cancellationToken);

        AuctionWinnerOffer? offer = null;
        if (paymentStatus is SettlementPaymentStatus.Expired or SettlementPaymentStatus.PaymentTimedOut or SettlementPaymentStatus.Failed)
            offer = await TryCreateWinnerOfferAsync(auctionId, auction.ProductId, winnerId, now, cancellationToken);

        if (offer is null)
        {
            state.Status = paymentStatus == SettlementPaymentStatus.Paid
                ? AuctionStatus.Settled
                : AuctionStatus.Failed;
            state.UpdatedAt = now;
            state.RowVersion += 1;

            // Thanh toán không thành công (Failed, Expired, Cancelled) mà không có bidder kế tiếp thì nhả sản phẩm để seller tạo lại phiên.
            // Index unique theo ProductId bỏ qua xoá mềm nên phải xoá thật dòng này. Paid thì giữ nguyên vì sản phẩm đã bán.
            if (paymentStatus != SettlementPaymentStatus.Paid)
            {
                var openAuction = await _dbContext.ProductOpenAuctions
                    .FirstOrDefaultAsync(p => p.AuctionId == auctionId, cancellationToken);
                if (openAuction is not null)
                    _dbContext.ProductOpenAuctions.Remove(openAuction);
            }

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

        await SaveAsync(null, cancellationToken);

        // Ghi nhận vi phạm không thanh toán (tăng ViolationStreak, trừ uy tín và áp chế tài đấu giá) bên UserService (API 20).
        if (paymentStatus is SettlementPaymentStatus.Expired or SettlementPaymentStatus.PaymentTimedOut or SettlementPaymentStatus.Failed)
        {
            await TryRecordNonPaymentViolationAsync(
                auctionId, orderId, winnerId, isSecondChanceNonPayment, now, cancellationToken);
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

            if (state.Status == AuctionStatus.Failed)
            {
                _notifications.NotifyAuctionFailed(new AuctionFailedNotification(
                    auctionId, auction.SellerId, auction.SnapshotProductTitle,
                    $"Commerce payment status: {paymentStatus}.", now, null));
            }
        }
        else
        {
            _notifications.NotifyReplacementWinnerOffer(new ReplacementWinnerOfferNotification(
                auctionId, offer.Id, offer.BidId, offer.BidderId, winnerId, auction.SellerId,
                auction.SnapshotProductTitle, offer.Amount, offer.OfferedAt, offer.ExpiresAt, null));
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

        if (state.Status != AuctionStatus.Settled || settlement is null || settlement.Status != AuctionSettlementStatus.Paid)
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
            if (!alreadyAccepted)
            {
                _dbContext.AddAuditLog(
                    AuditAction.SellerAccept, "Auction", auctionIdText,
                    oldValues: new { status = previousStatus.ToString(), orderId },
                    newValues: new { status = previousStatus.ToString(), orderId, sellerDecision = decision.ToString() });
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

        await SaveAsync(null, cancellationToken);

        // Đã lưu. Chấp nhận: chốt bán ở Catalog. Từ chối, quá hạn hoặc đơn bị huỷ sau khi đã SellerAccepted: gọi revert-auctioned (hoặc unlock-auction nếu chưa chốt).
        if (decision == SettlementPaymentStatus.SellerAccepted)
        {
            _catalogSync.MarkProductAuctioned(auction.ProductId, auctionId);
        }
        else
        {
            if (alreadyAccepted)
                _catalogSync.RevertProductAuctioned(auction.ProductId, auctionId);
            else
                _catalogSync.UnlockProduct(auction.ProductId, auctionId);

            _notifications.NotifyAuctionFailed(new AuctionFailedNotification(
                auctionId, auction.SellerId, auction.SnapshotProductTitle,
                decision switch
                {
                    SettlementPaymentStatus.SellerRejected => "The seller rejected the order.",
                    SettlementPaymentStatus.SellerExpired => "The seller did not respond to the order in time.",
                    _ => "The order was cancelled after payment."
                },
                now, null));
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
        Guid? sellerId, int page, int pageSize, string? status, CancellationToken cancellationToken)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);
        var statusFilter = ParseStatus(status);

        var query = _dbContext.Auctions
            .AsNoTracking()
            .Where(a => !a.IsDeleted && a.AuctionState != null);

        if (sellerId is { } owner)
            query = query.Where(a => a.SellerId == owner);

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

        return new AuctionDetailDto(
            row.Id, row.ProductId, row.SkuId, row.SellerId, row.SnapshotProductTitle ?? product?.Name,
            product?.Description, images.FirstOrDefault()?.Url, images, ParseSpecifications(product?.SpecificationsJson),
            row.Status, row.StartPriceAmount, row.BidStepAmount, row.CurrentPriceAmount, row.BidCount,
            row.ScheduledStartAt, row.OriginalEndAt, row.ScheduledEndAt, row.StartedAt, row.EndedAt,
            row.ExtensionCount, row.MaxExtensionCount, row.AntiSnipingExtensionMinutes,
            row.RecreatedFromAuctionId, row.CreatedAt, row.RowVersion);
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
        AuctionRules.ValidateSchedule(request.ScheduledStartAt, request.ScheduledEndAt, now);

        // 2. User service: the caller must be an approved seller with enough reputation.
        await EnsureSellerEligibleAsync(actor.UserId, cancellationToken);

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
            a.AntiSnipingExtensionMinutes, a.MaxExtensionCount, a.CreatedAt);
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

        await SaveAsync(null, cancellationToken);

        // Giải phóng cọc ngay sau commit (fast path); lỗi mạng giữ ReleasePending = true để retry sau.
        if (depositsToRelease > 0)
            await _deposits.ReleasePendingDepositsForAuctionAsync(auction.Id, cancellationToken);

        // Phiên đã được huỷ và lưu: mở khoá sản phẩm ở Catalog (gửi nền, có thử lại).
        _catalogSync.UnlockProduct(auction.ProductId, auction.Id);

        // Báo cho người đang xem phiên để họ dừng đặt giá.
        await _notifier.AuctionCancelledAsync(
            auction.Id, new AuctionCancelledMessage(auction.Id, now), CancellationToken.None);
        _notifications.NotifyAuctionCancelled(new AuctionCancelledNotification(
            auction.Id, auction.SellerId, auction.SnapshotProductTitle, reason, now, false, actor.TraceId));

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

        await SaveAsync(null, cancellationToken);

        // Giải phóng cọc ngay sau commit (fast path); lỗi mạng giữ ReleasePending = true để retry sau.
        if (depositsToRelease > 0)
            await _deposits.ReleasePendingDepositsForAuctionAsync(auction.Id, cancellationToken);

        // Phiên đã bị huỷ và lưu: mở khoá sản phẩm ở Catalog (gửi nền, có thử lại).
        _catalogSync.UnlockProduct(auction.ProductId, auction.Id);

        // Báo cho người đang xem phiên để họ dừng đặt giá. Không trừ uy tín seller.
        await _notifier.AuctionCancelledAsync(
            auction.Id, new AuctionCancelledMessage(auction.Id, now), CancellationToken.None);
        _notifications.NotifyAuctionCancelled(new AuctionCancelledNotification(
            auction.Id, auction.SellerId, auction.SnapshotProductTitle, reason, now, true, actor.TraceId));

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
            // Always set ReleasePending = true: a Pending deposit without GatewayReference may still have locked funds at Wallet if W1 timed out.
            deposit.ReleasePending = true;
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

        await SaveAsync(null, cancellationToken);

        // Đã lưu: mở khoá sản phẩm ở Catalog (gửi nền, có thử lại), rồi báo cho người đang xem phiên rằng phiên đã chuyển sang Failed.
        _catalogSync.UnlockProduct(auction.ProductId, auctionId);

        _notifications.NotifyAuctionFailed(new AuctionFailedNotification(
            auctionId, auction.SellerId, auction.SnapshotProductTitle,
            "The invited replacement bidder declined the winner offer.", now, actor.TraceId));

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

        await SaveAsync(null, cancellationToken);

        _catalogSync.UnlockProduct(auction.ProductId, auctionId);

        _notifications.NotifyAuctionFailed(new AuctionFailedNotification(
            auctionId, auction.SellerId, auction.SnapshotProductTitle,
            "The invited second-chance bidder declined the offer.", now, actor.TraceId));

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
        await EnsureProductHasNoOpenAuctionAsync(source.ProductId, cancellationToken);

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
            a.StartPriceAmount, a.BidStepAmount, a.ScheduledStartAt, a.OriginalEndAt, a.CreatedAt);
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

    /// <summary>Gọi UserService (API 20) để ghi nhận vi phạm không thanh toán của Winner hoặc Người về nhì. Lỗi dịch vụ ngoài không làm hỏng giao dịch đã lưu.</summary>
    private async Task TryRecordNonPaymentViolationAsync(
        Guid auctionId, Guid orderId, Guid buyerId, bool isSecondChance, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        try
        {
            decimal depositForfeitedAmount = 0m;
            if (!isSecondChance)
            {
                var deposit = await _dbContext.AuctionDeposits
                    .AsNoTracking()
                    .Where(d => d.AuctionId == auctionId && d.BidderId == buyerId && !d.IsDeleted)
                    .Select(d => (decimal?)d.Amount)
                    .FirstOrDefaultAsync(cancellationToken);
                depositForfeitedAmount = deposit ?? 0m;
            }

            var violationType = isSecondChance ? "SecondChanceNonPayment" : "WinnerNonPayment";
            await _users.RecordAuctionViolationAsync(
                new RecordAuctionViolationCommand(
                    buyerId,
                    violationType,
                    auctionId,
                    orderId,
                    occurredAt,
                    depositForfeitedAmount),
                cancellationToken);
        }
        catch (DomainException)
        {
            // Non-payment violation recording is best-effort after settlement commit; User service is idempotent on retry.
        }
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