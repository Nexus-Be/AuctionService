namespace Auction.Application.Services.Auction;

using System;
using global::Auction.Domain.Enums;

public sealed record AuctionCreatedResponse(
    Guid AuctionId,
    Guid ProductId,
    Guid? SkuId,
    string? ProductName,
    AuctionStatus Status,
    decimal StartPriceAmount,
    decimal BidStepAmount,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset ScheduledEndAt,
    int AntiSnipingExtensionMinutes,
    int MaxExtensionCount,
    DateTimeOffset CreatedAt,
    decimal? ReservePriceAmount = null,
    decimal? BuyNowPriceAmount = null);

public sealed record AuctionUpdatedResponse(
    Guid AuctionId,
    AuctionStatus Status,
    decimal StartPriceAmount,
    decimal BidStepAmount,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset ScheduledEndAt,
    DateTimeOffset UpdatedAt,
    decimal? ReservePriceAmount = null,
    decimal? BuyNowPriceAmount = null);

public sealed record AuctionCancelledResponse(
    Guid AuctionId,
    AuctionStatus FromStatus,
    AuctionStatus Status,
    string? Reason,
    DateTimeOffset CancelledAt,
    int DepositsToRelease,
    string StockRelease);

public sealed record AuctionRecreatedResponse(
    Guid AuctionId,
    Guid RecreatedFromAuctionId,
    Guid ProductId,
    Guid? SkuId,
    AuctionStatus Status,
    decimal StartPriceAmount,
    decimal BidStepAmount,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset ScheduledEndAt,
    DateTimeOffset CreatedAt,
    decimal? ReservePriceAmount = null,
    decimal? BuyNowPriceAmount = null);

public sealed record BuyNowResponse(
    Guid AuctionId,
    Guid WinnerId,
    decimal BuyNowPriceAmount,
    decimal DepositAmountHeld,
    decimal RemainingGoodsAmountHeld,
    AuctionStatus Status,
    DateTimeOffset EndedAt,
    DateTimeOffset PaymentDeadlineAt,
    long RowVersion);

public sealed record ReserveApprovalResponse(
    Guid AuctionId,
    AuctionStatus AuctionStatus,
    AuctionSettlementStatus SettlementStatus,
    decimal? AcceptedPriceAmount,
    DateTimeOffset? PaymentDeadlineAt,
    int ConsecutiveDeclineCount,
    DateTimeOffset? ReserveRestrictedUntil,
    DateTimeOffset RespondedAt);

public sealed record SellerReserveStatusResponse(
    Guid SellerId,
    int ConsecutiveDeclineCount,
    bool IsReserveRestricted,
    DateTimeOffset? ReserveRestrictedUntil);

/// <summary>Phản hồi sau khi AuctionService xử lý kết quả thanh toán từ Commerce.</summary>
public sealed record SettlementPaymentStatusResponse(
    Guid AuctionId,
    Guid OrderId,
    Guid WinnerId,
    AuctionStatus PreviousStatus,
    AuctionStatus Status,
    SettlementPaymentStatus PaymentStatus,
    DateTimeOffset ProcessedAt);

/// <summary>Kết quả sau khi bidder chấp nhận hoặc từ chối lời mời. PaymentDeadlineAt chỉ có khi chấp nhận.</summary>
public sealed record WinnerOfferResponse(
    Guid OfferId,
    Guid AuctionId,
    AuctionWinnerOfferStatus OfferStatus,
    AuctionStatus Status,
    decimal Amount,
    DateTimeOffset RespondedAt,
    DateTimeOffset? PaymentDeadlineAt);

/// <summary>Phản hồi khi người về nhì chấp nhận lời mời mua lại (API 07).</summary>
public sealed record SecondChanceAcceptResponse(
    Guid OfferId,
    string Status,
    string SettlementStatus,
    Guid? OrderId,
    decimal AmountToPay,
    DateTimeOffset PaymentDeadlineAt);

/// <summary>Phản hồi khi người về nhì từ chối lời mời mua lại (API 08).</summary>
public sealed record SecondChanceDeclineResponse(
    Guid OfferId,
    string Status,
    string AuctionStatus);

public sealed record AdminSyncRetryResponse(
    Guid AuctionId,
    bool PushedCommerceSettlement,
    bool PushedCatalogUnlock,
    int ResetPendingFlags,
    DateTimeOffset RetriedAt);

public sealed record InvalidateBidResponse(
    Guid AuctionId,
    Guid BidId,
    BidInvalidationReason Reason,
    decimal? NewCurrentPriceAmount,
    Guid? NewCurrentWinnerId,
    int NewBidCount,
    DateTimeOffset InvalidatedAt);

public sealed record WatchAuctionResponse(
    Guid AuctionId,
    bool IsWatching,
    bool NotifyOnNewBid,
    DateTimeOffset UpdatedAt);

public sealed record CreateAuctionReportResponse(
    Guid ReportId,
    Guid AuctionId,
    AuctionReportReason Reason,
    AuctionReportStatus Status,
    DateTimeOffset CreatedAt);

public sealed record DismissAuctionReportsResponse(
    Guid AuctionId,
    int DismissedCount,
    string Note,
    DateTimeOffset ReviewedAt);