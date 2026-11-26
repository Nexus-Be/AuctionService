namespace Auction.Application.Services.Auction;

using System;
using System.Collections.Generic;
using System.Text.Json;
using global::Auction.Domain.Enums;

public sealed record AuctionSummaryDto(
    Guid AuctionId,
    Guid ProductId,
    Guid? SkuId,
    Guid SellerId,
    string? ProductName,
    string? PrimaryImageUrl,
    AuctionStatus Status,
    decimal StartPriceAmount,
    decimal? CurrentPriceAmount,
    int BidCount,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset ScheduledEndAt,
    DateTimeOffset CreatedAt,
    decimal? BuyNowPriceAmount = null,
    bool IsReserveMet = true,
    bool BuyNowAvailable = false);

public sealed record SellerAuctionStatisticsDto(
    Guid SellerId,
    int SuccessfulAuctions,
    int FailedAuctions,
    int CompletedAuctions,
    decimal? SuccessRate);

public sealed record AuctionImageDto(string Url, bool IsPrimary);

public sealed record AuctionDetailDto(
    Guid AuctionId,
    Guid ProductId,
    Guid? SkuId,
    Guid SellerId,
    string? ProductName,
    string? Description,
    string? PrimaryImageUrl,
    IReadOnlyList<AuctionImageDto> Images,
    JsonElement? Specifications,
    AuctionStatus Status,
    decimal StartPriceAmount,
    decimal BidStepAmount,
    decimal? CurrentPriceAmount,
    int BidCount,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset OriginalEndAt,
    DateTimeOffset ScheduledEndAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int ExtensionCount,
    int MaxExtensionCount,
    int AntiSnipingExtensionMinutes,
    Guid? RecreatedFromAuctionId,
    DateTimeOffset CreatedAt,
    long RowVersion,
    decimal? BuyNowPriceAmount = null,
    bool HasReservePrice = false,
    bool IsReserveMet = true,
    bool BuyNowAvailable = false,
    string? LeadingBidderDisplayName = null);

public sealed record AuctionStatusHistoryDto(
    Guid HistoryId,
    AuctionStatus? FromStatus,
    AuctionStatus ToStatus,
    string? Reason,
    Guid? ChangedBy,
    DateTimeOffset ChangedAt);

public sealed record AuctionResultWinnerDto(
    Guid? BidderId,
    string? DisplayName);

public sealed record AuctionResultSettlementDto(
    Guid? OrderId,
    string SettlementStatus,
    decimal DepositAmount,
    decimal RemainingAmount,
    DateTimeOffset? PaymentDeadlineAt,
    bool AwaitingSellerApproval = false,
    DateTimeOffset? SellerApprovalDeadlineAt = null);

/// <summary>The prices of an auction as its own seller set them. The reserve price is secret from everyone else, so this is a separate view.</summary>
public sealed record SellerAuctionPricingDto(
    Guid AuctionId,
    decimal StartPriceAmount,
    decimal? ReservePriceAmount,
    decimal? BuyNowPriceAmount);

public sealed record AuctionResultMyDepositDto(
    DepositStatus Status,
    decimal Amount);

public sealed record AuctionResultSecondChanceDto(
    Guid? OfferId,
    Guid? BidderId,
    string Status,
    decimal? OfferPrice,
    DateTimeOffset? RespondBy);

public sealed record AuctionResultDto(
    Guid AuctionId,
    AuctionStatus? Status,
    string Phase,
    Guid? WinnerId,
    Guid? WinningBidId,
    decimal? FinalPriceAmount,
    DateTimeOffset? DeterminedAt,
    AuctionResultWinnerDto? Winner,
    decimal? WinningAmount,
    AuctionResultSettlementDto? Settlement,
    AuctionResultMyDepositDto? MyDeposit,
    AuctionResultSecondChanceDto? SecondChance);

/// <summary>Thông tin lời mời mua lại của người về nhì trong một phiên cụ thể (API 09).</summary>
public sealed record MySecondChanceOfferDto(
    Guid OfferId,
    string Status,
    decimal OfferPrice,
    string Currency,
    DateTimeOffset OfferedAt,
    DateTimeOffset RespondBy,
    Guid? OrderId,
    DateTimeOffset? PaymentDeadlineAt,
    string Notice);

/// <summary>Latest auction of a product. All fields except the product id are empty or false when the product was never auctioned.</summary>
public sealed record ProductAuctionStatusDto(
    Guid ProductId,
    Guid? AuctionId,
    AuctionStatus? Status,
    bool IsOpen,
    bool IsSold);

public sealed record AuctionInternalSummaryDto(
    Guid AuctionId,
    Guid ProductId,
    Guid SellerId,
    AuctionStatus Status,
    decimal StartingPriceAmount,
    decimal BidStepAmount,
    decimal? CurrentPriceAmount,
    Guid? CurrentLeadingBidderId,
    string? CurrentLeadingBidderMasked,
    int BidCount,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int ExtensionCount);

/// <summary>
/// Nội dung sự kiện AuctionCancelled đẩy tới mọi người đang xem phiên khi phiên bị huỷ.
/// Client dùng tin này để khoá nút đặt giá và báo phiên đã huỷ.
/// </summary>
public sealed record AuctionCancelledMessage(
    Guid AuctionId,
    DateTimeOffset CancelledAt);

/// <summary>
/// Nội dung sự kiện AuctionStatusChanged đẩy tới mọi người đang xem phiên khi trạng thái phiên đổi
/// (ví dụ Settled hoặc Failed sau khi Commerce báo kết quả thanh toán). Client cập nhật badge và khoá hoặc mở các nút tương ứng.
/// </summary>
public sealed record AuctionStatusChangedMessage(
    Guid AuctionId,
    string Status,
    DateTimeOffset ChangedAt,
    string? Reason = null);

/// <summary>
/// Một lời mời trở thành người thắng của bidder. IsExpired là true khi lời mời còn Pending nhưng đã quá ExpiresAt
/// (chưa có worker đánh dấu Expired nên client dựa vào cờ này).
/// </summary>
public sealed record WinnerOfferDto(
    Guid OfferId,
    Guid AuctionId,
    string AuctionTitle,
    Guid BidId,
    decimal Amount,
    AuctionWinnerOfferStatus Status,
    bool IsExpired,
    DateTimeOffset OfferedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RespondedAt,
    string? ResponseReason);

/// <summary>
/// Thông tin chốt kết quả của một phiên để Commerce tạo order: sản phẩm, SKU, seller, người thắng, giá chốt, hạn thanh toán.
/// OrderId và PaidAt chỉ có sau khi Commerce báo kết quả thanh toán qua callback payment-status.
/// </summary>
public sealed record AuctionSettlementDto(
    Guid AuctionId,
    Guid ProductId,
    Guid? SkuId,
    Guid SellerId,
    string ProductTitle,
    Guid? WinnerId,
    Guid? WinningBidId,
    decimal? FinalPriceAmount,
    AuctionSettlementStatus SettlementStatus,
    AuctionStatus Status,
    DateTimeOffset? PaymentDeadlineAt,
    Guid? OrderId,
    DateTimeOffset? PaidAt,
    DateTimeOffset DeterminedAt);

/// <summary>
/// Tiến trình xử lý hậu đấu giá (saga) của một phiên đã có người thắng, dựng từ dữ liệu có sẵn. Chỉ admin được xem.
/// SagaType luôn là AuctionSettlement, CurrentState là tên bước hiện tại, StateData chứa chi tiết các bước.
/// </summary>
public sealed record AuctionSagaDto(
    Guid AuctionId,
    string SagaType,
    string CurrentState,
    AuctionSagaStateData StateData,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Chi tiết trạng thái saga: phiên và seller, trạng thái phiên, đã kết thúc hay chưa, đang chờ gì (kèm hạn và cờ quá hạn) và danh sách các bước.</summary>
public sealed record AuctionSagaStateData(
    Guid ProductId,
    Guid SellerId,
    AuctionStatus Status,
    bool IsFinished,
    string? WaitingFor,
    DateTimeOffset? DeadlineAt,
    bool IsOverdue,
    IReadOnlyList<AuctionSagaStepDto> Steps);

/// <summary>Một bước của saga. State là Completed, InProgress, Pending, Skipped hoặc Failed; At là thời điểm bước đó xảy ra (nếu đã xảy ra).</summary>
public sealed record AuctionSagaStepDto(string Name, string State, DateTimeOffset? At, string? Detail);

public sealed record MyParticipatedAuctionDto(
    Guid AuctionId,
    Guid ProductId,
    Guid? SkuId,
    Guid SellerId,
    string? ProductName,
    string? PrimaryImageUrl,
    AuctionStatus Status,
    decimal StartPriceAmount,
    decimal? CurrentPriceAmount,
    decimal? MyHighestBidAmount,
    bool IsWinning,
    string ParticipationState,
    DepositStatus? MyDepositStatus,
    Guid? OrderId,
    int BidCount,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset ScheduledEndAt);

public sealed record AdminAuctionSummaryDto(
    Guid AuctionId,
    Guid ProductId,
    Guid? SkuId,
    Guid SellerId,
    string? ProductName,
    AuctionStatus Status,
    decimal StartPriceAmount,
    decimal? CurrentPriceAmount,
    int BidCount,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset ScheduledEndAt,
    bool HasAttentionDeposit,
    bool HasOverdueSettlement,
    bool NeedsAttention,
    DateTimeOffset CreatedAt);

public sealed record MyWatchlistItemDto(
    Guid AuctionId,
    Guid ProductId,
    Guid? SkuId,
    Guid SellerId,
    string? ProductName,
    string? PrimaryImageUrl,
    AuctionStatus Status,
    decimal StartPriceAmount,
    decimal? CurrentPriceAmount,
    int BidCount,
    bool NotifyOnNewBid,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset ScheduledEndAt,
    DateTimeOffset WatchedAt);

public sealed record MyAuctionReportDto(
    Guid ReportId,
    Guid AuctionId,
    string? AuctionTitle,
    AuctionStatus? AuctionStatus,
    AuctionReportReason Reason,
    string Description,
    AuctionReportStatus Status,
    string? AdminNote,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt);

public sealed record AdminAuctionReportDto(
    Guid ReportId,
    Guid AuctionId,
    string? AuctionTitle,
    AuctionStatus? AuctionStatus,
    Guid SellerId,
    Guid ReporterId,
    AuctionReportReason Reason,
    string Description,
    AuctionReportStatus Status,
    string? AdminNote,
    Guid? ReviewedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt);