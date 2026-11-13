namespace Auction.Application.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Auction.Domain.Enums;

/// <summary>
/// Synchronous (REST) calls to the Nexus Wallet service for auction deposit holds (Direction C: W1–W5). Implementations throw a DomainException:
/// 503 WALLET_UNAVAILABLE when Wallet cannot be reached,
/// 422 INSUFFICIENT_AVAILABLE_BALANCE, WALLET_FROZEN or PAYER_NOT_ELIGIBLE for business refusals,
/// 404 HOLD_NOT_FOUND when a hold does not exist.
/// </summary>
public interface IWalletClient
{
    /// <summary>W1: Locks the deposit amount synchronously from the buyer's available Nexus Wallet balance. The same idempotency key always returns the same hold.</summary>
    Task<DepositHoldCreated> CreateDepositHoldAsync(
        CreateDepositHoldCommand command, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>W4: Reads the real state of a hold by holdId.</summary>
    Task<DepositHoldSnapshot> GetDepositHoldAsync(string holdId, CancellationToken cancellationToken = default);

    /// <summary>W4 (by-reference): Looks up a hold by deposit referenceId to reconcile after a timeout. Returns null when no hold exists yet.</summary>
    Task<DepositHoldSnapshot?> GetDepositHoldByReferenceAsync(Guid referenceId, CancellationToken cancellationToken = default);

    /// <summary>W3: Releases a hold so the buyer's locked balance returns to available balance. Repeating the same idempotency key is safe.</summary>
    Task<DepositReleaseOutcome> ReleaseDepositHoldAsync(
        string holdId, string reason, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>W2: Captures a hold and executes the pre-calculated transfers (order payment or forfeiture split).</summary>
    Task CaptureDepositHoldAsync(
        string holdId, CaptureDepositHoldCommand command, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>W5: Refunds a previously captured deposit hold back to the buyer's wallet when the seller rejects or lets the order expire.</summary>
    Task RefundCapturedHoldAsync(
        string holdId, decimal amount, string reason, string idempotencyKey, CancellationToken cancellationToken = default);
}

public sealed record CreateDepositHoldCommand(
    Guid DepositId,
    Guid PayerUserId,
    decimal Amount,
    string Currency);

public sealed record DepositHoldCreated(
    string HoldId,
    DepositHoldState Status,
    DateTimeOffset? AuthorizedAt);

public sealed record DepositHoldSnapshot(
    string HoldId,
    Guid? ReferenceId,
    DepositHoldState Status,
    string? Mode,
    decimal Amount,
    DateTimeOffset? AuthorizedAt,
    DateTimeOffset? ProviderHoldExpiresAt,
    string? FailureCode);

public sealed record HoldTransferInstruction(
    string DestinationType,
    Guid? DestinationUserId,
    decimal Amount,
    string Description);

public sealed record CaptureDepositHoldCommand(
    string Purpose,
    decimal TotalAmount,
    IReadOnlyList<HoldTransferInstruction> Transfers);

public enum DepositReleaseOutcome
{
    /// <summary>The hold was released or voided.</summary>
    Released,
    /// <summary>The hold does not exist or already expired by itself, so there is nothing left to release.</summary>
    AlreadyGone,
    /// <summary>The hold was already captured. It must not be released and an admin should look at it.</summary>
    AlreadyCaptured
}
