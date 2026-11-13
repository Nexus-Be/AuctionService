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
    private readonly DepositOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<DepositService> _logger;

    public DepositService(
        IAuctionDbContext dbContext, IUserClient users, IWalletClient wallet, DepositOptions options,
        TimeProvider clock, ILogger<DepositService> logger)
    {
        _dbContext = dbContext;
        _users = users;
        _wallet = wallet;
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
                    deposit.Id, actor.UserId, deposit.Amount, DepositRules.Currency),
                $"deposit:{deposit.Id}:{deposit.AttemptCount}",
                cancellationToken);
        }
        catch (DomainException ex) when (ex.StatusCode == 422)
        {
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
    private static bool ApplyHoldState(
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
                    deposit.ReleaseReason = "AMOUNT_MISMATCH";
                    action = AuditAction.DepositFail;
                    return true;
                }

                deposit.AuthorizedAt = snapshot.AuthorizedAt ?? now;
                deposit.ExpiresAt = snapshot.ProviderHoldExpiresAt;
                if (!auctionOpen)
                {
                    deposit.Status = DepositStatus.Released;
                    deposit.ReleasePending = true;
                    deposit.ReleaseReason = "LATE_AUTHORIZATION";
                    action = AuditAction.DepositRelease;
                    return true;
                }

                deposit.Status = DepositStatus.Authorized;
                deposit.FailureCode = null;
                action = AuditAction.DepositAuthorize;
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
                _logger.LogError(
                    "Deposit {DepositId} could not be released because hold {HoldId} was already captured. An admin must check it.",
                    deposit.Id, deposit.GatewayReference);
            else
                deposit.ReleasedAt = _clock.GetUtcNow();

            deposit.ReleasePending = false;
            deposit.UpdatedAt = _clock.GetUtcNow();
            deposit.RowVersion += 1;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DomainException ex) when (ex.StatusCode >= 500)
        {
            _logger.LogWarning(
                "Release of hold {HoldId} for deposit {DepositId} failed ({ErrorCode}). It stays pending and must be retried.",
                deposit.GatewayReference, deposit.Id, ex.ErrorCode);
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
            string? pendingAction = d.ReleasePending ? "RELEASE_RETRYING" : null;
            bool needsAttention = d.AttemptCount >= 10;
            string? capturePurpose = d.Status == DepositStatus.Captured ? "OrderPayment" : null;
            DateTimeOffset? capturedAt = d.Status == DepositStatus.Captured ? d.UpdatedAt : null;

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

        var isRestricted = false;
        try
        {
            var profile = await _users.GetAuctionDepositProfileAsync(actor.UserId, bypassCache: false, cancellationToken);
            if (profile.IsRestricted || string.Equals(profile.AccountStatus, "BANNED", StringComparison.OrdinalIgnoreCase))
                isRestricted = true;
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
                BlockReason: isRestricted ? "BIDDER_RESTRICTED" : "DEPOSIT_REQUIRED");
        }

        var (canBid, blockReason) = deposit.Status switch
        {
            DepositStatus.Authorized when !isRestricted => (true, (string?)null),
            DepositStatus.Authorized => (false, "BIDDER_RESTRICTED"),
            DepositStatus.Pending => (false, "DEPOSIT_PENDING"),
            DepositStatus.Failed => (false, "DEPOSIT_FAILED"),
            DepositStatus.Expired => (false, "DEPOSIT_EXPIRED"),
            DepositStatus.Released or DepositStatus.Captured => (false, "DEPOSIT_CLOSED"),
            _ => (false, "DEPOSIT_REQUIRED")
        };

        string? capturePurpose = deposit.Status == DepositStatus.Captured ? "OrderPayment" : null;
        DateTimeOffset? capturedAt = deposit.Status == DepositStatus.Captured ? deposit.UpdatedAt : null;

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

    // ------------------------------------------------------------------ Release helper for auction cancel / end

    /// <summary>
    /// Giải phóng các cọc đã được đánh dấu ReleasePending của một phiên đấu giá (fast path sau khi commit huỷ phiên hoặc kết thúc phiên).
    /// Lỗi mạng từ Wallet được ghi log và giữ nguyên ReleasePending = true để job quét thử lại sau.
    /// </summary>
    public async Task ReleasePendingDepositsForAuctionAsync(Guid auctionId, CancellationToken cancellationToken = default)
    {
        var pendingDeposits = await _dbContext.AuctionDeposits
            .Where(d => d.AuctionId == auctionId && d.ReleasePending && !d.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var deposit in pendingDeposits)
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
                    continue;
                }

                if (snapshot is null || snapshot.Status is DepositHoldState.Released or DepositHoldState.Voided or DepositHoldState.Expired or DepositHoldState.Failed)
                {
                    deposit.ReleasePending = false;
                    deposit.ReleasedAt = _clock.GetUtcNow();
                    deposit.UpdatedAt = _clock.GetUtcNow();
                    deposit.RowVersion += 1;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    continue;
                }

                deposit.GatewayReference = snapshot.HoldId;
            }

            await TryReleaseAsync(deposit, cancellationToken);
        }
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
