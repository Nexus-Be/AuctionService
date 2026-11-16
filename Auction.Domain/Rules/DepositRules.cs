namespace Auction.Domain.Rules;

using Auction.Domain.Enums;
using Auction.Domain.Exceptions;

/// <summary>Một mức tỷ lệ cọc theo khoảng điểm uy tín (gồm cả hai đầu).</summary>
public sealed record DepositTier(int MinReputation, int MaxReputation, decimal RatePercent);

/// <summary>Phụ thu khi Buyer có từ Streak vi phạm trở lên. Chọn mức có Streak lớn nhất không vượt Streak của Buyer.</summary>
public sealed record StreakSurcharge(int Streak, decimal AdditionalPercent);

/// <summary>Chính sách cọc hiện hành. Version 0 là chính sách mặc định dựng sẵn khi Admin chưa cấu hình.</summary>
public sealed record DepositPolicy(
    int Version,
    IReadOnlyList<DepositTier> Tiers,
    decimal NewUserRatePercent,
    IReadOnlyList<StreakSurcharge> StreakSurcharges,
    decimal MaxRatePercent,
    decimal MinDepositAmount,
    decimal SellerPercent,
    decimal PlatformPercent);

/// <summary>Kết quả tính cọc. RatePercent là tỷ lệ sau khi áp trần MaxRatePercent.</summary>
public sealed record DepositQuote(decimal Amount, decimal RatePercent, bool MinDepositApplied);

/// <summary>Stateless business rules for auction deposits.</summary>
public static class DepositRules
{
    public const string Currency = "VND";

    /// <summary>Chính sách dùng khi chưa có cấu hình DEPOSIT_POLICY. Giá trị lấy từ ví dụ trong tài liệu API.</summary>
    public static DepositPolicy DefaultPolicy { get; } = new(
        Version: 0,
        Tiers: new[]
        {
            new DepositTier(80, 100, 5m),
            new DepositTier(60, 79, 10m),
            new DepositTier(40, 59, 15m),
            new DepositTier(0, 39, 20m)
        },
        NewUserRatePercent: 20m,
        StreakSurcharges: new[] { new StreakSurcharge(2, 10m) },
        MaxRatePercent: 30m,
        MinDepositAmount: 50000m,
        SellerPercent: 70m,
        PlatformPercent: 30m);

    /// <summary>
    /// Kiểm tra tính hợp lệ của chính sách cọc trước khi lưu:
    /// - reason bắt buộc (ít nhất 5 ký tự)
    /// - tiers phủ kín [0, 100], không hở, không chồng chéo, ratePercent trong [0, maxRatePercent]
    /// - maxRatePercent trong [0, 100]
    /// - newUserRatePercent trong [0, maxRatePercent]
    /// - sellerPercent + platformPercent == 100
    /// - minDepositAmount >= 0
    /// </summary>
    public static void ValidatePolicy(
        string? reason,
        IReadOnlyList<DepositTier>? tiers,
        decimal? newUserRatePercent,
        IReadOnlyList<StreakSurcharge>? streakSurcharges,
        decimal? maxRatePercent,
        decimal? minDepositAmount,
        decimal? sellerPercent,
        decimal? platformPercent)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
            throw new DomainException("VALIDATION_ERROR", "Reason is required and must be at least 5 characters.", 400);

        if (maxRatePercent is null or < 0m or > 100m)
            throw new DomainException("VALIDATION_ERROR", "Max rate percent must be between 0 and 100.", 400);

        if (newUserRatePercent is null or < 0m || newUserRatePercent > maxRatePercent)
            throw new DomainException("VALIDATION_ERROR", "New user rate percent must be between 0 and max rate percent.", 400);

        if (minDepositAmount is null or < 0m)
            throw new DomainException("VALIDATION_ERROR", "Minimum deposit amount must not be negative.", 400);

        if (sellerPercent is null || platformPercent is null || (sellerPercent.Value + platformPercent.Value != 100m) ||
            sellerPercent < 0m || platformPercent < 0m)
        {
            throw new DomainException("FORFEITURE_SPLIT_NOT_100", "The sum of seller and platform forfeiture percentages must equal 100.", 400);
        }

        if (tiers is null || tiers.Count == 0)
            throw new DomainException("POLICY_TIERS_INVALID", "Tiers must cover reputation 0 to 100 without gaps or overlaps.", 400);

        var sortedTiers = tiers.OrderBy(t => t.MinReputation).ToList();
        var currentExpected = 0;
        foreach (var tier in sortedTiers)
        {
            if (tier.MinReputation != currentExpected || tier.MaxReputation < tier.MinReputation)
                throw new DomainException("POLICY_TIERS_INVALID", "Tiers must cover reputation 0 to 100 without gaps or overlaps.", 400);

            if (tier.RatePercent < 0m || tier.RatePercent > maxRatePercent.Value)
                throw new DomainException("POLICY_TIERS_INVALID", "Each tier rate percent must be between 0 and max rate percent.", 400);

            currentExpected = tier.MaxReputation + 1;
        }

        if (currentExpected != 101)
            throw new DomainException("POLICY_TIERS_INVALID", "Tiers must cover reputation 0 to 100 without gaps or overlaps.", 400);

        if (streakSurcharges is not null)
        {
            foreach (var surcharge in streakSurcharges)
            {
                if (surcharge.Streak < 1 || surcharge.AdditionalPercent < 0m)
                    throw new DomainException("VALIDATION_ERROR", "Streak surcharge must have streak >= 1 and additional percent >= 0.", 400);
            }
        }
    }

    /// <summary>
    /// rate = tier(uy tín) hoặc tỷ lệ người mới, cộng phụ thu theo Streak, tối đa MaxRatePercent.
    /// amount = max(ceil(giá khởi điểm × rate / 100), mức cọc tối thiểu), rồi không vượt giá khởi điểm.
    /// Người chưa có điểm uy tín, hoặc điểm nằm ngoài mọi tier, dùng tỷ lệ của người mới.
    /// </summary>
    public static DepositQuote Calculate(DepositPolicy policy, decimal startPriceAmount, int? reputationScore, bool isNewUser, int violationStreak)
    {
        decimal baseRate;
        if (isNewUser || reputationScore is null)
        {
            baseRate = policy.NewUserRatePercent;
        }
        else
        {
            var tier = policy.Tiers.FirstOrDefault(t => reputationScore >= t.MinReputation && reputationScore <= t.MaxReputation);
            baseRate = tier?.RatePercent ?? policy.NewUserRatePercent;
        }

        var surcharge = policy.StreakSurcharges
            .Where(s => s.Streak <= violationStreak)
            .OrderByDescending(s => s.Streak)
            .FirstOrDefault()?.AdditionalPercent ?? 0m;

        var rate = Math.Min(baseRate + surcharge, policy.MaxRatePercent);
        var raw = Math.Ceiling(startPriceAmount * rate / 100m);

        var minApplied = raw < policy.MinDepositAmount;
        var amount = Math.Min(minApplied ? policy.MinDepositAmount : raw, startPriceAmount);
        return new DepositQuote(amount, rate, minApplied);
    }

    /// <summary>Chỉ cho đóng cọc khi phiên Scheduled, hoặc Running và chưa tới giờ kết thúc.</summary>
    public static bool IsOpenForDeposit(AuctionStatus status, DateTimeOffset scheduledEndAt, DateTimeOffset now) =>
        status switch
        {
            AuctionStatus.Scheduled => true,
            AuctionStatus.Running => now < scheduledEndAt,
            _ => false
        };
}
