# 05 — ERROR HANDLING & ENUM BOUNDARY (AUCTION SERVICE)

## 1. 100% English & Sentence Case
* Error messages must be 100% English sentence case.
* Capitalize only the first letter. Keep technical acronyms uppercase (`ID`, `URL`, `UUID`).
* Examples: `"Auction has already ended or is not active."`, `"Bid amount must be greater than current highest bid."`, `"Auction not found with the specified ID."`

## 2. SCREAMING_SNAKE_CASE Error Codes
* Error codes must be uppercase with underscores. No `ERR_` prefixes.
* Use `VAL_` prefix for validation errors.
* Examples: `AUCTION_NOT_FOUND`, `AUCTION_ENDED`, `BID_AMOUNT_TOO_LOW`, `VAL_INVALID_INPUT`, `UNAUTHORIZED`, `FORBIDDEN`.

## 3. RFC 9110 ProblemDetails Structure
* Handled by `GlobalExceptionHandlerMiddleware.cs`: returns `application/problem+json` containing `type`, `title`, `status`, `errorCode`, `detail`, `traceId`.

## 4. Enum Persistence & Boundary Serialization
* In PostgreSQL (`auction_db`): 100% stored as `integer` (int32). Prohibit `HasConversion<string>()`.
* Enums: `AuctionStatus` (`Scheduled = 0`, `Running = 1`, `Ended = 2`, `Cancelled = 3`).
* REST Outbound: 100% serialized as `PascalCase` strings (`"Scheduled"`, `"Running"`, `"Ended"`).
* REST Inbound: Handled via `PascalCaseJsonStringEnumConverter(allowIntegerValues: false)`.
