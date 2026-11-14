namespace Auction.Application.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Synchronous (REST) calls to the User service. Implementations throw a DomainException with
/// status 503 when User cannot be reached and 502 when it returns data that is not usable.
/// </summary>
public interface IUserClient
{
    /// <summary>Asks User whether the user is an approved seller with enough reputation to create an auction.</summary>
    Task<UserReputationCheck> CheckSellerReputationAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Asks User whether the user can place a bid. Short timeout: this is on the bidding hot path.</summary>
    Task<UserBidEligibility> VerifyBidEligibilityAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the facts Auction needs to price a deposit and to block a restricted bidder (reputation, violation streak, auction restriction).
    /// The answer is cached for a short time per user. Pass bypassCache when the answer must be fresh (creating a deposit).
    /// Throws USER_NOT_FOUND (404) for an unknown user and USER_SERVICE_UNAVAILABLE (503) when User cannot be reached and no cached answer exists.
    /// </summary>
    Task<UserAuctionDepositProfile> GetAuctionDepositProfileAsync(
        Guid userId, bool bypassCache = false, CancellationToken cancellationToken = default);

    /// <summary>Gets the public (already masked) names of users. Unknown users are missing from the result.</summary>
    Task<IReadOnlyDictionary<Guid, UserPublicInfo>> GetPublicInfosAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an auction non-payment violation in the User service (API 20: POST /api/v1/internal/users/{userId}/auction-violations).
    /// Idempotent on (UserId, AuctionId, ViolationType). Evicts the cached deposit profile for the user.
    /// </summary>
    Task<RecordAuctionViolationResult> RecordAuctionViolationAsync(
        RecordAuctionViolationCommand command, CancellationToken cancellationToken = default);
}

/// <summary>FailureReason is the code sent by User (for example ERR_REPUTATION_TOO_LOW). Null when allowed.</summary>
public sealed record UserReputationCheck(
    Guid UserId,
    bool Allowed,
    bool IsApprovedSeller,
    string? UserStatus,
    string? FailureReason);

public sealed record UserBidEligibility(Guid UserId, bool Eligible, string? FailureReason);

/// <summary>
/// AccountStatus is PENDINGVERIFICATION, ACTIVE, BANNED or DELETED. ReputationScore is null for a user without history.
/// RestrictionType is TEMP_RESTRICTED or SUSPENDED when IsRestricted is true. The restriction only affects auctions.
/// </summary>
public sealed record UserAuctionDepositProfile(
    Guid UserId,
    string AccountStatus,
    int? ReputationScore,
    bool IsNewUser,
    int ViolationStreak,
    bool IsRestricted,
    string? RestrictionType,
    DateTimeOffset? RestrictedUntil,
    string? RestrictionReason);

/// <summary>MaskedName is shown as is. User masks it, Auction never sees the real name.</summary>
public sealed record UserPublicInfo(Guid UserId, string MaskedName, bool IsDeleted);

/// <summary>ViolationType is "WinnerNonPayment" or "SecondChanceNonPayment" (DepositForfeitedAmount must be 0 for SecondChanceNonPayment).</summary>
public sealed record RecordAuctionViolationCommand(
    Guid UserId,
    string ViolationType,
    Guid AuctionId,
    Guid OrderId,
    DateTimeOffset OccurredAt,
    decimal DepositForfeitedAmount);

public sealed record RecordAuctionViolationResult(
    Guid ViolationId,
    bool Created,
    int PreviousStreak,
    int NewStreak,
    int ReputationBefore,
    int ReputationAfter,
    string? SanctionType,
    DateTimeOffset? SanctionUntil);