namespace Auction.Application.Services.AuctionConfig;

using System;
using System.Collections.Generic;
using System.Text.Json;

public sealed record AuctionConfigDto(
    Guid ConfigId,
    string ConfigKey,
    JsonElement Value,
    string? Description,
    int Version,
    bool IsActive,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt);

/// <summary>Một phiên bản trong lịch sử của cấu hình.</summary>
public sealed record AuctionConfigHistoryItemDto(
    Guid ConfigId,
    int Version,
    bool IsActive,
    JsonElement Value,
    IReadOnlyList<AuctionConfigChangeDto> Changes,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt);

/// <summary>Một trường thay đổi so với phiên bản liền trước. Giá trị null nghĩa là trường chưa tồn tại ở phiên bản đó.</summary>
public sealed record AuctionConfigChangeDto(
    string Field,
    JsonElement? OldValue,
    JsonElement? NewValue);