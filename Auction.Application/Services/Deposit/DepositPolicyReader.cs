namespace Auction.Application.Services.Deposit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using global::Auction.Application.Interfaces;
using global::Auction.Domain.Exceptions;
using global::Auction.Domain.Rules;

/// <summary>
/// Reads the active deposit policy from the DEPOSIT_POLICY row of AuctionConfigs (the Version column is the policy version).
/// Without that row the built-in default policy (version 0) is used. A row that cannot be understood is a configuration error (500),
/// never a silent fallback, because a wrong deposit amount would be charged to real buyers.
/// </summary>
internal static class DepositPolicyReader
{
    public const string ConfigKey = "DEPOSIT_POLICY";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<DepositPolicy> ReadAsync(IAuctionDbContext dbContext, CancellationToken cancellationToken)
    {
        var config = await dbContext.AuctionConfigs
            .AsNoTracking()
            .Where(c => c.ConfigKey == ConfigKey && c.IsActive && !c.IsDeleted)
            .OrderByDescending(c => c.Version)
            .FirstOrDefaultAsync(cancellationToken);

        return config is null ? DepositRules.DefaultPolicy : Parse(config.Version, config.ConfigValue);
    }

    internal static DepositPolicy Parse(int version, string json)
    {
        PolicyJson? policy;
        try
        {
            policy = JsonSerializer.Deserialize<PolicyJson>(json, JsonOptions);
        }
        catch (JsonException)
        {
            throw Invalid();
        }

        if (policy?.Tiers is not { Count: > 0 } ||
            policy.NewUserRatePercent is null || policy.MaxRatePercent is null ||
            policy.MinDepositAmount is null || policy.Forfeiture is null)
            throw Invalid();

        if (policy.MaxRatePercent is < 0m or > 100m || policy.MinDepositAmount < 0m)
            throw Invalid();

        return new DepositPolicy(
            version,
            policy.Tiers.Select(t => new DepositTier(t.MinReputation, t.MaxReputation, t.RatePercent)).ToList(),
            policy.NewUserRatePercent.Value,
            (policy.StreakSurcharges ?? new List<SurchargeJson>())
                .Select(s => new StreakSurcharge(s.Streak, s.AdditionalPercent)).ToList(),
            policy.MaxRatePercent.Value,
            policy.MinDepositAmount.Value,
            policy.Forfeiture.SellerPercent,
            policy.Forfeiture.PlatformPercent);
    }

    private static DomainException Invalid() =>
        new("DEPOSIT_POLICY_INVALID", "The deposit policy configuration is invalid.", 500);

    public sealed record PolicyJson(
        List<TierJson>? Tiers,
        decimal? NewUserRatePercent,
        List<SurchargeJson>? StreakSurcharges,
        decimal? MaxRatePercent,
        decimal? MinDepositAmount,
        ForfeitureJson? Forfeiture);

    public sealed record TierJson(int MinReputation, int MaxReputation, decimal RatePercent);

    public sealed record SurchargeJson(int Streak, decimal AdditionalPercent);

    public sealed record ForfeitureJson(decimal SellerPercent, decimal PlatformPercent);
}
