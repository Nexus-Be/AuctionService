# feat(auction): core auction and bid APIs, lifecycle, winner offers, settlement and realtime

Branch: `feature/auction-bid-settlement-apis`

## Summary

Before this branch the service only had 3 read endpoints (list auctions, get auction, list bids). This branch adds the write flows (create, edit, cancel, recreate, place a bid), seller/bidder/admin read endpoints, the settlement and winner-offer endpoints used by Commerce and bidders, internal endpoints for Catalog, admin endpoints for anti-sniping config, a SignalR hub for realtime updates, and an auction lifecycle service/worker. The service now calls Catalog and User over REST (new `ICatalogClient` and `IUserClient`).

## New endpoints (21 REST + 1 SignalR hub)

### Seller
| Method | Path | Purpose |
|---|---|---|
| POST | `/api/v1/auctions` | Seller creates an auction. The seller is checked with the User service, the SKU with Catalog, and the product must not already have an open auction. The new auction is `Scheduled` and uses the anti-sniping policy `DEFAULT_AUCTION_POLICY`. |
| PUT | `/api/v1/auctions/{id}` | Seller edits their own auction before it starts. |
| DELETE | `/api/v1/auctions/{id}/cancel` | Seller cancels their own auction before it starts (reason optional). The product is released. |
| POST | `/api/v1/auctions/{id}/recreate` | Seller creates a new auction for the product of one of their `Failed` auctions, with new prices and schedule. The new auction records the source in `RecreatedFromAuctionId`. |
| GET | `/api/v1/seller/auctions` | Seller lists their own auctions (newest start first) with an optional `status` filter. |
| GET | `/api/v1/auctions/{id}/status-history` | Paged status changes of an auction (newest first). Only the auction's seller, an admin or a staff member can view it. |

### Bidding
| Method | Path | Purpose |
|---|---|---|
| POST | `/api/v1/auctions/{auctionId}/bids` | Place a bid. Requires `Idempotency-Key` (same key and body returns the first result, different body returns 422) and `If-Match` (auction row version, mismatch returns 409). A seller cannot bid on their own auction. Checks that the auction is running, the minimum bid and bid eligibility (User service). It marks the previous leader as outbid, updates the current price, applies the anti-sniping extension, stores the idempotency record and audit log, and broadcasts `BidPlaced` after the save. The response includes `PreviousPriceAmount` and the new `RowVersion`. |
| GET | `/api/v1/me/bids` | List the caller's own bids across auctions, with the auction name and status. |

### Result and settlement
| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/auctions/{id}/result` | Winner, winning bid and final price of an auction. Returns 404 `AUCTION_RESULT_NOT_AVAILABLE` until the result is determined. |
| GET | `/api/v1/auctions/{id}/settlement` | For Commerce: settlement info used to create an order (product, SKU, seller, winner, winning bid, final price, payment deadline, settlement and auction status). |
| GET | `/api/v1/auction-settlements` | For Commerce: paged list of auctions with a determined winner, oldest first, with optional `status` and `determinedFrom` filters, so Commerce can scan and create orders. |
| POST | `/api/v1/auctions/{id}/settlement/payment-status` | For Commerce: payment result callback. `eventId` is the idempotency key. The winner, amount and order are checked against the auction result. `Paid` moves the auction to `Settled`; other statuses move it to `Failed`. Writes status history and audit and broadcasts `AuctionStatusChanged`. |

### Winner offers
| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/me/auction-offers` | The caller's winner offers (newest first) with an optional `status` filter. Each row has `IsExpired`. |
| POST | `/api/v1/auctions/{auctionId}/winner-offers/{offerId}/accept` | The invited bidder accepts becoming the winner. The auction result and settlement are reassigned to this bidder (price of their own bid, new 24-hour payment deadline, old order id cleared). The auction stays `Ended`. |
| POST | `/api/v1/auctions/{auctionId}/winner-offers/{offerId}/decline` | The invited bidder declines (reason optional). The offer becomes `Declined`, the auction moves from `Ended` to `Failed`, the product is released, and `AuctionStatusChanged` is broadcast. |

### Internal (Catalog)
| Method | Path | Purpose |
|---|---|---|
| GET | `/internal/v1/auctions/status?productId=` | Latest auction status of a product with `isOpen` (Draft, Scheduled, Running, Ended) and `isSold` (Settled). |
| GET | `/internal/v1/auctions/{auctionId}/summary` | Start price, bid step, current price, leading bidder (id and masked name), bid count, start/end time and extension count of an auction. |

### Admin
| Method | Path | Purpose |
|---|---|---|
| POST | `/api/v1/admin/auctions/{id}/force-cancel` | Admin cancels an auction that has not ended (Draft, Scheduled or Running, including one that already has bids). A reason is required. Releases the product, writes history and audit (marked forced) and broadcasts `AuctionCancelled`. |
| GET | `/api/v1/admin/auction-configs` | List the active auction configurations. |
| GET | `/api/v1/admin/auction-configs/{configKey}/history` | Version history of a config (for example `DEFAULT_AUCTION_POLICY`), newest first, with the fields changed against the previous version. |
| PUT | `/api/v1/admin/auction-configs` | Publish a new version of the anti-sniping settings (extension minutes 0 to 60, max extension count 0 to 20). Only auctions created afterwards use the new values. Two admins updating at the same time: the later one gets 409. |

### Realtime (SignalR hub `/hubs/auctions`)
Clients call `JoinAuction(auctionId)` and `LeaveAuction(auctionId)` (max 20 auctions per connection, the auction must exist). The server pushes `BidPlaced`, `AuctionCancelled` and `AuctionStatusChanged`. Bids are still placed only through `POST /api/v1/auctions/{auctionId}/bids`. `BidPlaced` carries the new `RowVersion` so the client can use it for the next `If-Match`.

## Existing endpoints changed
- `GET /api/v1/auctions`: new optional `status` filter; returns `AuctionSummaryDto` (status, current price, bid count, product name and image from Catalog).
- `GET /api/v1/auctions/{id}`: returns `AuctionDetailDto` (Catalog description, images and specifications, schedule, extension info, `RecreatedFromAuctionId`, `RowVersion`).
- `GET /api/v1/auctions/{auctionId}/bids`: reshaped `BidDto`, read from bid history, bidder names masked through the User service.

## Auction lifecycle (service + worker)
`AuctionLifecycleService` and `AuctionLifecycleWorker` (every `AuctionLifecycle:IntervalSeconds`, default 5; `AuctionLifecycle:Enabled`) implement:
- `Scheduled` to `Running` when the start time is reached.
- `Running` to `Ended` when the end time (including anti-sniping extensions) is reached. The winner is the highest valid bid (earliest wins a tie). This creates the `AuctionResult` and a `PendingPayment` settlement with a 24-hour deadline and keeps the product locked.
- `Running` to `Failed` when there is no valid bid; the product is released.
- Each transition writes status history and audit and broadcasts `AuctionStatusChanged`. `RowVersion` prevents double processing.

## Files changed

### Api
| File | Change |
|---|---|
| `Controllers/Public/AuctionsController.cs` | Adds the auction, seller, result, settlement, winner-offer, internal and force-cancel endpoints listed above; adds `status` filter to the list. |
| `Controllers/Public/BidsController.cs` | Adds `POST` place bid and `GET /api/v1/me/bids`. |
| `Controllers/Public/AuctionConfigsController.cs` (new) | Admin endpoints for anti-sniping config. |
| `Hubs/AuctionHub.cs`, `Hubs/SignalRAuctionNotifier.cs` (new) | SignalR hub and the notifier that pushes events to viewers of an auction. |
| `Workers/AuctionLifecycleWorker.cs` (new) | Background loop that starts and ends auctions on time. |
| `Program.cs` | Registers SignalR, CORS, HttpClients for Catalog and User (timeouts, internal service key header), registers `AuctionLifecycleWorker` as a hosted service, maps the hub, refuses `BypassUserCheck` in Production, logs host/db and logs migration failures as errors. |

### Application
| File | Change |
|---|---|
| `Services/Auction/*` (service, interface, DTOs, requests, responses) | Implements create, update, cancel, force-cancel, recreate, list, detail, seller list, status history, result, settlement, settlement list, payment-status callback, internal summary/status, winner offer accept/decline/list. `AuctionDto` is replaced by `AuctionSummaryDto` and `AuctionDetailDto`. |
| `Services/Bid/*` | Implements place bid, list bids and list my bids; adds `PlaceBidResponse`, `MyBidDto`, `BidPlacedMessage` (with `RowVersion`). |
| `Services/AuctionConfig/*` (new) | Config list, history and versioned update. |
| `Services/AuctionLifecycle/*` (new) | Start and end logic used by the worker. |
| `Interfaces/ICatalogClient.cs`, `IUserClient.cs`, `IAuctionNotifier.cs` (new), `IAuctionDbContext.cs` | Contracts for Catalog, User and realtime notification; adds `AuctionWinnerOffers` DbSet. |
| `Common/Models/ActorContext.cs`, `Common/UserEligibilityErrors.cs` (new) | Caller info for ownership and audit; maps User service reason codes to Auction error codes. |
| `DependencyInjection.cs` | Registers `TimeProvider`, config service and lifecycle service. |

### Domain
| File | Change |
|---|---|
| `Rules/AuctionRules.cs`, `Rules/BidRules.cs` (new) | Stateless business rules: prices, schedule, edit/cancel timing, bid amount, idempotency key, row version, bid eligibility and minimum bid. |
| `Entities/AuctionWinnerOffer.cs` (new), `Entities/Auction.cs` | New winner offer entity and navigation from `Auction`. |
| `Entities/AuctionSettlement.cs` | Adds `WinnerId`, `WinningBidId` and `Status`. |
| `Enums/AuctionSettlementStatus.cs`, `Enums/AuctionWinnerOfferStatus.cs` (new), `Enums/AuditAction.cs` | New enums; `AuditAction` adds `WinnerOfferAccept`, `WinnerOfferDecline`, `AuctionStart`, `AuctionEnd`. |

### Infrastructure
| File | Change |
|---|---|
| `Clients/CatalogClient.cs`, `Clients/UserClient.cs` (new) | REST clients for Catalog and User with timeouts and error mapping. |
| `Persistence/AppDbContext.cs`, `Configurations/AuctionWinnerOfferConfiguration.cs` (new), `Configurations/AuctionSettlementConfiguration.cs`, `Configurations/AuctionConfigConfiguration.cs` | Winner offer table mapping, settlement mapping for the new columns, and `IsActive` as a concurrency token on config. |
| `Persistence/Migrations/20261105142709_AddAuctionWinnerOfferAndSettlementFlow` (+ designer), `AppDbContextModelSnapshot.cs` | Creates `AuctionWinnerOffers`; adds settlement `Status`, `WinnerId`, `WinningBidId`, indexes and foreign keys. |
| `Persistence/Seeds/AuctionSeedData.cs` | Adds two sample auctions for local testing (a Scheduled one for edit/cancel and a Failed one for recreate) and seed values for the new settlement columns. The migration does not insert the two sample auctions. |

## Notes
- No code in this branch creates `AuctionWinnerOffer` rows and there is no automatic offer expiry. The accept, decline and list endpoints only work on existing offer rows.
- The Commerce and Catalog endpoints (`/internal/v1/...`, settlement, `auction-settlements`, `payment-status`) have no role check in the controller; they rely on the API Gateway. There is no HMAC, service key or JWT check on the callback.
- There is no test project in the repository, so there are no automated tests for these flows.