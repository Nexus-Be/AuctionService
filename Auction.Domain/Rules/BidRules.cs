namespace Auction.Domain.Rules;

using System.Globalization;
using Auction.Domain.Enums;
using Auction.Domain.Exceptions;

/// <summary>Stateless business rules for placing a bid.</summary>
public static class BidRules
{
    public const int IdempotencyKeyMaxLength = 255;

    /// <summary>The amount must be greater than zero and have at most two decimal places.</summary>
    public static void ValidateAmount(decimal? amount)
    {
        if (amount is null || amount <= 0m || amount != decimal.Round(amount.Value, 2))
            throw new DomainException(
                "VAL_INVALID_BID_AMOUNT", "Amount must be greater than zero and have at most two decimal places.", 400);
    }

    /// <summary>The Idempotency-Key header is required and must fit in the database column.</summary>
    public static void ValidateIdempotencyKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new DomainException("VAL_IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key header is required.", 400);
        if (idempotencyKey.Trim().Length > IdempotencyKeyMaxLength)
            throw new DomainException(
                "VAL_INVALID_IDEMPOTENCY_KEY", $"Idempotency-Key must be at most {IdempotencyKeyMaxLength} characters.", 400);
    }

    /// <summary>Reads the row version from the If-Match header. Quotes and the weak validator prefix are accepted.</summary>
    public static long ParseRowVersion(string? ifMatch)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
            throw new DomainException("VAL_IF_MATCH_REQUIRED", "If-Match header is required.", 400);

        var text = ifMatch.Trim();
        if (text.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        text = text.Trim('"');

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 0)
            throw new DomainException("VAL_INVALID_IF_MATCH", "If-Match must be the row version of the auction.", 400);

        return version;
    }

    /// <summary>Bids are accepted only while the auction is Running and its end time has not passed.</summary>
    public static void EnsureBiddable(AuctionStatus status, DateTimeOffset scheduledEndAt, DateTimeOffset now)
    {
        switch (status)
        {
            case AuctionStatus.Draft:
            case AuctionStatus.Scheduled:
                throw new BusinessRuleValidationException("AUCTION_NOT_RUNNING", "The auction has not started yet.", 422);

            case AuctionStatus.Running:
                if (now >= scheduledEndAt)
                    throw new DomainException("AUCTION_ENDED", "The auction has ended.", 410);
                return;

            default:
                throw new DomainException("AUCTION_ENDED", "The auction has ended.", 410);
        }
    }

    /// <summary>The first bid must reach the start price. Later bids must add at least one bid step.</summary>
    public static decimal MinNextBid(decimal startPriceAmount, decimal bidStepAmount, decimal? currentPriceAmount) =>
        currentPriceAmount is null ? startPriceAmount : currentPriceAmount.Value + bidStepAmount;

    public static void EnsureMeetsMinimum(decimal amount, decimal minNextBidAmount)
    {
        if (amount < minNextBidAmount)
            throw new BusinessRuleValidationException(
                "BID_TOO_LOW",
                $"Bid must be at least {minNextBidAmount.ToString("0.##", CultureInfo.InvariantCulture)}.",
                422);
    }
}
