namespace Auction.Application.Services.AuctionConfig;

using System;
using System.Collections.Generic;

public sealed record DepositPolicyTierDto(int MinReputation, int MaxReputation, decimal RatePercent);

public sealed record DepositPolicySurchargeDto(int Streak, decimal AdditionalPercent);

public sealed record DepositPolicyForfeitureDto(decimal SellerPercent, decimal PlatformPercent);

public sealed record DepositPolicyHistoryItemDto(
    int Version,
    DateTimeOffset EffectiveFrom,
    string? UpdatedBy,
    string? Reason);

public sealed record DepositPolicyResponse(
    int Version,
    DateTimeOffset EffectiveFrom,
    string? UpdatedBy,
    string? Reason,
    IReadOnlyList<DepositPolicyTierDto> Tiers,
    decimal NewUserRatePercent,
    IReadOnlyList<DepositPolicySurchargeDto> StreakSurcharges,
    decimal MaxRatePercent,
    decimal MinDepositAmount,
    DepositPolicyForfeitureDto Forfeiture,
    IReadOnlyList<DepositPolicyHistoryItemDto>? History);

public sealed record UpdateDepositPolicyRequest(
    string? Reason,
    IReadOnlyList<DepositPolicyTierDto>? Tiers,
    decimal? NewUserRatePercent,
    IReadOnlyList<DepositPolicySurchargeDto>? StreakSurcharges,
    decimal? MaxRatePercent,
    decimal? MinDepositAmount,
    DepositPolicyForfeitureDto? Forfeiture);

public sealed record UpdateDepositPolicyResponse(
    int Version,
    DateTimeOffset EffectiveFrom);
