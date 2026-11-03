namespace Auction.Application.Services.Bid;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using global::Auction.Application.Common;
using global::Auction.Application.Common.Extensions;
using global::Auction.Application.Common.Models;
using global::Auction.Application.Interfaces;
using global::Auction.Domain.Entities;
using global::Auction.Domain.Enums;
using global::Auction.Domain.Exceptions;
using global::Auction.Domain.Rules;

public sealed class BidService : IBidService
{
    private const string AnonymousName = "Người dùng ẩn danh";
    private static readonly TimeSpan IdempotencyLifetime = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAuctionDbContext _dbContext;
    private readonly IUserClient _users;
    private readonly TimeProvider _clock;

    public BidService(IAuctionDbContext dbContext, IUserClient users, TimeProvider clock)
    {
        _dbContext = dbContext;
        _users = users;
        _clock = clock;
    }

    // ------------------------------------------------------------------ History

    public async Task<PagedData<BidDto>> ListBidsByAuctionIdAsync(Guid auctionId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);

        var auctionExists = await _dbContext.Auctions
            .AsNoTracking()
            .AnyAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (!auctionExists)
            throw new EntityNotFoundException("AUCTION_NOT_FOUND", "Auction", auctionId);

        var query = _dbContext.BidHistories
            .AsNoTracking()
            .Where(h => h.AuctionId == auctionId && !h.IsDeleted);

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(h => h.SequenceNo)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(h => new
            {
                h.BidId,
                h.SequenceNo,
                h.BidderId,
                h.Amount,
                h.PlacedAt,
                h.IsOutbid,
                h.IsWinning,
                h.IsInvalidated
            })
            .ToListAsync(cancellationToken);

        // Names are masked by the User service. Without them the bidder is shown as anonymous:
        // the stored display name is never exposed because it is not masked.
        var names = await TryGetNamesAsync(rows.Select(r => r.BidderId).Distinct().ToList(), cancellationToken);

        var items = rows
            .Select(r => new BidDto(
                r.BidId, r.SequenceNo, r.BidderId,
                names.TryGetValue(r.BidderId, out var info) ? info.MaskedName : AnonymousName,
                r.Amount, r.PlacedAt, r.IsOutbid, r.IsWinning, r.IsInvalidated))
            .ToList();

        return new PagedData<BidDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    // ------------------------------------------------------------------ Place a bid

    public async Task<PlaceBidResponse> PlaceBidAsync(
        ActorContext actor, Guid auctionId, string? idempotencyKey, string? ifMatch,
        PlaceBidRequest request, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        // 1. Input checks (no database, no other service).
        if (auctionId == Guid.Empty)
            throw new DomainException("VAL_INVALID_IDENTIFIER", "Auction ID is required.", 400);
        BidRules.ValidateIdempotencyKey(idempotencyKey);
        var expectedRowVersion = BidRules.ParseRowVersion(ifMatch);
        BidRules.ValidateAmount(request.Amount);

        var amount = request.Amount!.Value;
        var key = idempotencyKey!.Trim();
        var requestHash = HashRequest(auctionId, actor.UserId, amount);

        // 2. Same key again: return the first result, or refuse when the request is different.
        var existing = await _dbContext.IdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == key && k.Scope == IdempotencyScope.Bid && k.ExpiresAt > now, cancellationToken);
        if (existing is not null)
        {
            if (existing.RequestHash != requestHash)
                throw new BusinessRuleValidationException(
                    "IDEMPOTENCY_KEY_REUSED", "The Idempotency-Key was already used with a different request.", 422);

            var saved = DeserializeResponse(existing.ResponseBody);
            if (saved is not null)
                return saved;
        }

        // 3. The auction must exist and be open for bids.
        var auction = await _dbContext.Auctions
            .Include(a => a.AuctionState)
            .FirstOrDefaultAsync(a => a.Id == auctionId && !a.IsDeleted, cancellationToken);
        if (auction?.AuctionState is null)
            throw new EntityNotFoundException("AUCTION_NOT_FOUND", "Auction", auctionId);

        var state = auction.AuctionState;
        BidRules.EnsureBiddable(state.Status, state.ScheduledEndAt, now);

        // 4. The bidder saw an older version of the auction: someone else bid first.
        if (state.RowVersion != expectedRowVersion)
            throw new DomainException(
                "CONCURRENT_UPDATE", "The auction changed since it was loaded. Reload it and try again.", 409);

        // 5. User must confirm the bidder (reputation, account status). No answer means no bid.
        var eligibility = await _users.VerifyBidEligibilityAsync(actor.UserId, cancellationToken);
        if (!eligibility.Eligible)
            throw UserEligibilityErrors.FromFailureReason(
                eligibility.FailureReason, "place a bid", "BIDDER_NOT_ELIGIBLE", "The user is not eligible to place a bid.");

        // 6. The amount must reach the minimum.
        var minNextBid = BidRules.MinNextBid(auction.StartPriceAmount, auction.BidStepAmount, state.CurrentPriceAmount);
        BidRules.EnsureMeetsMinimum(amount, minNextBid);

        // 7. Write everything in one SaveChanges.
        var previousWinner = await _dbContext.BidHistories
            .Where(h => h.AuctionId == auctionId && h.IsWinning)
            .ToListAsync(cancellationToken);
        foreach (var history in previousWinner)
        {
            history.IsWinning = false;
            history.IsOutbid = true;
            history.UpdatedAt = now;
        }

        var bidSeq = state.BidCount + 1;
        var bid = new Bid
        {
            Id = Guid.NewGuid(),
            AuctionId = auctionId,
            BidSeq = bidSeq,
            BidderId = actor.UserId,
            Amount = amount,
            PlacedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        _dbContext.Bids.Add(bid);
        _dbContext.BidHistories.Add(new BidHistory
        {
            Id = Guid.NewGuid(),
            AuctionId = auctionId,
            BidId = bid.Id,
            SequenceNo = bidSeq,
            BidderId = actor.UserId,
            Amount = amount,
            PlacedAt = now,
            IsWinning = true,
            CreatedAt = now,
            UpdatedAt = now
        });

        var oldValues = new
        {
            currentPriceAmount = state.CurrentPriceAmount,
            bidCount = state.BidCount,
            scheduledEndAt = state.ScheduledEndAt,
            extensionCount = state.ExtensionCount
        };

        state.CurrentPriceAmount = amount;
        state.CurrentWinnerId = actor.UserId;
        state.BidCount = bidSeq;
        state.RowVersion += 1;
        state.UpdatedAt = now;

        // Anti-sniping: a bid close to the end moves the end time, a limited number of times.
        var extensionWindow = TimeSpan.FromMinutes(auction.AntiSnipingExtensionMinutes);
        if (auction.AntiSnipingExtensionMinutes > 0 &&
            state.ExtensionCount < auction.MaxExtensionCount &&
            state.ScheduledEndAt - now <= extensionWindow)
        {
            var previousEnd = state.ScheduledEndAt;
            state.ScheduledEndAt = previousEnd.Add(extensionWindow);
            state.ExtensionCount += 1;

            _dbContext.AuctionExtensions.Add(new AuctionExtension
            {
                Id = Guid.NewGuid(),
                AuctionId = auctionId,
                TriggerBidId = bid.Id,
                SequenceNo = state.ExtensionCount,
                PreviousEndAt = previousEnd,
                NewEndAt = state.ScheduledEndAt,
                ExtendedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var response = new PlaceBidResponse(
            bid.Id, auctionId, bidSeq, amount, now, amount, state.BidCount, state.ScheduledEndAt, state.ExtensionCount);

        _dbContext.IdempotencyKeys.Add(new IdempotencyKey
        {
            Id = Guid.NewGuid(),
            Key = key,
            Scope = IdempotencyScope.Bid,
            RequestHash = requestHash,
            ResponseStatus = 201,
            ResponseBody = JsonSerializer.Serialize(response, JsonOptions),
            ExpiresAt = now.Add(IdempotencyLifetime),
            CreatedAt = now,
            UpdatedAt = now
        });

        _dbContext.AddAuditLog(
            AuditAction.BidPlace, "Bid", bid.Id.ToString(),
            oldValues: oldValues,
            newValues: new
            {
                auctionId,
                bidId = bid.Id,
                bidSeq,
                amount,
                currentPriceAmount = state.CurrentPriceAmount,
                bidCount = state.BidCount,
                scheduledEndAt = state.ScheduledEndAt,
                extensionCount = state.ExtensionCount
            },
            userId: actor.UserId.ToString(),
            ipAddress: actor.ClientIp,
            traceId: actor.TraceId);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another bid, or the same Idempotency-Key, was saved first.
            throw new DomainException(
                "CONCURRENT_UPDATE", "The auction changed while the bid was being saved. Reload it and try again.", 409);
        }

        return response;
    }

    // ------------------------------------------------------------------ Helpers

    private async Task<IReadOnlyDictionary<Guid, UserPublicInfo>> TryGetNamesAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, UserPublicInfo>();

        try
        {
            return await _users.GetPublicInfosAsync(userIds, cancellationToken);
        }
        catch (DomainException ex) when (ex.StatusCode >= 500)
        {
            // Names are display data only: the history is still returned (the failure is logged by the client).
            return new Dictionary<Guid, UserPublicInfo>();
        }
    }

    private static string HashRequest(Guid auctionId, Guid userId, decimal amount)
    {
        var text = $"{auctionId:N}|{userId:N}|{amount.ToString("0.00", CultureInfo.InvariantCulture)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static PlaceBidResponse? DeserializeResponse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonSerializer.Deserialize<PlaceBidResponse>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}