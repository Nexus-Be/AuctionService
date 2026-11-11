namespace Auction.Domain.Rules;

using Auction.Domain.Enums;
using Auction.Domain.Exceptions;

/// <summary>
/// Stateless business rules for creating, editing and cancelling an auction.
/// The seller chooses the start price and the schedule. The bid step is optional: when the seller does not send it,
/// a default percent of the start price is used (the percent is set by the admin in the auction policy).
/// </summary>
public static class AuctionRules
{
    // Validation limits on the schedule (limits, not defaults).
    public const int MaxStartLeadDays = 30;
    public const int MinDurationMinutes = 60;
    public const int MaxDurationHours = 168;

    /// <summary>Default bid step as a percent of the start price. Used only when the policy has no value for it.</summary>
    public const decimal FallbackDefaultBidStepPercent = 10m;

    /// <summary>How long the next bidder has to answer a winner offer (business rule: 24 hours).</summary>
    public const int WinnerOfferWindowHours = 24;

    /// <summary>Both prices are required. Input problems return HTTP 400 with a VAL_ code.</summary>
    public static void ValidatePrices(decimal? startPriceAmount, decimal? bidStepAmount)
    {
        ValidateStartPrice(startPriceAmount);
        if (bidStepAmount is null)
            throw Invalid("VAL_INVALID_PRICE", "Bid step is required.");
        ValidateBidStep(bidStepAmount);
    }

    /// <summary>The start price is required, zero or greater, with at most two decimal places.</summary>
    public static void ValidateStartPrice(decimal? startPriceAmount)
    {
        if (startPriceAmount is null)
            throw Invalid("VAL_INVALID_PRICE", "Start price is required.");
        if (startPriceAmount < 0m || HasMoreThanTwoDecimals(startPriceAmount.Value))
            throw Invalid("VAL_INVALID_PRICE", "Start price must be zero or greater and have at most two decimal places.");
    }

    /// <summary>A bid step that is sent must be greater than zero, with at most two decimal places.</summary>
    public static void ValidateBidStep(decimal? bidStepAmount)
    {
        if (bidStepAmount is null)
            throw Invalid("VAL_INVALID_PRICE", "Bid step is required.");
        if (bidStepAmount <= 0m || HasMoreThanTwoDecimals(bidStepAmount.Value))
            throw Invalid("VAL_INVALID_PRICE", "Bid step must be greater than zero and have at most two decimal places.");
    }

    /// <summary>
    /// Returns the bid step of the auction. A step sent by the seller is validated and used as is.
    /// Without one, the step is the default percent of the start price, rounded to two decimals.
    /// A start price too small to give a step above zero (for example 0) needs the seller to send a step.
    /// </summary>
    public static decimal ResolveBidStep(decimal? startPriceAmount, decimal? bidStepAmount, decimal defaultPercent)
    {
        ValidateStartPrice(startPriceAmount);

        if (bidStepAmount is not null)
        {
            ValidateBidStep(bidStepAmount);
            return bidStepAmount.Value;
        }

        var step = decimal.Round(startPriceAmount!.Value * defaultPercent / 100m, 2, MidpointRounding.AwayFromZero);
        if (step <= 0m)
            throw Invalid(
                "VAL_INVALID_PRICE",
                "Bid step is required because the start price is too small to calculate a default bid step.");

        return step;
    }

    /// <summary>
    /// Start and end are required. The start must be in the future and at most 30 days ahead,
    /// the end must be after the start, and the duration must be between 60 minutes and 168 hours.
    /// </summary>
    public static void ValidateSchedule(DateTimeOffset? scheduledStartAt, DateTimeOffset? scheduledEndAt, DateTimeOffset now)
    {
        if (scheduledStartAt is null)
            throw Invalid("VAL_INVALID_SCHEDULE", "Scheduled start time is required.");
        if (scheduledEndAt is null)
            throw Invalid("VAL_INVALID_SCHEDULE", "Scheduled end time is required.");

        var start = scheduledStartAt.Value;
        var end = scheduledEndAt.Value;

        if (start <= now)
            throw Invalid("VAL_INVALID_SCHEDULE", "Scheduled start time must be in the future.");
        if (end <= start)
            throw Invalid("VAL_INVALID_SCHEDULE", "Scheduled end time must be after the scheduled start time.");

        if (start > now.AddDays(MaxStartLeadDays))
            throw new BusinessRuleValidationException(
                "INVALID_SCHEDULE", $"Scheduled start time must be within {MaxStartLeadDays} days from now.", 422);

        var duration = end - start;
        if (duration < TimeSpan.FromMinutes(MinDurationMinutes) || duration > TimeSpan.FromHours(MaxDurationHours))
            throw new BusinessRuleValidationException(
                "INVALID_SCHEDULE", $"Auction duration must be between {MinDurationMinutes} minutes and {MaxDurationHours} hours.", 422);
    }

    /// <summary>
    /// A seller can edit or cancel an auction only before it starts: the status must be Scheduled
    /// and the start time must not have been reached yet.
    /// </summary>
    public static void EnsureNotStarted(
        AuctionStatus status, DateTimeOffset scheduledStartAt, DateTimeOffset now,
        string errorCode, string message, int statusCode)
    {
        if (status != AuctionStatus.Scheduled || now >= scheduledStartAt)
            throw new BusinessRuleValidationException(errorCode, message, statusCode);
    }

    private static DomainException Invalid(string errorCode, string message) => new(errorCode, message, 400);

    private static bool HasMoreThanTwoDecimals(decimal value) => value != decimal.Round(value, 2);
}