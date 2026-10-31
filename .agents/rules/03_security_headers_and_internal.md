# 03 — SECURITY HEADERS, INTERNAL API & IDENTITY (AUCTION SERVICE)

## 1. Controller Segregation
* `Auction.Api/Controllers/Public/`: Serves public auction and bidding endpoints (`AuctionsController`, `BidsController`).
* `Auction.Api/Controllers/Internal/`: Serves internal automated operations (e.g. cron-triggered auction settlement).

## 2. Internal Service Security
* All internal endpoints protected by `InternalServiceAuthMiddleware` with `X-Internal-Service-Key`.

## 3. Trusted Identity Extraction & Bidder Validation
* Never trust client query parameters for `bidderId`.
* Bidding operations (`POST /api/v1/auctions/{id}/bids`) must extract bidder identity from `this.GetUserId()`.
* Prohibit bidding on behalf of other users or bidding on own auction.
