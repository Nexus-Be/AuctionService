namespace Auction.Application.Services.Deposit;

using System;
using global::Auction.Domain.Enums;

public sealed record BuyerStandingDto(int? ReputationScore, bool IsNewUser, int ViolationStreak);

/// <summary>Cách chia cọc khi Winner không thanh toán.</summary>
public sealed record ForfeiturePolicyDto(decimal SellerPercent, decimal PlatformPercent);

public sealed record ExistingDepositDto(Guid DepositId, DepositStatus Status);

/// <summary>
/// Điều khoản cọc cho Buyer trước khi đóng. DepositAmount do server tính.
/// CanJoin là false khi Buyer đã có cọc Authorized (hoặc cọc đã đóng), true khi chưa có cọc hoặc cọc có thể thử lại.
/// </summary>
public sealed record DepositRequirementResponse(
    Guid AuctionId,
    string Currency,
    decimal StartPrice,
    decimal DepositAmount,
    decimal RatePercent,
    bool MinDepositApplied,
    int PolicyVersion,
    BuyerStandingDto BuyerStanding,
    ForfeiturePolicyDto ForfeiturePolicy,
    bool CanJoin,
    ExistingDepositDto? ExistingDeposit);

/// <summary>Kết quả đóng cọc đồng bộ bằng số dư ví Nexus (Hướng C).</summary>
public sealed record CreateDepositResponse(
    Guid DepositId,
    DepositStatus Status,
    decimal Amount,
    decimal RatePercent,
    int PolicyVersion);

/// <summary>StatusCode là 201 khi vừa tạo hold mới, 200 khi trả lại bản ghi đã có.</summary>
public sealed record CreateDepositResult(CreateDepositResponse Body, int StatusCode);

/// <summary>Applied là false khi callback là bản trùng, cũ hoặc không đổi được trạng thái. Wallet vẫn nhận 200 để không thử lại mãi.</summary>
public sealed record DepositAuthorizationResultResponse(Guid DepositId, DepositStatus Status, bool Applied);

public sealed record DepositSummaryDto(
    int Total,
    int Authorized,
    int Released,
    int Captured,
    int Failed,
    int Expired,
    decimal AmountHeld,
    decimal AmountCaptured);

public sealed record AdminAuctionDepositItemDto(
    Guid DepositId,
    Guid BidderId,
    decimal Amount,
    decimal RatePercent,
    string Status,
    string? CapturePurpose,
    string? GatewayReference,
    int PolicyVersion,
    DateTimeOffset? AuthorizedAt,
    DateTimeOffset? ReleasedAt,
    DateTimeOffset? CapturedAt,
    string? PendingAction,
    bool NeedsAttention);

public sealed record AdminAuctionDepositsResponse(
    Guid AuctionId,
    DepositSummaryDto Summary,
    IReadOnlyList<AdminAuctionDepositItemDto> Items,
    int Page,
    int PageSize,
    int TotalItems);

/// <summary>Trạng thái cọc của chính người gọi trong một phiên đấu giá (API 03, Hướng C).</summary>
public sealed record MyDepositResponse(
    Guid? DepositId,
    string Status,
    decimal? Amount,
    decimal? RatePercent,
    DateTimeOffset? AuthorizedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ReleasedAt,
    DateTimeOffset? CapturedAt,
    string? CapturePurpose,
    bool CanBid,
    string? BlockReason);

