---
name: new-feature-endpoint
description: Add or change a REST endpoint in AuctionService (port 8005). Use when adding a controller action, Application feature service, request or response contract, database field, or when changing an existing AuctionService endpoint.
---

# New Endpoint in AuctionService

> Scope: this repository only. Never reference the parent workspace, its documentation, or a sibling service.
> Synced with the root `new-feature-endpoint` skill on 2026-10-31.

## Local facts

- Service: AuctionService
- HTTP port: 8005
- Database: `auction_db` on host port `5437`
- Solution: `Auction.sln`
- Tests: No test project in this repository. Record the gap; do not claim coverage.
- DbContext contract: `IAuctionDbContext`
- Controllers: `Auction.Api/Controllers/Public/`
- Application features: `Auction.Application/Services/{Feature}/`
- API contract spec: `specs/02_API_REST_CONTRACTS.md`

## Read first

- `AGENTS.md`
- `.agents/rules/01_clean_architecture_layers.md` through `.agents/rules/06_database_and_migrations.md`
- `specs/00_AGENT_MISSION_GUARDRAILS.md` through `specs/05_DEV_VERIFICATION_CHECKLIST.md`
- The nearest existing controller, feature service, request/response records, and tests.

## Checklist

### 0. Preflight

- [ ] Read the files above.
- [ ] Run GitNexus impact for every symbol to change and record the risk.
- [ ] Confirm the contract: method, route, RBAC, ownership, pagination, idempotency, database change.
- [ ] Create `.agents/plan/PLAN-YYYYMMDD-HHMM-<NAME>.md`.
- [ ] Stop and ask if the contract is ambiguous.

### 1. Contract

- [ ] Route is `/api/v1/<plural>`; only `/api/v1/me` and `/api/v1/auth` may be singular.
- [ ] One route attribute per action; no alias.
- [ ] Admin route is namespaced under `/api/v1/admin/<domain>`.
- [ ] Request and response records are defined in the feature folder.
- [ ] Status codes and error codes are listed.
- [ ] Error codes are SCREAMING_SNAKE_CASE; messages are English sentence case.
- [ ] Enums are documented with integer database value and PascalCase API value.
- [ ] List endpoints use `page` and `pageSize` and return `PagedData<T>`.
- [ ] Internal API stays off: no `/api/v1/internal`, no `X-Internal-Service-Key`.

### 2. Persistence

- [ ] Entity inherits `BaseEntity<Guid>` or `AuditableEntity<Guid>`.
- [ ] Enum values are explicit integers from 0; no `HasConversion<string>()`.
- [ ] Configuration has keys, indexes, soft delete filter, and `HasPrecision(18, 2)` for money.
- [ ] Migration is additive, timestamped, and the snapshot and designer are synchronized.
- [ ] Seeds use deterministic GUIDs and BCrypt hashes.
- [ ] No DROP, TRUNCATE, or RENAME without approval.
- [ ] Skip this phase with a written reason when no schema changes.

### 3. Application

- [ ] Files live in ``Auction.Application/Services/{Feature}/`` as `I{Feature}Service.cs`, `{Feature}Service.cs`, `{Feature}Requests.cs`, `{Feature}Responses.cs`, optional `{Feature}Dtos.cs`.
- [ ] Service injects ``IAuctionDbContext`` directly; no repository and no unit of work.
- [ ] Reads use `AsNoTracking()`; multi-write operations use one transaction.
- [ ] Sensitive mutations call `AddAuditLog(...)`.
- [ ] Pagination runs in SQL with `Skip`/`Take` and `CountAsync`.
- [ ] Errors use stable codes and English messages.
- [ ] Cross-service interfaces live in `Application/Interfaces`, implementations in `Infrastructure/Services`, registration in `Infrastructure/DependencyInjection.cs`.

### 4. Api

- [ ] Controller lives in `Auction.Api/Controllers/Public/` and is named `{Feature}Controller`.
- [ ] `[ApiController]`, one `[Route]`, one route attribute per action, one-line XML `<summary>`.
- [ ] Only `I{Feature}Service` is injected.
- [ ] Identity comes from `this.TryGetUserId(...)`, ownership from `this.EnsureOwnership(...)`.
- [ ] DTOs are returned directly; delete returns `NoContent()`.
- [ ] No `ResultFilter`, no manual envelope, no business branching in the controller.

### 5. Tests

- [ ] Happy path and every error code are covered.
- [ ] Pagination boundaries and ownership `401`/`403` are covered.
- [ ] Enum serialization and Swagger parameters are covered.
- [ ] Run `dotnet test Auction.sln -c Release`.

### 6. Verify

- [ ] `dotnet build Auction.sln -c Release` has 0 errors.
- [ ] Tests pass 100%, or the missing test project is recorded.
- [ ] Fetch `/swagger/v1/swagger.json` and confirm the new route and parameters are present.
- [ ] Confirm `git status` has no build output or secret.
- [ ] Run GitNexus `detect_changes` before commit.

### 7. Handoff

- [ ] Tick the session plan.
- [ ] Append the session summary to `.agents/history/YYYY-MM-DD_session_summary.md`.
- [ ] Fill the contract handoff block (service, endpoint, controller, application, entity, RBAC, request, response, errors, enums, tests, build) and give it to the repository owner.
- [ ] Do not link outward from this repository.

## Variant

Auction owns auction sessions and bids. `BidderId` comes from `HttpContext`, never from the request. Only the seller who owns a session, or an admin/staff caller, may change or cancel it.

## Commands

```bash
dotnet build Auction.sln -c Release
dotnet test Auction.sln -c Release
```

## Done when

- [ ] Build passes with 0 errors.
- [ ] Tests pass 100%, or the gap is recorded.
- [ ] No forbidden pattern is present.
- [ ] The repository owner has the contract handoff block.
