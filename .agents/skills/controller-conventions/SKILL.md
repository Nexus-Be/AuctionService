---
name: controller-conventions
description: Canonical standards for ASP.NET Core thin controllers in AuctionService (port 8005). Covers boundary validation, Auction.Application shared request/response DTOs, English sentence-case error messages, bidder identity extraction, and the reference controller blueprint.
---

# Controller Conventions & Standards in AuctionService

> Scope: AuctionService repository only. Never reference parent workspace paths, root documentation, or sibling service directories.

This skill defines the invariant structural and boundary rules for all REST API controllers in `Auction.Api`.

Controllers serve strictly as **HTTP Transport Boundary Adapters**. They bind incoming HTTP protocol details, extract verified bidder identities, invoke Auction Application use cases, and translate results into standardized HTTP responses. They must never contain business decisions, direct database queries, or manual error formatting.

---

## 1. When to Use

Consult and follow this skill whenever you:
- Create a new controller or add action methods in `Auction.Api/Controllers/Public/`.
- Review controller code changes for architectural consistency in AuctionService.
- Bind request parameters (`[FromRoute]`, `[FromQuery]`, `[FromBody]`).
- Design validation placement for incoming client payloads.
- Format outbound responses, pagination metadata, or status codes.

---

## 2. Request and Response DTO Placement (Application Layer Sharing)

To prevent duplicate wrapper classes and maintain clean domain boundaries:

- **Declare DTOs in Auction.Application**: All Request and Response records are declared directly in `Auction.Application/Services/{Feature}/{Feature}Requests.cs` and `{Feature}Responses.cs`.
- **Direct Consumption by Controller**: Controllers directly bind these Application-layer request records and return these Application-layer response records.
- **Prohibit Duplicate API DTOs**: Never create redundant DTOs in `Auction.Api` (e.g. prohibited: creating `ApiCreateBidRequest` in Api only to map into `CreateBidRequest` in Application).
- **Domain Entities Stay Private**: Never expose EF Core entities or domain aggregate roots (`Auction`, `Bid`, `AuctionItem`) through the controller. Only immutable DTO records and `PagedData<T>` leave the application boundary.

---

## 3. Validation Placement (Boundary Discipline)

Validation is concentrated exclusively at system boundaries:

- **Boundary Validation on Request Records**: Place declarative validation attributes from `System.ComponentModel.DataAnnotations` directly on Request DTO records (`[Required]`, `[Range]`, `[RegularExpression]`).
- **Automatic Model State Rejection**: Invalid inputs are intercepted automatically by ASP.NET Core model validation via `ApiValidationProblemFactory`, producing standardized RFC 9110 ProblemDetails with `errorCode = "VAL_INVALID_INPUT"`.
- **PROHIBIT Manual Validation in Controllers**: Never write ad-hoc validation checks inside controller action bodies (e.g. prohibited: `if (request.BidAmount <= 0) return BadRequest(...);`).

---

## 4. Error Message Standards (Descriptive Sentence Case)

When domain exceptions, validation failures, or auction timing guards trigger errors:

- **100% English Language**: All error messages, failure details, and exception descriptions must be written in English.
- **Sentence Case Format**:
  - Capitalize ONLY the first letter of the sentence.
  - All subsequent words must be lowercase, except technical acronyms and identifiers (`ID`, `OTP`, `JWT`, `URL`, `UUID`, `Email`, `API`, `HTTP`).
  - Prohibit ALL-CAPS messages (e.g. prohibited: `"AUCTION ENDED"`, `"BID TOO LOW"`).
  - Prohibit Title Case (e.g. prohibited: `"Auction Ended"`).
- **Contextual Precision**: The message must precisely describe what condition failed so any developer or client can diagnose the issue immediately.
  - Correct: `"Auction has already ended or is not active."`
  - Correct: `"Bid amount must be greater than current highest bid."`
  - Correct: `"Auction was not found for the specified identifier."`
- **Error Codes**: Always `SCREAMING_SNAKE_CASE` without arbitrary `ERR_` prefixes (`AUCTION_NOT_FOUND`, `AUCTION_ENDED`, `BID_AMOUNT_TOO_LOW`, `VAL_INVALID_INPUT`, `FORBIDDEN`).

---

## 5. Canonical Reference Controller Blueprint

The following structural skeleton represents the standardized pattern for controllers in `Auction.Api`:

```csharp
namespace Auction.Api.Controllers;

using System;
using System.Threading;
using System.Threading.Tasks;
using Auction.Api.Extensions;
using Auction.Api.Filters;
using Auction.Application.Common.Models;
using Auction.Application.Services.{Feature};
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/v1/{plural-resources}")]
public class {Feature}Controller : ControllerBase
{
    private readonly I{Feature}Service _service;

    public {Feature}Controller(I{Feature}Service service)
    {
        _service = service;
    }

    /// <summary>Lists active auctions with pagination.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedData<{Feature}Dto>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _service.ListAsync(page, pageSize, ct);
        return Ok(result);
    }

    /// <summary>Gets a single resource by its unique identifier.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<{Feature}Dto>> GetById([FromRoute] Guid id, CancellationToken ct)
    {
        var result = await _service.GetByIdAsync(id, ct);
        return Ok(result);
    }

    /// <summary>Creates a new resource from an Application-layer request record.</summary>
    [HttpPost]
    [RequireRole("buyer", "seller", "admin")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        [FromBody] Create{Feature}Request request,
        CancellationToken ct)
    {
        if (!this.TryGetUserId(out var userId, out var authError))
        {
            return authError;
        }

        var result = await _service.CreateAsync(userId, request, ct);
        return StatusCode(StatusCodes.Status201Created, result);
    }
}
```

---

## 6. HTTP Status & Return Conventions

- **GET Single Item**: Return `Task<ActionResult<T>>` with `Ok(dto)` (HTTP 200).
- **GET Paginated List**: Return `Task<ActionResult<PagedData<T>>>` with `Ok(pagedData)` (HTTP 200).
- **POST Creation**: Return `Task<IActionResult>` with `StatusCode(StatusCodes.Status201Created, dto)` (HTTP 201).
- **PUT / PATCH Update**: Return `Task<IActionResult>` with `Ok(dto)` (HTTP 200).
- **DELETE or Empty Operations**: Return `Task<IActionResult>` with `NoContent()` (HTTP 204, 0-byte payload).
- **No Manual Outer Envelopes**: Microservice controllers return pure payloads. Wrapping into `ApiResponse<T>` is performed by Gateway `ResponseEnvelopeMiddleware`.
- **Automatic Error Responses**: All unhandled domain exceptions bubble up to `GlobalExceptionHandlerMiddleware` to format RFC 9110 ProblemDetails.
