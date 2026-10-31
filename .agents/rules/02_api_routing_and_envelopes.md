# 02 — API ROUTING, ENVELOPES & PAGINATION (AUCTION SERVICE)

## 1. Route Conventions in AuctionService
* Collections: Lowercase plural nouns (`/api/v1/auctions`, `/api/v1/auctions/{id}`, `/api/v1/auctions/{auctionId}/bids`).
* Prohibit singular nouns (`/api/v1/auction`, `/api/v1/bid`).
* One route attribute per action. Use single-line XML `<summary>`.

## 2. Pure DTO Return & Zero ResultFilter
* Controllers return pure DTOs:
  * `Ok(auctionDto)`
  * `StatusCode(StatusCodes.Status201Created, createdAuctionDto)`
  * `Ok(bidsList)`
  * `Ok(pagedAuctions)` (for paginated auction lists)
* Zero ResultFilter: Do NOT apply `ResultFilter` or manual envelope factories in `Auction.Api`. Gateway automatically wraps raw DTOs in `ApiResponse<T>`.

## 3. Database Pagination Governance
* Database-level pagination: `.Skip((page - 1) * pageSize).Take(pageSize)` and `.CountAsync()`.
* Output: Standard `PagedData<T>` envelope containing `items` and `pagination` metadata.
