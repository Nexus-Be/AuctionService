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
    DateTimeOffset CreatedAt);