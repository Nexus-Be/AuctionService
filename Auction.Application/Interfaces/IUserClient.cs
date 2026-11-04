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

    /// <summary>Gets the public (already masked) names of users. Unknown users are missing from the result.</summary>
    Task<IReadOnlyDictionary<Guid, UserPublicInfo>> GetPublicInfosAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);
}

/// <summary>FailureReason is the code sent by User (for example ERR_REPUTATION_TOO_LOW). Null when allowed.</summary>
public sealed record UserReputationCheck(
    Guid UserId,
    bool Allowed,
    bool IsApprovedSeller,
    string? UserStatus,
    string? FailureReason);

public sealed record UserBidEligibility(Guid UserId, bool Eligible, string? FailureReason);

/// <summary>MaskedName is shown as is. User masks it, Auction never sees the real name.</summary>
public sealed record UserPublicInfo(Guid UserId, string MaskedName, bool IsDeleted);
