namespace Auction.Application.Common;

using System;
using global::Auction.Application.Interfaces;
using global::Auction.Domain.Exceptions;

/// <summary>Turns the facts of the User service into the errors that stop a user from taking part in an auction.</summary>
public static class DepositProfileGuard
{
    /// <summary>
    /// An unverified account, a banned account, a deleted account and a bidder under an auction restriction cannot join (deposit or bid).
    /// Reputation never blocks here: it only changes the size of the deposit.
    /// </summary>
    public static void EnsureCanJoin(UserAuctionDepositProfile profile)
    {
        switch (profile.AccountStatus.ToUpperInvariant())
        {
            case "PENDINGVERIFICATION":
            case "PENDING_VERIFICATION":
                throw new DomainException("ACCOUNT_NOT_VERIFIED", "The account has not been verified.", 403);
            case "BANNED":
                throw new DomainException("ACCOUNT_BANNED", "The account has been banned.", 403);
            case "DELETED":
                throw new DomainException("USER_NOT_FOUND", "The user was not found.", 404);
        }

        if (profile.IsRestricted)
        {
            var message = profile.RestrictedUntil is { } until
                ? $"The bidder is restricted from auctions until {until.UtcDateTime:yyyy-MM-dd HH:mm} UTC."
                : "The bidder is restricted from auctions.";
            throw new DomainException("BIDDER_RESTRICTED", message, 403);
        }
    }
}
