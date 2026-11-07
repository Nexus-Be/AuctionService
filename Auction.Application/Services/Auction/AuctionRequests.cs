namespace Auction.Application.Services.Auction;

using System;
using global::Auction.Domain.Enums;

// Prices and schedule are always chosen by the seller. Fields are nullable so that a missing value
// is reported with a clear message instead of being replaced by a default.

/// <summary>SkuId là mã biến thể (variant) của Catalog. ProductId không nhận từ client, được lấy từ Catalog theo SkuId.</summary>
public sealed record CreateAuctionRequest(
    Guid? SkuId,
    decimal? StartPriceAmount,
    decimal? BidStepAmount,
    DateTimeOffset? ScheduledStartAt,
    DateTimeOffset? ScheduledEndAt);

public sealed record UpdateAuctionRequest(
    decimal? StartPriceAmount,
    decimal? BidStepAmount,
    DateTimeOffset? ScheduledStartAt,
    DateTimeOffset? ScheduledEndAt);

public sealed record CancelAuctionRequest(string? Reason);

/// <summary>Dữ liệu admin gửi khi huỷ cưỡng chế một phiên. Lý do là bắt buộc.</summary>
public sealed record ForceCancelAuctionRequest(string? Reason);

public sealed record RecreateAuctionRequest(
    decimal? StartPriceAmount,
    decimal? BidStepAmount,
    DateTimeOffset? ScheduledStartAt,
    DateTimeOffset? ScheduledEndAt);

/// <summary>Trạng thái kết quả thanh toán do Commerce gửi về AuctionService.</summary>
public enum SettlementPaymentStatus
{
    /// <summary>Thanh toán đã thành công.</summary>
    Paid = 0,
    /// <summary>Thanh toán thất bại.</summary>
    Failed = 1,
    /// <summary>Thanh toán đã hết hạn.</summary>
    Expired = 2,
    /// <summary>Thanh toán đã bị huỷ.</summary>
    Cancelled = 3
}

/// <summary>Dữ liệu Commerce gửi để cập nhật kết quả thanh toán của phiên đấu giá.</summary>
public sealed record SettlementPaymentStatusRequest(
    Guid? OrderId,
    Guid? WinnerId,
    SettlementPaymentStatus? Status,
    decimal? Amount,
    DateTimeOffset? PaidAt,
    string? EventId);

/// <summary>Dữ liệu bidder gửi khi từ chối lời mời trở thành người thắng. Lý do là tuỳ chọn.</summary>
public sealed record DeclineWinnerOfferRequest(string? Reason);