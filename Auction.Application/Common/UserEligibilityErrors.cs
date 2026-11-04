namespace Auction.Application.Common;

using global::Auction.Domain.Exceptions;

/// <summary>
/// Turns the failure codes of the User service into Auction error codes.
/// Auction error codes never start with ERR_ and never leak the code of another service.
/// </summary>
public static class UserEligibilityErrors
{
    public static DomainException FromFailureReason(
        string? failureReason, string actionText, string fallbackCode, string fallbackMessage) =>
        failureReason switch
        {
            "ERR_USER_NOT_FOUND" => new DomainException("USER_NOT_FOUND", "The user was not found.", 404),
            "ERR_ACCOUNT_BANNED" => new DomainException("ACCOUNT_BANNED", "The account has been banned.", 403),
            "ERR_ACCOUNT_LOCKED_TEMPORARY" => new DomainException(
                "ACCOUNT_LOCKED_TEMPORARY", "The account is temporarily locked. Try again later.", 423),
            "ERR_ACCOUNT_UNVERIFIED" => new DomainException("ACCOUNT_UNVERIFIED", "The account has not been verified.", 403),
            "ERR_REPUTATION_TOO_LOW" => new DomainException(
                "REPUTATION_TOO_LOW", $"The reputation score is too low to {actionText}.", 403),
            _ => new DomainException(fallbackCode, fallbackMessage, 403)
        };
}
