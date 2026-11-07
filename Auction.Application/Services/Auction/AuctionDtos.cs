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
    DateTimeOffset CreatedAt);

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
    long RowVersion);

public sealed record AuctionStatusHistoryDto(
    Guid HistoryId,
    AuctionStatus? FromStatus,
    AuctionStatus ToStatus,
    string? Reason,
    Guid? ChangedBy,
    DateTimeOffset ChangedAt);

public sealed record AuctionResultDto(
    Guid AuctionId,
    AuctionStatus? Status,
    Guid? WinnerId,
    Guid? WinningBidId,
    decimal? FinalPriceAmount,
    DateTimeOffset DeterminedAt);

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
    DateTimeOffset ChangedAt);

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