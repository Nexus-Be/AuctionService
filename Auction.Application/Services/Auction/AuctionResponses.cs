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
    DateTimeOffset CreatedAt);

public sealed record AuctionUpdatedResponse(
    Guid AuctionId,
    AuctionStatus Status,
    decimal StartPriceAmount,
    decimal BidStepAmount,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset ScheduledEndAt,
    DateTimeOffset UpdatedAt);

public sealed record AuctionCancelledResponse(
    Guid AuctionId,
    AuctionStatus FromStatus,
    AuctionStatus Status,
    string? Reason,
    DateTimeOffset CancelledAt);

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
    DateTimeOffset CreatedAt);