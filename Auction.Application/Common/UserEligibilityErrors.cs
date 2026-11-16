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
            "ERR_USER_NOT_FOUND" or "USER_NOT_FOUND" or "USER_DELETED" =>
                new DomainException("USER_NOT_FOUND", "The user was not found.", 404),
            "ERR_ACCOUNT_BANNED" or "ACCOUNT_BANNED" =>
                new DomainException("ACCOUNT_BANNED", "The account has been banned.", 403),
            "ERR_ACCOUNT_LOCKED_TEMPORARY" or "ACCOUNT_LOCKED_TEMPORARY" =>
                new DomainException("ACCOUNT_LOCKED_TEMPORARY", "The account is temporarily locked. Try again later.", 423),
            "ERR_ACCOUNT_UNVERIFIED" or "ACCOUNT_UNVERIFIED" or "ACCOUNT_NOT_VERIFIED" or "ACCOUNT_INACTIVE" or "PENDING_VERIFICATION" or "PENDINGVERIFICATION" =>
                new DomainException("ACCOUNT_NOT_VERIFIED", "The account has not been verified.", 403),
            "SELLER_NOT_APPROVED" =>
                new DomainException("SELLER_NOT_APPROVED", "The seller has not been approved.", 403),
            "ACCOUNT_RESTRICTED" =>
                new DomainException("ACCOUNT_RESTRICTED", $"The account is restricted and cannot {actionText}.", 403),
            "ERR_REPUTATION_TOO_LOW" or "REPUTATION_TOO_LOW" =>
                new DomainException("REPUTATION_TOO_LOW", $"The reputation score is too low to {actionText}.", 403),
            _ => new DomainException(fallbackCode, fallbackMessage, 403)
        };
}
