namespace Auction.Application.Services.Deposit;

using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using global::Auction.Application.Common;
using global::Auction.Application.Common.Extensions;
using global::Auction.Application.Common.Models;
using global::Auction.Application.Interfaces;
using global::Auction.Domain.Entities;
using global::Auction.Domain.Enums;
using global::Auction.Domain.Exceptions;
using global::Auction.Domain.Rules;
using AuctionEntity = global::Auction.Domain.Entities.Auction;

public sealed class DepositService : IDepositService
{
    private static readonly TimeSpan IdempotencyLifetime = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAuctionDbContext _dbContext;
    private readonly IUserClient _users;
    private readonly IWalletClient _wallet;
    private readonly INotificationClient _notifications;
    private readonly DepositOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<DepositService> _logger;

    public DepositService(
        IAuctionDbContext dbContext, IUserClient users, IWalletClient wallet, INotificationClient notifications,
        DepositOptions options, TimeProvider clock, ILogger<DepositService> logger)
    {
        _dbContext = dbContext;
        _users = users;
        _wallet = wallet;
        _notifications = notifications;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    // ------------------------------------------------------------------ 01: deposit requirement

    /// <summary>
    /// Tính số cọc cho Buyer: tỷ lệ theo điểm uy tín (hoặc tỷ lệ người mới) cộng phụ thu Streak, có mức tối thiểu và không vượt giá khởi điểm.
    /// Phiên phải Scheduled hoặc Running; seller của phiên, tài khoản bị ban và bidder bị hạn chế đều bị từ chối.
    /// User service không trả lời được (và không có cache) thì từ chối thay vì đoán số cọc.
    /// </summary>
    public async Task<DepositRequirementResponse> GetRequirementAsync(
        ActorContext actor, Guid auctionId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var auction = await LoadAuctionForDepositAsync(auctionId, actor.UserId, now, cancellationToken);

        var profile = await _users.GetAuctionDepositProfileAsync(actor.UserId, bypassCache: false, cancellationToken);
        DepositProfileGuard.EnsureCanJoin(profile);

        var policy = await DepositPolicyReader.ReadAsync(_dbContext, cancellationToken);
        var quote = DepositRules.Calculate(
            policy, auction.StartPriceAmount, profile.ReputationScore, profile.IsNewUser, profile.ViolationStreak);

        var existing = await _dbContext.AuctionDeposits
            .AsNoTracking()
            .Where(d => d.AuctionId == auctionId && d.BidderId == actor.UserId && !d.IsDeleted)
            .Select(d => new { d.Id, d.Status })
            .FirstOrDefaultAsync(cancellationToken);

        var canJoin = existing is null ||
                      existing.Status is DepositStatus.Pending or DepositStatus.Failed or DepositStatus.Expired;

        return new DepositRequirementResponse(
            auctionId,
            DepositRules.Currency,
            auction.StartPriceAmount,
            quote.Amount,
            quote.RatePercent,
            quote.MinDepositApplied,
            policy.Version,
            new BuyerStandingDto(profile.ReputationScore, profile.IsNewUser, profile.ViolationStreak),
            new ForfeiturePolicyDto(policy.SellerPercent, policy.PlatformPercent),
            canJoin,
            existing is null ? null : new ExistingDepositDto(existing.Id, existing.Status));
    }

    // ------------------------------------------------------------------ 02: create deposit

    /// <summary>
    /// Tạo yêu cầu đóng cọc bằng cách khóa đồng bộ số dư khả dụng trong ví Nexus (Hướng C).
    /// Tính lại số cọc và so với số Buyer đã xác nhận (lệch thì 409 DEPOSIT_TERMS_CHANGED), mỗi Buyer chỉ có một cọc mỗi phiên.
    /// Cọc đã Authorized hoặc đang Pending (do lần gọi trước timeout) thì trả lại bản ghi cũ (200 OK) mà không khóa hai lần.
    /// Cọc Failed/Expired được dùng lại với AttemptCount + 1.
    /// </summary>
    public async Task<CreateDepositResult> CreateAsync(
        ActorContext actor, Guid auctionId, string? idempotencyKey, CreateDepositRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        // 1. Input checks (no database, no other service).
        if (auctionId == Guid.Empty)
            throw new DomainException("VAL_INVALID_IDENTIFIER", "Auction ID is required.", 400);
        BidRules.ValidateIdempotencyKey(idempotencyKey);
        ValidateCreateRequest(request);

        var acceptedVersion = request.AcceptedPolicyVersion!.Value;
        var acceptedAmount = request.AcceptedDepositAmount!.Value;
        var key = idempotencyKey!.Trim();
        var requestHash = HashRequest(auctionId, actor.UserId, acceptedVersion, acceptedAmount);

        // 2. Same key again: return the first result, or refuse when the request is different.
        var saved = await _dbContext.IdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == key && k.Scope == IdempotencyScope.Deposit && k.ExpiresAt > now, cancellationToken);
        if (saved is not null)
        {
            if (saved.RequestHash != requestHash)
                throw new BusinessRuleValidationException(
                    "IDEMPOTENCY_KEY_REUSED", "The Idempotency-Key was already used with a different request.", 422);

            var body = DeserializeResponse(saved.ResponseBody);
            if (body is not null)
                return new CreateDepositResult(body, saved.ResponseStatus ?? HttpCreated);
        }

        // 3. The auction must be open for deposits and the bidder must be allowed to join.
        var auction = await LoadAuctionForDepositAsync(auctionId, actor.UserId, now, cancellationToken);

        // Fresh facts: the deposit amount depends on reputation and streak, so the cache is skipped here.
        var profile = await _users.GetAuctionDepositProfileAsync(actor.UserId, bypassCache: true, cancellationToken);
        DepositProfileGuard.EnsureCanJoin(profile);

        // 4. One deposit per bidder and auction. An existing Authorized or Pending deposit is returned without locking twice.
        var deposit = await _dbContext.AuctionDeposits
            .FirstOrDefaultAsync(d => d.AuctionId == auctionId && d.BidderId == actor.UserId && !d.IsDeleted, cancellationToken);

        if (deposit is { Status: DepositStatus.Authorized })
            return new CreateDepositResult(ToCreateResponse(deposit), HttpOk);

        if (deposit is { Status: DepositStatus.Released or DepositStatus.Captured })
            throw new DomainException("DEPOSIT_CLOSED", "The deposit for this auction is already closed.", 409);

        // The old hold is still being released: its holdId must not be overwritten before that is done.
        if (deposit is { ReleasePending: true })
            throw new DomainException(
                "DEPOSIT_RELEASE_PENDING", "The previous payment hold is still being released. Try again shortly.", 409);

        if (deposit is { Status: DepositStatus.Pending })
        {
            var existingHold = await _wallet.GetDepositHoldByReferenceAsync(deposit.Id, cancellationToken);
            if (existingHold is not null)
            {
                // A hold that was already released, captured or refunded no longer locks any money, so it must not unlock bidding.
                if (existingHold.Status != DepositHoldState.Authorized)
                    throw new DomainException(
                        "DEPOSIT_HOLD_NOT_ACTIVE", "The payment hold for this deposit is no longer active.", 409);

                deposit.GatewayReference = existingHold.HoldId;
                deposit.Status = DepositStatus.Authorized;
                deposit.AuthorizedAt = existingHold.AuthorizedAt ?? now;
                deposit.ExpiresAt = null;
                deposit.FailureCode = null;
                deposit.UpdatedAt = now;
                deposit.RowVersion += 1;

                _dbContext.AddAuditLog(
                    AuditAction.DepositAuthorize, "AuctionDeposit", deposit.Id.ToString(),
                    oldValues: new { status = DepositStatus.Pending },
                    newValues: new
                    {
                        depositId = deposit.Id,
                        status = deposit.Status,
                        holdId = existingHold.HoldId,
                        authorizedAt = deposit.AuthorizedAt
                    },
                    userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

                await SaveAsync(cancellationToken);
                return new CreateDepositResult(ToCreateResponse(deposit), HttpOk);
            }
        }

        // 5. The buyer confirmed these terms: they must still be the current ones.
        var policy = await DepositPolicyReader.ReadAsync(_dbContext, cancellationToken);
        var quote = DepositRules.Calculate(
            policy, auction.StartPriceAmount, profile.ReputationScore, profile.IsNewUser, profile.ViolationStreak);
        if (policy.Version != acceptedVersion || quote.Amount != acceptedAmount)
            throw new DomainException(
                "DEPOSIT_TERMS_CHANGED",
                "The deposit terms have changed. Reload the deposit requirement and confirm again.", 409);

        // 6. Create the record (or reuse a Pending/Failed/Expired record) and save it as Pending before calling Wallet.
        object? oldValues = null;
        if (deposit is null)
        {
            deposit = new AuctionDeposit
            {
                Id = Guid.NewGuid(),
                AuctionId = auctionId,
                BidderId = actor.UserId,
                Amount = quote.Amount,
                RatePercent = quote.RatePercent,
                PolicyVersion = policy.Version,
                Status = DepositStatus.Pending,
                AttemptCount = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.AuctionDeposits.Add(deposit);
        }
        else
        {
            var isPendingRetry = deposit.Status == DepositStatus.Pending;
            oldValues = new { status = deposit.Status, attemptCount = deposit.AttemptCount, amount = deposit.Amount };

            // Keep AttemptCount unchanged when retrying a Pending deposit so W1 receives the same Idempotency-Key.
            if (!isPendingRetry)
                deposit.AttemptCount += 1;
            deposit.Status = DepositStatus.Pending;
            deposit.Amount = quote.Amount;
            deposit.RatePercent = quote.RatePercent;
            deposit.PolicyVersion = policy.Version;
            deposit.GatewayReference = null;
            deposit.GatewayMode = null;
            deposit.PaymentRedirectUrl = null;
            deposit.PaymentExpiresAt = null;
            deposit.AuthorizedAt = null;
            deposit.ExpiresAt = null;
            deposit.FailureCode = null;
            deposit.UpdatedAt = now;
            deposit.RowVersion += 1;
        }

        _dbContext.AddAuditLog(
            AuditAction.DepositCreate, "AuctionDeposit", deposit.Id.ToString(),
            oldValues: oldValues,
            newValues: new
            {
                auctionId,
                depositId = deposit.Id,
                amount = deposit.Amount,
                ratePercent = deposit.RatePercent,
                policyVersion = deposit.PolicyVersion,
                attempt = deposit.AttemptCount
            },
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);
        await SaveAsync(cancellationToken);

        // 7. Ask Wallet to lock the balance synchronously (W1). A 422 business refusal marks the attempt Failed; timeout/503 leaves it Pending.
        DepositHoldCreated hold;
        try
        {
            hold = await _wallet.CreateDepositHoldAsync(
                new CreateDepositHoldCommand(
                    deposit.Id, actor.UserId, deposit.Amount, DepositRules.Currency, auctionId, deposit.PolicyVersion),
                $"deposit:{deposit.Id}:{deposit.AttemptCount}",
                cancellationToken);
        }
        catch (DomainException ex) when (ex.StatusCode == 422 || ex.ErrorCode == "DEPOSIT_HOLD_NOT_ACTIVE")
        {
            // A refusal locked nothing (422), and a closed reference can never lock again (409): both end this attempt.
            deposit.Status = DepositStatus.Failed;
            deposit.FailureCode = ex.ErrorCode;
            deposit.UpdatedAt = now;
            deposit.RowVersion += 1;
            _dbContext.AddAuditLog(
                AuditAction.DepositFail, "AuctionDeposit", deposit.Id.ToString(),
                newValues: new { depositId = deposit.Id, failureCode = ex.ErrorCode },
                userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);
            await SaveAsync(cancellationToken);
            throw;
        }

        if (hold.Status == DepositHoldState.PendingAuthorization)
        {
            // Not locked yet: keep the deposit Pending so the reconcile job authorizes or fails it by reference.
            deposit.GatewayReference = hold.HoldId;
            deposit.UpdatedAt = now;
            deposit.RowVersion += 1;
            await SaveAsync(cancellationToken);
            throw new DomainException(
                "WALLET_UNAVAILABLE", "The deposit hold is still being confirmed. Check again shortly.", 503);
        }

        if (hold.Status != DepositHoldState.Authorized)
        {
            // Captured, Released or Refunded: the hold is closed and holds no money, so nothing needs to be released.
            deposit.Status = DepositStatus.Failed;
            deposit.FailureCode = "HOLD_NOT_ACTIVE";
            deposit.GatewayReference = hold.HoldId;
            deposit.UpdatedAt = now;
            deposit.RowVersion += 1;
            _dbContext.AddAuditLog(
                AuditAction.DepositFail, "AuctionDeposit", deposit.Id.ToString(),
                newValues: new { depositId = deposit.Id, failureCode = deposit.FailureCode, holdStatus = hold.Status.ToString() },
                userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);
            await SaveAsync(cancellationToken);
            throw new DomainException(
                "DEPOSIT_HOLD_NOT_ACTIVE", "The payment hold for this deposit is no longer active.", 409);
        }

        // 8. Synchronous lock succeeded: immediately transition deposit to Authorized and return 201 Created.
        deposit.GatewayReference = hold.HoldId;
        deposit.Status = DepositStatus.Authorized;
        deposit.AuthorizedAt = hold.AuthorizedAt ?? now;
        deposit.ExpiresAt = null;
        deposit.FailureCode = null;
        deposit.UpdatedAt = now;
        deposit.RowVersion += 1;

        _dbContext.AddAuditLog(
            AuditAction.DepositAuthorize, "AuctionDeposit", deposit.Id.ToString(),
            oldValues: new { status = DepositStatus.Pending },
            newValues: new
            {
                depositId = deposit.Id,
                status = deposit.Status,
                holdId = hold.HoldId,
                authorizedAt = deposit.AuthorizedAt
            },
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        await QueueDepositNotificationAsync(
            deposit, "Authorized", "DEPOSIT_HELD", deposit.AuthorizedAt ?? now, actor.TraceId, cancellationToken);

        var response = ToCreateResponse(deposit);
        _dbContext.IdempotencyKeys.Add(new IdempotencyKey
        {
            Id = Guid.NewGuid(),
            Key = key,
            Scope = IdempotencyScope.Deposit,
            RequestHash = requestHash,
            ResponseStatus = HttpCreated,
            ResponseBody = JsonSerializer.Serialize(response, JsonOptions),
            ExpiresAt = now.Add(IdempotencyLifetime),
            CreatedAt = now,
            UpdatedAt = now
        });
        await SaveAsync(cancellationToken);

        return new CreateDepositResult(response, HttpCreated);
    }

    // ------------------------------------------------------------------ 04: authorization result (callback from Wallet)

    /// <summary>
    /// Nhận callback kết quả Authorization. Callback không được tin trực tiếp (route này không bắt buộc khoá nội bộ): Auction hỏi lại Wallet trạng thái thật
    /// của holdId rồi mới đổi trạng thái, nên kẻ giả mạo không thể biến cọc thành Authorized. Wallet không trả lời được thì trả 503 để Wallet gửi lại cùng EventId.
    /// EventId đã xử lý thì trả 200 với applied = false. Cọc Authorized đến muộn (phiên đã kết thúc) hoặc lệch số tiền được đặt lại và giải phóng hold.
    /// </summary>
    public async Task<DepositAuthorizationResultResponse> ApplyAuthorizationResultAsync(
        Guid depositId, DepositAuthorizationResultRequest request, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        if (depositId == Guid.Empty)
            throw new DomainException("VAL_INVALID_IDENTIFIER", "Deposit ID is required.", 400);
        if (string.IsNullOrWhiteSpace(request.EventId) || request.EventId.Trim().Length > 200)
            throw new DomainException("VAL_INVALID_INPUT", "Event ID is required and must be at most 200 characters.", 400);
        if (string.IsNullOrWhiteSpace(request.HoldId))
            throw new DomainException("VAL_INVALID_INPUT", "Hold ID is required.", 400);
        if (request.Result is null)
            throw new DomainException("VAL_INVALID_INPUT", "Result is required.", 400);

        var eventId = request.EventId.Trim();
        var holdId = request.HoldId.Trim();

        var deposit = await _dbContext.AuctionDeposits
            .FirstOrDefaultAsync(d => d.Id == depositId && !d.IsDeleted, cancellationToken)
            ?? throw new EntityNotFoundException("DEPOSIT_NOT_FOUND", "Deposit", depositId);

        // Duplicate event: nothing to do, and a 200 stops Wallet from retrying forever.
        var alreadyProcessed = await _dbContext.DepositCallbackEvents
            .AsNoTracking()
            .AnyAsync(e => e.EventId == eventId && !e.IsDeleted, cancellationToken);
        if (alreadyProcessed)
            return new DepositAuthorizationResultResponse(deposit.Id, deposit.Status, false);

        if (deposit.GatewayReference is null || !string.Equals(deposit.GatewayReference, holdId, StringComparison.Ordinal))
            throw new BusinessRuleValidationException("HOLD_ID_MISMATCH", "The hold ID does not match the deposit.", 422);

        // The real state of the hold comes from Wallet, never from this request body.
        DepositHoldSnapshot snapshot;
        try
        {
            snapshot = await _wallet.GetDepositHoldAsync(holdId, cancellationToken);
        }
        catch (DomainException ex) when (ex.ErrorCode == "HOLD_NOT_FOUND")
        {
            throw new BusinessRuleValidationException("HOLD_ID_MISMATCH", "The hold ID does not match any known hold.", 422);
        }

        if (snapshot.ReferenceId is { } referenceId && referenceId != deposit.Id)
            throw new BusinessRuleValidationException("HOLD_ID_MISMATCH", "The hold ID does not match the deposit.", 422);

        if (MapToResult(snapshot.Status) != request.Result)
            _logger.LogWarning(
                "Deposit callback {EventId} says {Reported} but Wallet reports {Actual} for hold {HoldId}. Wallet is trusted.",
                eventId, request.Result, snapshot.Status, holdId);

        var auctionState = await _dbContext.AuctionStates
            .AsNoTracking()
            .Where(s => s.AuctionId == deposit.AuctionId && !s.IsDeleted)
            .Select(s => new { s.Status, s.ScheduledEndAt })
            .FirstOrDefaultAsync(cancellationToken);
        var auctionOpen = auctionState is not null &&
                          DepositRules.IsOpenForDeposit(auctionState.Status, auctionState.ScheduledEndAt, now);

        var oldStatus = deposit.Status;
        var applied = ApplyHoldState(deposit, snapshot, auctionOpen, now, out var action);

        _dbContext.DepositCallbackEvents.Add(new DepositCallbackEvent
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            DepositId = deposit.Id,
            Result = request.Result.ToString(),
            Applied = applied,
            ProcessedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        if (applied)
        {
            deposit.LastGatewayEventId = eventId;
            deposit.UpdatedAt = now;
            deposit.RowVersion += 1;
            _dbContext.AddAuditLog(
                action, "AuctionDeposit", deposit.Id.ToString(),
                oldValues: new { status = oldStatus },
                newValues: new
                {
                    depositId = deposit.Id,
                    status = deposit.Status,
                    holdId,
                    eventId,
                    failureCode = deposit.FailureCode,
                    releaseReason = deposit.ReleaseReason
                });
        }

        // A parallel callback with the same EventId, or on the same deposit, was saved first: Wallet will send it again.
        await SaveAsync(cancellationToken);

        if (applied && deposit.ReleasePending)
            await TryReleaseAsync(deposit, cancellationToken);

        return new DepositAuthorizationResultResponse(deposit.Id, deposit.Status, applied);
    }

    // ------------------------------------------------------------------ 05 (support): deposit check when bidding

    /// <summary>
    /// Có cọc Authorized trong DB của Auction thì mới được bid (Hướng C).
    /// Chưa có cọc hoặc cọc không ở trạng thái Authorized thì trả 403 DEPOSIT_REQUIRED.
    /// Không còn kiểm tra ExpiresAt hay gọi Wallet xác minh khi đặt giá.
    /// </summary>
    public async Task EnsureBidderDepositValidAsync(
        Guid auctionId, Guid bidderId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var deposit = await _dbContext.AuctionDeposits
            .AsNoTracking()
            .Where(d => d.AuctionId == auctionId && d.BidderId == bidderId && !d.IsDeleted)
            .Select(d => new { d.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (deposit is null || deposit.Status != DepositStatus.Authorized)
            throw DepositRequired();
    }

    // ------------------------------------------------------------------ Helpers

    private const int HttpOk = 200;
    private const int HttpCreated = 201;

    /// <summary>Loads the auction and checks that this user may deposit on it: it exists, the user is not its seller, and it is open.</summary>
    private async Task<AuctionEntity> LoadAuctionForDepositAsync(
        Guid auctionId, Guid bidderId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (auctionId == Guid.Empty)
            throw new DomainException("VAL_INVALID_IDENTIFIER", "Auction ID is required.", 400);

        var auction = await _dbContext.Auctions
            .AsNoTracking()
            .Include(a => a.AuctionState)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (auction?.AuctionState is null)
            throw new EntityNotFoundException("AUCTION_NOT_FOUND", "Auction", auctionId);

        if (auction.SellerId == bidderId)
            throw new DomainException("SELLER_CANNOT_BID_OWN_AUCTION", "A seller cannot bid on their own auction.", 403);

        if (!DepositRules.IsOpenForDeposit(auction.AuctionState.Status, auction.AuctionState.ScheduledEndAt, now))
            throw new DomainException("AUCTION_NOT_OPEN_FOR_DEPOSIT", "The auction is not open for deposits.", 409);

        return auction;
    }

    /// <summary>
    /// Applies the real state of a hold to a deposit. Valid moves: Pending to Authorized or Failed, Authorized to Expired.
    /// Pending plus Authorized with a different amount becomes Failed, and one that arrives after the auction closed becomes Released;
    /// both set ReleasePending so the hold is given back. Anything else leaves the deposit unchanged and returns false.
    /// </summary>
    private bool ApplyHoldState(
        AuctionDeposit deposit, DepositHoldSnapshot snapshot, bool auctionOpen, DateTimeOffset now, out AuditAction action)
    {
        action = default;

        switch (deposit.Status, snapshot.Status)
        {
            case (DepositStatus.Pending, DepositHoldState.Authorized):
                if (snapshot.Amount != deposit.Amount)
                {
                    deposit.Status = DepositStatus.Failed;
                    deposit.FailureCode = "AMOUNT_MISMATCH";
                    deposit.ReleasePending = true;
                    deposit.PendingAction = DepositPendingAction.Release;
                    deposit.ReleaseReason = "AMOUNT_MISMATCH";
                    deposit.NextRetryAt = now;
                    action = AuditAction.DepositFail;
                    return true;
                }

                deposit.AuthorizedAt = snapshot.AuthorizedAt ?? now;
                deposit.ExpiresAt = snapshot.ProviderHoldExpiresAt;
                if (!auctionOpen)
                {
                    deposit.Status = DepositStatus.Released;
                    deposit.ReleasePending = true;
                    deposit.PendingAction = DepositPendingAction.Release;
                    deposit.ReleaseReason = "LATE_AUTHORIZATION";
                    deposit.NextRetryAt = now;
                    action = AuditAction.DepositRelease;
                    return true;
                }

                deposit.Status = DepositStatus.Authorized;
                deposit.FailureCode = null;
                action = AuditAction.DepositAuthorize;
                _notifications.NotifyAuctionDeposit(new AuctionDepositNotification(
                    deposit.AuctionId, deposit.Auction?.SnapshotProductTitle, deposit.Id, deposit.BidderId,
                    deposit.Amount, "Authorized", "DEPOSIT_HELD", deposit.AuthorizedAt ?? now));
                return true;

            case (DepositStatus.Pending, DepositHoldState.Failed):
                deposit.Status = DepositStatus.Failed;
                deposit.FailureCode = snapshot.FailureCode ?? "PAYMENT_FAILED";
                action = AuditAction.DepositFail;
                return true;

            case (DepositStatus.Authorized, DepositHoldState.Expired or DepositHoldState.Voided):
                deposit.Status = DepositStatus.Expired;
                action = AuditAction.DepositExpire;
                return true;

            default:
                return false;
        }
    }

    /// <summary>Gives the hold back after the state change was saved. A failure leaves ReleasePending set so a retry job can finish it.</summary>
    private async Task TryReleaseAsync(AuctionDeposit deposit, CancellationToken cancellationToken)
    {
        try
        {
            // The attempt number keeps a later hold of the same deposit from reusing this key.
            var outcome = await _wallet.ReleaseDepositHoldAsync(
                deposit.GatewayReference!, deposit.ReleaseReason ?? "ADMIN_FORCE_CANCEL",
                $"release:{deposit.Id}:{deposit.AttemptCount}", cancellationToken);

            if (outcome == DepositReleaseOutcome.AlreadyCaptured)
            {
                var cancelledOrder = await _dbContext.AuctionSettlements.AsNoTracking()
                    .AnyAsync(s => s.AuctionId == deposit.AuctionId && s.WinnerId == deposit.BidderId &&
                                   s.OrderId != null && s.Status == AuctionSettlementStatus.Cancelled &&
                                   !s.IsDeleted, cancellationToken);
                if (cancelledOrder)
                {
                    deposit.Status = DepositStatus.Captured;
                    deposit.PendingAction = DepositPendingAction.Refund;
                    deposit.ReleasePending = false;
                    deposit.NextRetryAt = _clock.GetUtcNow();
                    deposit.LastError = null;
                    deposit.UpdatedAt = _clock.GetUtcNow();
                    deposit.RowVersion += 1;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    return;
                }
                deposit.NeedsAttention = true;
                deposit.LastError = "Wallet hold was captured while Auction requested release.";
                _logger.LogError("Captured hold {HoldId} for deposit {DepositId} requires manual review.",
                    deposit.GatewayReference, deposit.Id);
            }
            else
            {
                var releasedAt = _clock.GetUtcNow();
                deposit.ReleasedAt = releasedAt;
                await QueueDepositNotificationAsync(
                    deposit, "Released", deposit.ReleaseReason ?? "DEPOSIT_RELEASED", releasedAt, null, cancellationToken);
            }

            deposit.ReleasePending = false;
            deposit.PendingAction = DepositPendingAction.None;
            deposit.NextRetryAt = null;
            if (!deposit.NeedsAttention)
            {
                deposit.LastError = null;
                deposit.ActionFailureCount = 0;
            }
            deposit.UpdatedAt = _clock.GetUtcNow();
            deposit.RowVersion += 1;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DomainException ex) when (ex.StatusCode >= 500)
        {
            _logger.LogWarning(
                "Release of hold {HoldId} for deposit {DepositId} failed ({ErrorCode}). It stays pending and must be retried.",
                deposit.GatewayReference, deposit.Id, ex.ErrorCode);
            RecordDepositActionFailure(deposit, ex.Message, _clock.GetUtcNow());
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
            }
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(
                ex, "Hold {HoldId} was released but deposit {DepositId} could not be updated. The release is safe to repeat.",
                deposit.GatewayReference, deposit.Id);
        }
    }

    private static DepositAuthorizationResult? MapToResult(DepositHoldState state) => state switch
    {
        DepositHoldState.Authorized => DepositAuthorizationResult.Authorized,
        DepositHoldState.Failed => DepositAuthorizationResult.Failed,
        DepositHoldState.Expired => DepositAuthorizationResult.Expired,
        DepositHoldState.Voided => DepositAuthorizationResult.Voided,
        _ => null
    };

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another request changed the same deposit, or used the same key or event, first.
            throw new DomainException(
                "CONCURRENT_UPDATE", "The deposit changed while it was being saved. Try again.", 409);
        }
    }

    // ------------------------------------------------------------------ 13: admin auction deposits

    /// <summary>
    /// Admin hoặc Staff đối soát danh sách cọc của một phiên đấu giá.
    /// Trả về thống kê tổng quan (summary), danh sách chi tiết có phân trang và lọc theo trạng thái.
    /// </summary>
    public async Task<AdminAuctionDepositsResponse> GetAdminAuctionDepositsAsync(
        ActorContext actor, Guid auctionId, string? status, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (auctionId == Guid.Empty)
            throw new DomainException("VAL_INVALID_IDENTIFIER", "Auction ID is required.", 400);

        if (page < 1)
            throw new DomainException("VALIDATION_ERROR", "Page must be greater than or equal to 1.", 400);

        if (pageSize is < 1 or > 100)
            throw new DomainException("VALIDATION_ERROR", "Page size must be between 1 and 100.", 400);

        DepositStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<DepositStatus>(status.Trim(), ignoreCase: true, out var parsedStatus) || !Enum.IsDefined(parsedStatus))
                throw new DomainException("VALIDATION_ERROR", "Invalid deposit status.", 400);

            statusFilter = parsedStatus;
        }

        var auctionExists = await _dbContext.Auctions
            .AsNoTracking()
            .AnyAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);

        if (!auctionExists)
            throw new EntityNotFoundException("AUCTION_NOT_FOUND", "Auction", auctionId);

        var allDeposits = await _dbContext.AuctionDeposits
            .AsNoTracking()
            .Where(d => d.AuctionId == auctionId && !d.IsDeleted)
            .Select(d => new { d.Status, d.Amount })
            .ToListAsync(cancellationToken);

        var total = allDeposits.Count;
        var authorized = allDeposits.Count(d => d.Status == DepositStatus.Authorized);
        var released = allDeposits.Count(d => d.Status == DepositStatus.Released);
        var captured = allDeposits.Count(d => d.Status == DepositStatus.Captured);
        var failed = allDeposits.Count(d => d.Status == DepositStatus.Failed);
        var expired = allDeposits.Count(d => d.Status == DepositStatus.Expired);
        var amountHeld = allDeposits.Where(d => d.Status == DepositStatus.Authorized).Sum(d => d.Amount);
        var amountCaptured = allDeposits.Where(d => d.Status == DepositStatus.Captured).Sum(d => d.Amount);

        var summary = new DepositSummaryDto(total, authorized, released, captured, failed, expired, amountHeld, amountCaptured);

        var query = _dbContext.AuctionDeposits
            .AsNoTracking()
            .Where(d => d.AuctionId == auctionId && !d.IsDeleted);

        if (statusFilter.HasValue)
            query = query.Where(d => d.Status == statusFilter.Value);

        var totalItems = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = rows.Select(d =>
        {
            string? pendingAction = d.PendingAction switch
            {
                DepositPendingAction.Release => "RELEASE_RETRYING",
                DepositPendingAction.Capture => "CAPTURE_RETRYING",
                DepositPendingAction.Refund => "REFUND_RETRYING",
                _ => d.ReleasePending ? "RELEASE_RETRYING" : null
            };
            bool needsAttention = d.NeedsAttention || d.ActionFailureCount > 10;
            string? capturePurpose = d.CapturePurpose ?? (d.Status == DepositStatus.Captured ? "OrderPayment" : null);
            DateTimeOffset? capturedAt = d.CapturedAt ?? (d.Status == DepositStatus.Captured ? d.UpdatedAt : null);

            return new AdminAuctionDepositItemDto(
                d.Id,
                d.BidderId,
                d.Amount,
                d.RatePercent,
                d.Status.ToString(),
                capturePurpose,
                d.GatewayReference,
                d.PolicyVersion,
                d.AuthorizedAt,
                d.ReleasedAt,
                capturedAt,
                pendingAction,
                needsAttention);
        }).ToList();

        return new AdminAuctionDepositsResponse(auctionId, summary, items, page, pageSize, totalItems);
    }

    // ------------------------------------------------------------------ 03: my deposit in auction

    /// <summary>Buyer xem danh sách & lịch sử tất cả các khoản cọc của chính mình trên toàn sàn.</summary>
    public async Task<PagedData<MyDepositListItemDto>> ListMyDepositsAsync(
        ActorContext actor, int page = 1, int pageSize = 20, string? status = null, CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);

        DepositStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            var trimmed = status.Trim();
            if (int.TryParse(trimmed, out _)
                || !Enum.TryParse<DepositStatus>(trimmed, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                throw new DomainException(
                    "VAL_INVALID_STATUS",
                    "Deposit status must be one of Pending, Authorized, Released, Captured, Failed or Expired.",
                    400);
            }
            statusFilter = parsed;
        }

        var query = _dbContext.AuctionDeposits
            .AsNoTracking()
            .Where(d => d.BidderId == actor.UserId && !d.IsDeleted);

        if (statusFilter is { } filter)
            query = query.Where(d => d.Status == filter);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(d => new MyDepositListItemDto(
                d.Id,
                d.AuctionId,
                d.Auction != null ? (d.Auction.SnapshotProductTitle ?? "Auction") : "Auction",
                d.Auction != null && d.Auction.AuctionState != null ? d.Auction.AuctionState.Status : null,
                d.Amount,
                d.RatePercent,
                d.Status,
                d.CapturePurpose,
                d.PolicyVersion,
                d.AuthorizedAt,
                d.ReleasedAt,
                d.CapturedAt,
                d.CreatedAt))
            .ToListAsync(cancellationToken);

        return new PagedData<MyDepositListItemDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    /// <summary>
    /// Cho Buyer biết trạng thái cọc của chính mình trong phiên và có được đặt giá hay không (API 03, Hướng C).
    /// Chưa có cọc trả về status = "NotCreated", canBid = false, blockReason = "DEPOSIT_REQUIRED" (200 OK).
    /// </summary>
    public async Task<MyDepositResponse> GetMyDepositAsync(
        ActorContext actor, Guid auctionId, CancellationToken cancellationToken = default)
    {
        if (auctionId == Guid.Empty)
            throw new DomainException("VAL_INVALID_IDENTIFIER", "Auction ID is required.", 400);

        var auctionExists = await _dbContext.Auctions
            .AsNoTracking()
            .AnyAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);

        if (!auctionExists)
            throw new EntityNotFoundException("AUCTION_NOT_FOUND", "Auction", auctionId);

        string? userBlockReason = null;
        try
        {
            var profile = await _users.GetAuctionDepositProfileAsync(actor.UserId, bypassCache: false, cancellationToken);
            if (string.Equals(profile.AccountStatus, "PENDINGVERIFICATION", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(profile.AccountStatus, "PENDING_VERIFICATION", StringComparison.OrdinalIgnoreCase))
            {
                userBlockReason = "ACCOUNT_NOT_VERIFIED";
            }
            else if (profile.IsRestricted || !string.Equals(profile.AccountStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                userBlockReason = "BIDDER_RESTRICTED";
            }
        }
        catch (DomainException)
        {
            // Reading deposit status should not fail if User service is temporarily unreachable.
        }

        var deposit = await _dbContext.AuctionDeposits
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.AuctionId == auctionId && d.BidderId == actor.UserId && !d.IsDeleted, cancellationToken);

        if (deposit is null)
        {
            return new MyDepositResponse(
                DepositId: null,
                Status: "NotCreated",
                Amount: null,
                RatePercent: null,
                AuthorizedAt: null,
                ExpiresAt: null,
                ReleasedAt: null,
                CapturedAt: null,
                CapturePurpose: null,
                CanBid: false,
                BlockReason: userBlockReason ?? "DEPOSIT_REQUIRED");
        }

        var (canBid, blockReason) = deposit.Status switch
        {
            DepositStatus.Authorized when userBlockReason is null => (true, (string?)null),
            DepositStatus.Authorized => (false, userBlockReason),
            DepositStatus.Pending => (false, "DEPOSIT_PENDING"),
            DepositStatus.Failed => (false, "DEPOSIT_FAILED"),
            DepositStatus.Expired => (false, "DEPOSIT_EXPIRED"),
            DepositStatus.Released or DepositStatus.Captured => (false, "DEPOSIT_CLOSED"),
            _ => (false, "DEPOSIT_REQUIRED")
        };

        string? capturePurpose = deposit.CapturePurpose ?? (deposit.Status == DepositStatus.Captured ? "OrderPayment" : null);
        DateTimeOffset? capturedAt = deposit.CapturedAt ?? (deposit.Status == DepositStatus.Captured ? deposit.UpdatedAt : null);

        return new MyDepositResponse(
            deposit.Id,
            deposit.Status.ToString(),
            deposit.Amount,
            deposit.RatePercent,
            deposit.AuthorizedAt,
            ExpiresAt: null,
            deposit.ReleasedAt,
            capturedAt,
            capturePurpose,
            canBid,
            blockReason);
    }

    // ------------------------------------------------------------------ Release & background retry helpers (Overview 4.1 & v0.4)

    /// <summary>
    /// Giải phóng các cọc đã được đánh dấu ReleasePending của một phiên đấu giá (fast path sau khi commit huỷ phiên hoặc kết thúc phiên).
    /// Lỗi mạng từ Wallet được ghi log và giữ nguyên ReleasePending = true để job quét thử lại sau.
    /// </summary>
    public async Task ReleasePendingDepositsForAuctionAsync(Guid auctionId, CancellationToken cancellationToken = default)
    {
        var pendingDeposits = await _dbContext.AuctionDeposits
            .Where(d => d.AuctionId == auctionId && (d.ReleasePending || d.PendingAction == DepositPendingAction.Release) && !d.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var deposit in pendingDeposits)
        {
            await ProcessSingleDepositReleaseAsync(deposit, cancellationToken);
        }
    }

    /// <summary>
    /// Job quét định kỳ (Overview 4.1 &amp; v0.4):
    /// 1) Thực hiện lại các việc nền trên cọc (Release, Capture, Refund) đang Pending có NextRetryAt &lt;= now và NeedsAttention = false.
    /// 2) Đồng bộ các cọc bị treo ở trạng thái Pending (do timeout lúc gọi W1 ở API 02) qua W4 by-reference; quá 10 phút không có hold thì chuyển Failed.
    /// </summary>
    public async Task<AdminDepositReconcileResponse> ReconcileAuctionDepositsAsync(
        ActorContext actor, Guid auctionId, CancellationToken cancellationToken = default)
    {
        if (auctionId == Guid.Empty)
            throw new DomainException("VAL_INVALID_IDENTIFIER", "Auction ID is required.", 400);

        var auctionExists = await _dbContext.Auctions
            .AsNoTracking()
            .AnyAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (!auctionExists)
            throw new EntityNotFoundException("AUCTION_NOT_FOUND", "Auction", auctionId);

        var now = _clock.GetUtcNow();
        var stuckDeposits = await _dbContext.AuctionDeposits
            .Where(d => d.AuctionId == auctionId
                        && !d.IsDeleted
                        && (d.PendingAction != DepositPendingAction.None || d.ReleasePending || d.NeedsAttention))
            .ToListAsync(cancellationToken);

        foreach (var deposit in stuckDeposits)
        {
            deposit.NeedsAttention = false;
            deposit.ActionFailureCount = 0;
            deposit.NextRetryAt = now;
            deposit.LastError = null;
            deposit.UpdatedAt = now;
            deposit.RowVersion += 1;
        }

        if (stuckDeposits.Count > 0)
        {
            _dbContext.AddAuditLog(
                AuditAction.DepositReconcile, "Auction", auctionId.ToString(),
                oldValues: null,
                newValues: new { queuedDeposits = stuckDeposits.Count },
                userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await ProcessDueDepositRetriesAsync(50, cancellationToken);

        var remaining = await _dbContext.AuctionDeposits
            .AsNoTracking()
            .CountAsync(d => d.AuctionId == auctionId
                             && !d.IsDeleted
                             && (d.PendingAction != DepositPendingAction.None || d.ReleasePending || d.NeedsAttention),
                cancellationToken);

        return new AdminDepositReconcileResponse(auctionId, stuckDeposits.Count, remaining, now);
    }

    public async Task ProcessDueDepositRetriesAsync(int limit, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        var dueDeposits = await _dbContext.AuctionDeposits
            .Where(d => !d.IsDeleted
                        && !d.NeedsAttention
                        && (d.PendingAction != DepositPendingAction.None || d.ReleasePending)
                        && (d.NextRetryAt == null || d.NextRetryAt <= now))
            .OrderBy(d => d.NextRetryAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        foreach (var deposit in dueDeposits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = deposit.PendingAction != DepositPendingAction.None
                ? deposit.PendingAction
                : DepositPendingAction.Release;

            switch (action)
            {
                case DepositPendingAction.Release:
                    await ProcessSingleDepositReleaseAsync(deposit, cancellationToken);
                    break;
                case DepositPendingAction.Capture:
                    await ProcessSingleDepositCaptureAsync(deposit, cancellationToken);
                    break;
                case DepositPendingAction.Refund:
                    await ProcessSingleDepositRefundAsync(deposit, cancellationToken);
                    break;
            }
        }

        // Reconcile deposits left in Pending due to W1 timeout (v0.4 section 4.2 & 5.4)
        var stuckPending = await _dbContext.AuctionDeposits
            .Where(d => !d.IsDeleted
                        && d.Status == DepositStatus.Pending
                        && !d.ReleasePending
                        && d.PendingAction == DepositPendingAction.None
                        && d.UpdatedAt <= now.AddSeconds(-30))
            .OrderBy(d => d.UpdatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        foreach (var deposit in stuckPending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReconcilePendingDepositAsync(deposit, now, cancellationToken);
        }

        await ReconcileOrphanedDepositsAsync(limit, cancellationToken);
    }

    /// <summary>How long an Authorized deposit on a closed auction is left alone, so a settlement that is still being processed is not raced.</summary>
    internal static readonly TimeSpan OrphanedDepositGrace = TimeSpan.FromMinutes(5);

    internal enum OrphanedDepositOutcome { Release, ForfeitForNonPayment, CaptureForOrder }

    /// <summary>
    /// Decides what to do with an Authorized deposit whose auction is already closed and which has no work queued.
    /// Money is only taken when Auction's own records prove the buyer owed it; anything doubtful goes back to the buyer.
    /// </summary>
    internal static OrphanedDepositOutcome DecideOrphanedDeposit(
        AuctionDeposit deposit, AuctionSettlement? settlement, bool boughtAsRunnerUp, DateTimeOffset now)
    {
        if (settlement?.WinnerId is not { } winnerId || winnerId != deposit.BidderId)
            return OrphanedDepositOutcome.Release;

        // The buyer paid for the order, so the deposit was applied to it and the capture was simply never queued.
        if (settlement.Status == AuctionSettlementStatus.Paid)
            return OrphanedDepositOutcome.CaptureForOrder;

        // Both winner and runner-up (who accepted second chance) forfeit their deposit once the stored payment deadline has passed.
        if (settlement.Status is AuctionSettlementStatus.Expired or AuctionSettlementStatus.Failed
            && settlement.PaymentDeadlineAt is { } deadline && deadline <= now)
            return OrphanedDepositOutcome.ForfeitForNonPayment;

        return OrphanedDepositOutcome.Release;
    }

    /// <summary>
    /// Finds deposits still Authorized on an auction that is Failed, Cancelled or Settled and queues the missing release or capture.
    /// Without it, a callback that was lost, handled by older code or contradicted by stale data leaves the buyer's money locked for good.
    /// </summary>
    private async Task ReconcileOrphanedDepositsAsync(int limit, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var cutoff = now - OrphanedDepositGrace;

        var orphans = await _dbContext.AuctionDeposits
            .Where(d => !d.IsDeleted
                        && d.Status == DepositStatus.Authorized
                        && d.PendingAction == DepositPendingAction.None
                        && !d.ReleasePending
                        && !d.NeedsAttention
                        && d.UpdatedAt <= cutoff
                        && _dbContext.AuctionStates.Any(s => s.AuctionId == d.AuctionId && !s.IsDeleted
                            && (s.Status == AuctionStatus.Failed || s.Status == AuctionStatus.Cancelled || s.Status == AuctionStatus.Settled)))
            .OrderBy(d => d.UpdatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        if (orphans.Count == 0)
            return;

        foreach (var deposit in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var settlement = await _dbContext.AuctionSettlements
                .FirstOrDefaultAsync(s => s.AuctionId == deposit.AuctionId && !s.IsDeleted, cancellationToken);
            var boughtAsRunnerUp = await _dbContext.AuctionWinnerOffers.AnyAsync(
                o => o.AuctionId == deposit.AuctionId && o.BidderId == deposit.BidderId && o.Status == AuctionWinnerOfferStatus.Accepted,
                cancellationToken);

            var outcome = DecideOrphanedDeposit(deposit, settlement, boughtAsRunnerUp, now);
            _logger.LogWarning(
                "Deposit {DepositId} of auction {AuctionId} is still Authorized on a closed auction. Reconciling: {Outcome}.",
                deposit.Id, deposit.AuctionId, outcome);

            switch (outcome)
            {
                case OrphanedDepositOutcome.Release:
                    deposit.Status = DepositStatus.Released;
                    deposit.ReleasePending = true;
                    deposit.PendingAction = DepositPendingAction.Release;
                    deposit.ReleaseReason = "RECONCILED_CLOSED_AUCTION";
                    break;

                case OrphanedDepositOutcome.CaptureForOrder:
                    deposit.PendingAction = DepositPendingAction.Capture;
                    deposit.PendingPurpose = "OrderPayment";
                    break;

                case OrphanedDepositOutcome.ForfeitForNonPayment:
                    deposit.PendingAction = DepositPendingAction.Capture;
                    deposit.PendingPurpose = "Forfeit";
                    await QueueMissingViolationAsync(deposit, settlement!, boughtAsRunnerUp, now, cancellationToken);
                    break;
            }

            deposit.NextRetryAt = now;
            deposit.NeedsAttention = false;
            deposit.UpdatedAt = now;
            deposit.RowVersion += 1;

            _dbContext.AddAuditLog(
                AuditAction.DepositReconcile, "AuctionDeposit", deposit.Id.ToString(),
                oldValues: new { status = DepositStatus.Authorized },
                newValues: new { depositId = deposit.Id, auctionId = deposit.AuctionId, outcome = outcome.ToString() });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A forfeited deposit goes with a violation for the buyer. Queue it if the auction has none queued or sent.</summary>
    private async Task QueueMissingViolationAsync(
        AuctionDeposit deposit, AuctionSettlement settlement, bool isSecondChance, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var auction = await _dbContext.Auctions.FirstOrDefaultAsync(a => a.Id == deposit.AuctionId, cancellationToken);
        if (auction is null || auction.ViolationState != BackgroundSyncState.None || settlement.OrderId is not { } orderId)
            return;

        auction.ViolationState = BackgroundSyncState.Pending;
        auction.PendingViolationBuyerId = deposit.BidderId;
        auction.PendingViolationOrderId = orderId;
        auction.PendingViolationOccurredAt = settlement.PaymentDeadlineAt ?? now;
        auction.PendingViolationIsSecondChance = isSecondChance;
        auction.NextRetryAt = now;
        auction.NeedsAttention = false;
        auction.UpdatedAt = now;
    }

    private async Task ProcessSingleDepositReleaseAsync(AuctionDeposit deposit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deposit.GatewayReference))
        {
            DepositHoldSnapshot? snapshot;
            try
            {
                snapshot = await _wallet.GetDepositHoldByReferenceAsync(deposit.Id, cancellationToken);
            }
            catch (DomainException ex) when (ex.StatusCode >= 500)
            {
                _logger.LogWarning(
                    "Lookup by reference for pending deposit {DepositId} failed ({ErrorCode}). Release stays pending and must be retried.",
                    deposit.Id, ex.ErrorCode);
                RecordDepositActionFailure(deposit, ex.Message, _clock.GetUtcNow());
                try
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                }
                return;
            }

            // Refunded money is already back with the buyer, so there is nothing left to release either.
            if (snapshot is null || snapshot.Status is DepositHoldState.Released or DepositHoldState.Voided or DepositHoldState.Expired
                    or DepositHoldState.Failed or DepositHoldState.Refunded)
            {
                var now = _clock.GetUtcNow();
                deposit.ReleasePending = false;
                deposit.PendingAction = DepositPendingAction.None;
                deposit.NextRetryAt = null;
                deposit.LastError = null;
                deposit.ActionFailureCount = 0;
                deposit.NeedsAttention = false;
                deposit.ReleasedAt = now;
                deposit.UpdatedAt = now;
                deposit.RowVersion += 1;
                await QueueDepositNotificationAsync(
                    deposit, "Released", deposit.ReleaseReason ?? "DEPOSIT_RELEASED", now, null, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
                return;
            }

            deposit.GatewayReference = snapshot.HoldId;
        }

        await TryReleaseAsync(deposit, cancellationToken);
    }

    private async Task ProcessSingleDepositCaptureAsync(AuctionDeposit deposit, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        try
        {
            if (string.IsNullOrWhiteSpace(deposit.GatewayReference))
            {
                var snapshot = await _wallet.GetDepositHoldByReferenceAsync(deposit.Id, cancellationToken);
                if (snapshot is null)
                {
                    RecordDepositActionFailure(deposit, "Hold reference was not found in Wallet for capture.", now);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    return;
                }
                deposit.GatewayReference = snapshot.HoldId;
            }

            var purpose = string.IsNullOrWhiteSpace(deposit.PendingPurpose) ? "OrderPayment" : deposit.PendingPurpose!;
            var auction = await _dbContext.Auctions
                .AsNoTracking()
                .Where(a => a.Id == deposit.AuctionId)
                .Select(a => new { a.SellerId, a.SnapshotProductTitle })
                .FirstOrDefaultAsync(cancellationToken);

            decimal sellerPercent = 70m;
            if (string.Equals(purpose, "Forfeit", StringComparison.OrdinalIgnoreCase))
            {
                var policy = await DepositPolicyReader.ReadByVersionAsync(_dbContext, deposit.PolicyVersion, cancellationToken);
                sellerPercent = policy.SellerPercent;
            }

            Guid? orderId = null;
            if (!string.Equals(purpose, "Forfeit", StringComparison.OrdinalIgnoreCase))
            {
                orderId = await _dbContext.AuctionSettlements.AsNoTracking()
                    .Where(s => s.AuctionId == deposit.AuctionId && s.WinnerId == deposit.BidderId && !s.IsDeleted)
                    .Select(s => s.OrderId).FirstOrDefaultAsync(cancellationToken);
                if (orderId is null)
                    throw new DomainException("ORDER_NOT_READY", "The auction order is not ready for deposit capture.", 503);
            }
            var transfers = BuildCaptureTransfers(deposit, purpose, auction?.SellerId, sellerPercent);
            await _wallet.CaptureDepositHoldAsync(
                deposit.GatewayReference,
                new CaptureDepositHoldCommand(purpose, deposit.Amount, transfers, orderId),
                $"capture:{deposit.Id}",
                cancellationToken);

            deposit.Status = DepositStatus.Captured;
            deposit.CapturePurpose = purpose;
            deposit.CapturedAt = now;
            deposit.PendingAction = DepositPendingAction.None;
            deposit.NextRetryAt = null;
            deposit.LastError = null;
            deposit.ActionFailureCount = 0;
            deposit.NeedsAttention = false;
            deposit.UpdatedAt = now;
            deposit.RowVersion += 1;
            _notifications.NotifyAuctionDeposit(new AuctionDepositNotification(
                deposit.AuctionId, auction?.SnapshotProductTitle, deposit.Id, deposit.BidderId,
                deposit.Amount, "Captured", purpose, now));
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DomainException ex)
        {
            _logger.LogWarning(
                "Capture of hold {HoldId} for deposit {DepositId} failed ({ErrorCode}). It stays pending and will be retried.",
                deposit.GatewayReference, deposit.Id, ex.ErrorCode);
            RecordDepositActionFailure(deposit, ex.Message, _clock.GetUtcNow());
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
            }
        }
    }

    private async Task ProcessSingleDepositRefundAsync(AuctionDeposit deposit, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        try
        {
            if (string.IsNullOrWhiteSpace(deposit.GatewayReference))
            {
                var snapshot = await _wallet.GetDepositHoldByReferenceAsync(deposit.Id, cancellationToken);
                if (snapshot is null)
                {
                    RecordDepositActionFailure(deposit, "Hold reference was not found in Wallet for refund.", now);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    return;
                }
                deposit.GatewayReference = snapshot.HoldId;
            }

            var orderId = await _dbContext.AuctionSettlements.AsNoTracking()
                .Where(s => s.AuctionId == deposit.AuctionId && s.WinnerId == deposit.BidderId && !s.IsDeleted)
                .Select(s => s.OrderId).FirstOrDefaultAsync(cancellationToken);
            if (orderId is null)
                throw new DomainException("ORDER_NOT_READY", "The auction order is not ready for deposit refund.", 503);
            await _wallet.RefundCapturedHoldAsync(
                deposit.GatewayReference,
                orderId.Value,
                deposit.Amount,
                deposit.ReleaseReason ?? "SELLER_REJECTED",
                $"refund:{deposit.Id}",
                cancellationToken);

            deposit.Status = DepositStatus.Released;
            deposit.ReleasedAt = now;
            deposit.PendingAction = DepositPendingAction.None;
            deposit.ReleasePending = false;
            deposit.NextRetryAt = null;
            deposit.LastError = null;
            deposit.ActionFailureCount = 0;
            deposit.NeedsAttention = false;
            deposit.UpdatedAt = now;
            deposit.RowVersion += 1;
            await QueueDepositNotificationAsync(
                deposit, "Refunded", deposit.ReleaseReason ?? "SELLER_REJECTED", now, null, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DomainException ex)
        {
            _logger.LogWarning(
                "Refund of captured hold {HoldId} for deposit {DepositId} failed ({ErrorCode}). It stays pending and will be retried.",
                deposit.GatewayReference, deposit.Id, ex.ErrorCode);
            RecordDepositActionFailure(deposit, ex.Message, _clock.GetUtcNow());
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
            }
        }
    }

    private async Task ReconcilePendingDepositAsync(AuctionDeposit deposit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        DepositHoldSnapshot? snapshot;
        try
        {
            snapshot = await _wallet.GetDepositHoldByReferenceAsync(deposit.Id, cancellationToken);
        }
        catch (DomainException ex) when (ex.StatusCode >= 500)
        {
            _logger.LogWarning(
                "Reconciling pending deposit {DepositId} with Wallet failed ({ErrorCode}). Will retry next cycle.",
                deposit.Id, ex.ErrorCode);
            return;
        }

        if (snapshot is not null && snapshot.Status == DepositHoldState.Authorized)
        {
            var auctionState = await _dbContext.AuctionStates
                .AsNoTracking()
                .Where(s => s.AuctionId == deposit.AuctionId && !s.IsDeleted)
                .Select(s => new { s.Status, s.ScheduledEndAt })
                .FirstOrDefaultAsync(cancellationToken);
            var auctionOpen = auctionState is not null &&
                              DepositRules.IsOpenForDeposit(auctionState.Status, auctionState.ScheduledEndAt, now);

            deposit.GatewayReference = snapshot.HoldId;
            deposit.AuthorizedAt = snapshot.AuthorizedAt ?? now;
            deposit.ExpiresAt = null;
            deposit.FailureCode = null;

            if (!auctionOpen)
            {
                deposit.Status = DepositStatus.Released;
                deposit.ReleasePending = true;
                deposit.PendingAction = DepositPendingAction.Release;
                deposit.ReleaseReason = "LATE_AUTHORIZATION";
                deposit.NextRetryAt = now;
                deposit.UpdatedAt = now;
                deposit.RowVersion += 1;
                await _dbContext.SaveChangesAsync(cancellationToken);
                await TryReleaseAsync(deposit, cancellationToken);
                return;
            }

            deposit.Status = DepositStatus.Authorized;
            deposit.UpdatedAt = now;
            deposit.RowVersion += 1;
            await QueueDepositNotificationAsync(
                deposit, "Authorized", "DEPOSIT_HELD", deposit.AuthorizedAt ?? now, null, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        if (now - deposit.UpdatedAt >= TimeSpan.FromMinutes(10))
        {
            deposit.Status = DepositStatus.Failed;
            deposit.FailureCode = "HOLD_CREATION_TIMEOUT";
            deposit.UpdatedAt = now;
            deposit.RowVersion += 1;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task QueueDepositNotificationAsync(
        AuctionDeposit deposit, string depositStatus, string? reason, DateTimeOffset occurredAt, string? traceId,
        CancellationToken cancellationToken)
    {
        var title = deposit.Auction?.SnapshotProductTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = await _dbContext.Auctions
                .AsNoTracking()
                .Where(a => a.Id == deposit.AuctionId)
                .Select(a => a.SnapshotProductTitle)
                .FirstOrDefaultAsync(cancellationToken);
        }

        _notifications.NotifyAuctionDeposit(new AuctionDepositNotification(
            deposit.AuctionId, title, deposit.Id, deposit.BidderId,
            deposit.Amount, depositStatus, reason, occurredAt, traceId));
    }

    private static IReadOnlyList<HoldTransferInstruction> BuildCaptureTransfers(
        AuctionDeposit deposit, string purpose, Guid? sellerId, decimal sellerPercent = 70m)
    {
        // Wallet accepts only SELLER_WALLET and PLATFORM recipients for a forfeit, so a missing seller sends the whole amount to the platform.
        if (string.Equals(purpose, "Forfeit", StringComparison.OrdinalIgnoreCase) && !sellerId.HasValue)
        {
            return new[]
            {
                new HoldTransferInstruction("PLATFORM", null, deposit.Amount, "Forfeited auction deposit for platform")
            };
        }

        if (string.Equals(purpose, "Forfeit", StringComparison.OrdinalIgnoreCase) && sellerId.HasValue)
        {
            var clampedSellerPercent = Math.Clamp(sellerPercent, 0m, 100m);
            var sellerShare = Math.Floor(deposit.Amount * clampedSellerPercent / 100m);
            var platformShare = deposit.Amount - sellerShare;
            var list = new List<HoldTransferInstruction>();
            if (sellerShare > 0m)
                list.Add(new HoldTransferInstruction("SELLER_WALLET", sellerId.Value, sellerShare, "Forfeited auction deposit share for seller"));
            if (platformShare > 0m)
                list.Add(new HoldTransferInstruction("PLATFORM", null, platformShare, "Forfeited auction deposit share for platform"));
            return list;
        }

        return new[]
        {
            new HoldTransferInstruction(
                "SYSTEM_ESCROW",
                null,
                deposit.Amount,
                $"Auction deposit applied to order payment for auction {deposit.AuctionId}")
        };
    }

    private static void RecordDepositActionFailure(AuctionDeposit deposit, string error, DateTimeOffset now)
    {
        // AttemptCount is part of the Idempotency-Key that creates the hold, so failed retries are counted separately.
        deposit.ActionFailureCount += 1;
        deposit.LastError = error.Length > 500 ? error[..500] : error;
        if (deposit.ActionFailureCount > 10)
        {
            deposit.NeedsAttention = true;
            deposit.NextRetryAt = null;
        }
        else
        {
            var backoffMinutes = deposit.ActionFailureCount switch
            {
                <= 1 => 1,
                2 => 2,
                3 => 5,
                4 => 10,
                _ => 30
            };
            deposit.NextRetryAt = now.AddMinutes(backoffMinutes);
        }
        deposit.UpdatedAt = now;
        deposit.RowVersion += 1;
    }

    private static void ValidateCreateRequest(CreateDepositRequest request)
    {
        if (request.AcceptedPolicyVersion is null or < 0)
            throw new DomainException("VAL_INVALID_INPUT", "Accepted policy version is required.", 400);
        if (request.AcceptedDepositAmount is null or <= 0m)
            throw new DomainException("VAL_INVALID_INPUT", "Accepted deposit amount must be greater than zero.", 400);
    }

    private static CreateDepositResponse ToCreateResponse(AuctionDeposit deposit) =>
        new(deposit.Id, deposit.Status, deposit.Amount, deposit.RatePercent, deposit.PolicyVersion);

    private static DomainException DepositRequired() =>
        new("DEPOSIT_REQUIRED", "A valid deposit is required before placing a bid.", 403);

    private static string HashRequest(Guid auctionId, Guid userId, int policyVersion, decimal amount)
    {
        var text = $"{auctionId:N}|{userId:N}|{policyVersion}|{amount.ToString("0.00", CultureInfo.InvariantCulture)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static CreateDepositResponse? DeserializeResponse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonSerializer.Deserialize<CreateDepositResponse>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
