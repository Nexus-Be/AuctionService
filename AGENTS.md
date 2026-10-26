<!-- gitnexus:start -->
# GitNexus — Code Intelligence
This project is indexed by GitNexus as **AuctionService** (445 symbols, 565 relationships, 4 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.
> Index stale? Run `node .gitnexus/run.cjs analyze` from the project root — it auto-selects an available runner. No `.gitnexus/run.cjs` yet? `npx gitnexus analyze` (npm 11 crash → `npm i -g gitnexus`; #1939).
## Always Do
- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows. For regression review, compare against the default branch: `detect_changes({scope: "compare", base_ref: "main"})`.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `query({search_query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `context({name: "symbolName"})`.
- For security review, `explain({target: "fileOrSymbol"})` lists taint findings (source→sink flows; needs `analyze --pdg`).
## Never Do
- NEVER edit a function, class, or method without first running `impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `rename` which understands the call graph.
- NEVER commit changes without running `detect_changes()` to check affected scope.
## Resources
| Resource | Use for |
|----------|---------|
| `gitnexus://repo/AuctionService/context` | Codebase overview, check index freshness |
| `gitnexus://repo/AuctionService/clusters` | All functional areas |
| `gitnexus://repo/AuctionService/processes` | All execution flows |
| `gitnexus://repo/AuctionService/process/{name}` | Step-by-step execution trace |
## CLI
| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |
<!-- gitnexus:end -->
> **Trạng thái triển khai (2026-10-24):** ❌ Chưa có bề mặt dịch vụ — 1 endpoint `GET /health` (`Auction.Api/Program.cs:58`), **0 rpc gRPC**, **0 controller**, **0 publisher/0 consumer**, **0 test project**. Đọc `specs/` kèm banner từng file; mọi mục ❌ là **đặc tả mục tiêu, chưa triển khai**.
> **Nguồn sự thật:** code trong repo này (`file:line` ghi kèm trong từng bảng).
> **Cập nhật lần cuối:** 2026-10-24. (`AGENTS.md` và `CLAUDE.md` trong repo này là **bản sao byte-identical**, sửa file này phải sửa file kia.)
---
# QUY CHUẨN VẬN HÀNH DÀNH CHO AI CODING AGENTS (AUCTION SERVICE)
---
## 🛑 NGUYÊN TẮC 1: BẮT BUỘC LẬP KẾ HOẠCH TRƯỚC KHI HÀNH ĐỘNG (PLAN-FIRST RULE)
Mọi AI Agent khi nhận nhiệm vụ kỹ thuật tại AuctionService **BẮT BUỘC PHẢI LẬP VÀ TRÌNH BÀY PLAN** trước khi thực hiện bất kỳ hành động sửa đổi mã nguồn, thêm/xóa file, refactor, tạo migration database hay thay đổi cấu hình nào.
Một Plan chuẩn bắt buộc phải có:
1. **Mục tiêu (Objective)**: Tóm tắt chính xác điều cần làm.
2. **Context & Files cần đọc**: Liệt kê các file nội bộ của AuctionService cần đọc theo đúng ma trận đọc file tại Nguyên tắc 3.
3. **Phân tích rủi ro & Phạm vi ảnh hưởng (Impact Analysis)**:
   - Dùng GitNexus `impact` đánh giá blast radius các symbol bị ảnh hưởng.
   - Cảnh báo rõ nếu mức độ rủi ro là HIGH hoặc CRITICAL.
4. **Checklist các bước thực hiện (Step-by-Step Implementation)**: Từng bước logic, chi tiết.
5. **Kế hoạch kiểm chứng (Verification)**: `dotnet build` tương ứng (⚠️ `dotnet test` hiện **không chạy được** vì repo không có test project — xem Nguyên tắc 6).
---
## 🧭 NGUYÊN TẮC 2: QUY TẮC PHẠM VI NỘI BỘ (INWARD-ONLY / SELF-CONTAINED)
AuctionService là một Git Repository độc lập (`AuctionService.git`):
- **TUYỆT ĐỐI CHỈ ĐƯỢC trỏ tới các tài nguyên nội bộ của AuctionService**.
- **TUYỆT ĐỐI KHÔNG ĐƯỢC trỏ ngược ra ngoài Root** (`../../../docs/...`) hay sang các Service khác (`../../Catalog/...`).
- Toàn bộ tài liệu, hướng dẫn và cấu hình trong repo này phải 100% tự cung tự cấp (Self-contained).
- **Ngoại lệ duy nhất được phép**: dòng `Báo cáo kiểm chứng: trong banner trạng thái, do bắt buộc. Đây là tham chiếu chỉ-đọc, không phải phụ thuộc kỹ thuật.
---
## 📚 NGUYÊN TẮC 3: DANH MỤC FILE NỘI BỘ BẮT BUỘC PHẢI ĐỌC TẠI AUCTION SERVICE
Khi bắt đầu làm việc tại AuctionService, AI Agent **BẮT BUỘC PHẢI ĐỌC** các file nội bộ sau theo thứ tự:
| # | File | Nội dung phải nắm | Trạng thái |
|---|---|---|---|
| 1 | `specs/00_AGENT_MISSION_GUARDRAILS.md` | Bounded context phiên đấu giá, đặt giá, chốt phiên; port `8005` (Docker) / `5230`-`7202` (local); DB `5437` (`auction_db`). | ❌ |
| 2 | `specs/01_CLEAN_ARCHITECTURE_LAYERS.md` | Cấu trúc 4 tầng Clean Architecture; xử lý đồng quy đặt giá (bidding concurrency). | ⚠️ |
| 3 | `specs/02_API_GRPC_CONTRACTS.md` | Hợp đồng gRPC Server `AuctionApi` — **5 rpc**: `ListAuctions`, `GetAuction`, `CreateAuction`, `PlaceBid`, `ListBids`. | ❌ 0/5 |
| 4 | `specs/03_EVENT_MESSAGING.md` | Sự kiện RabbitMQ: `BidPlacedEvent`, `AuctionEndedEvent`, `AuctionCancelledEvent` (MassTransit **8.3.0**). | ❌ |
| 5 | `specs/04_DATA_PERSISTENCE.md` | Schema PostgreSQL thật: 13 thực thể + 3 bảng outbox = **16 bảng PascalCase** (`Auctions`, `AuctionStates`, `Bids`, `BidHistories`, `AuctionResults`, `AuctionSettlements`, `AuditLogs`…); **không** có bảng `AuctionWinners`. | ⚠️ |
| 6 | `specs/05_DEV_VERIFICATION_CHECKLIST.md` | Lệnh build & test kiểm chứng service. | ❌ |
---
## 🔒 NGUYÊN TẮC 4: RÀO CHẮN BẤT BIẾN TẠI AUCTION SERVICE
1. **TUYỆT ĐỐI KHÔNG DÙNG REPOSITORY PATTERN BỌC NGOÀI EF CORE**: Sử dụng trực tiếp `AppDbContext` (hiện thực `IAuctionDbContext`) và `DbSet<T>` trong tầng Application. Cấm tạo `IAuctionRepository`, `IBidRepository`, `UnitOfWork`. (Đã kiểm chứng: `*Repository|UnitOfWork` = 0 kết quả.)
2. **ĐẢM BẢO TÍNH TOÀN VẸN VÀ THỨ TỰ ĐẶT GIÁ**: Xử lý concurrency an toàn khi nhiều user cùng đặt giá. `RowVersion` đã khai báo trên `AuctionState` (`AuctionState.cs:17`, `IsConcurrencyToken()` tại `AuctionStateConfiguration.cs:14`) nhưng **chưa từng được kiểm tra** vì `SaveChangesAsync` có 0 lời gọi.
3. **CẤU HÌNH PORT LÀ BẤT BIẾN, NHƯNG PHẢI NÓI ĐÚNG MÔI TRƯỜNG**: container `8005` (`Dockerfile:3-4`, `docker-compose.yml:37`); chạy local `5230`/`7202` (`Auction.Api/Properties/launchSettings.json:17,27`); DB `5437`.
4. **HỢP ĐỒNG PROTOBUF DUY NHẤT — NHƯNG CHƯA TỒN TẠI TRONG REPO NÀY**: hiện **0 file `.proto`**, **0 package `Grpc.AspNetCore`**. Không được viết tài liệu/PR tham chiếu `Auction.Api/Services/AuctionGrpcService.cs` hay `Auction.Api/Contracts/shop.proto` như thể chúng đã có.
5. **KHÔNG ĐƯỢC COI `/health` LÀ BẰNG CHỨNG "SERVICE HOẠT ĐỘNG"**: endpoint trả `Healthy` vô điều kiện, không kiểm tra DB/RabbitMQ (`Program.cs:58`).
6. **CẤM TỰ Ý SỬA ĐỔI BIẾN MÔI TRƯỜNG, SECRET VÀ KEY BÍ MẬT (MANDATORY HUMAN-IN-THE-LOOP FOR SECRETS)**:
   - **Tuyệt đối CẤM mọi AI Coding Agent tự ý sửa đổi, thay thế, xóa hoặc ghi đè các biến môi trường (`.env`, `.env.*`), Connection Strings, API Keys, JWT Secrets, chứng chỉ hoặc bất kỳ thông tin bí mật (credentials/secrets) nào trong toàn bộ hệ thống.**
   - **Bắt buộc phải xin ý kiến và nhận được sự đồng ý rõ ràng (explicit approval) từ người dùng trước khi thực hiện bất kỳ thay đổi nào liên quan đến biến môi trường hoặc tài khoản bí mật.**
   - Khi phát hiện cấu hình môi trường, connection string hoặc secret key có vấn đề hoặc cần cập nhật, Agent **CHỈ ĐƯỢC PHÉP báo cáo hiện trạng, đề xuất phương án và chờ người dùng xác nhận**, tuyệt đối không tự ý can thiệp.
---
## 💬 NGUYÊN TẮC 5: QUY CHUẨN THÔNG BÁO LỖI TOÀN HỆ THỐNG (MANDATORY ERROR RULES)
Tất cả các thông báo lỗi (Error Messages, Exceptions, ProblemDetails, Validation Errors, Response Details) **BẮT BUỘC** phải tuân theo 3 nguyên tắc sau:
1. **Ngôn ngữ (100% English)**: Toàn bộ thông báo lỗi phải được viết bằng tiếng Anh. Tuyệt đối không dùng tiếng Việt trong chuỗi thông báo lỗi trả về cho client.
2. **Quy cách viết hoa (Sentence case)**: Viết hoa duy nhất chữ cái đầu câu, các từ tiếp theo viết thường (ngoại trừ tên riêng, từ viết tắt hoặc định danh kỹ thuật như `ID`, `OTP`, `JWT`, `URL`, `UUID`, `Email`...).
   - ✅ Đúng: `"Auction has already ended or is not active."`
   - ✅ Đúng: `"Bid amount must be higher than current highest bid plus bid increment."`
   - ❌ Sai: `"Phiên đấu giá đã kết thúc"` (Không dùng tiếng Việt)
   - ❌ Sai: `"auction not found"` (Chữ cái đầu không viết hoa)
   - ❌ Sai: `"AUCTION NOT FOUND"` (Cấm viết hoa toàn bộ)
   - ❌ Sai: `"Auction Not Found Provided"` (Không dùng Title Case)
3. **Mã lỗi (Error Codes — SCREAMING_SNAKE_CASE)**: Mã lỗi luôn viết hoa toàn bộ và nối bằng dấu gạch dưới:
   - **Mã đã có thật trong code** (middleware `GlobalExceptionHandlerMiddleware.cs:36-63`): `NOT_FOUND`, `BUSINESS_RULE_VIOLATION`, `DOMAIN_ERROR`, `VAL_INVALID_ARGUMENT`, `INTERNAL_SERVER_ERROR`.
   - **Mã đặc tả mục tiêu, chưa triển khai**: `AUCTION_NOT_FOUND`, `AUCTION_ENDED`, `BID_AMOUNT_TOO_LOW`, `UNAUTHORIZED`, `FORBIDDEN`.
---
## 🎯 NGUYÊN TẮC 6: ĐIỀU KIỆN HOÀN THÀNH NHIỆM VỤ (DEFINITION OF DONE)
Một task tại AuctionService chỉ hoàn tất khi:
- [ ] Đã lập Plan chi tiết trước khi thực hiện.
- [ ] Không chứa bất kỳ liên kết trỏ ngược ra ngoài AuctionService.
- [ ] `dotnet build Auction.sln -c Release` thành công 100% (0 error). ⚠️ Tên file là **`Auction.sln`**, không phải `AuctionService.sln`.
- [ ] ~~`dotnet test Auction.sln -c Release` vượt qua 100% các bài test~~ — **chưa áp dụng được**: repo có **0 test project**, `dotnet test` không có gì để chạy. Khi thêm test project đầu tiên thì bắt buộc bật lại điều kiện này.
- [ ] Trạng thái Git sạch, không commit file rác (`bin/`, `obj/`, `.vs/`, `.idea/`, `.env`).
---
## 🧪 NGUYÊN TẮC 7: HIỆN TRẠNG ĐÃ KIỂM CHỨNG (BẮT BUỘC ĐỌC TRƯỚC KHI CODE)
> Số liệu đo bằng `find`/`wc`/`grep` trực tiếp trên repo (ngày 2026-10-24): **47 file `.cs` / 1.182 dòng** (không tính `Migrations/`), chia theo tầng — `.Api` 2 file/155 dòng, `.Application` 8/198, `.Domain` 21/327, `.Infrastructure` 16/502.
1. **Bề mặt dịch vụ**: chỉ có `GET /health` — không gRPC, không controller, không middleware nghiệp vụ. Bất kỳ ai nói "service đã chạy" phải chỉ ra được endpoint cụ thể.
2. **Tầng ghi dữ liệu**: `SaveChangesAsync` khai báo tại `Auction.Application/Interfaces/IAuctionDbContext.cs:24` nhưng **0 lời gọi** toàn repo ⇒ hệ thống hiện **read-only**. `throw new` toàn repo = **0** ⇒ 3 exception domain trong `Auction.Domain/Exceptions/DomainException.cs` chưa bao giờ được ném.
3. **Use case**: chỉ có 3 hàm đọc — `AuctionService.GetByIdAsync` (`AuctionService.cs:20`), `AuctionService.ListActiveAuctionsAsync` (`AuctionService.cs:39`, **không lọc `Status`** dù tên là Active), `BidService.ListBidsByAuctionIdAsync` (`BidService.cs:20`). Hai service đã đăng ký DI (`Auction.Application/DependencyInjection.cs:11-12`) nhưng **0 call site** — không endpoint nào gọi chúng.
4. **Lỗi phân trang đã xác nhận**: `AuctionService.cs:44` gọi `.Skip((page - 1) * pageSize)` mà **không validate** `page`/`pageSize` ⇒ `page = 0` ném `ArgumentException` (được middleware dịch thành 400 `VAL_INVALID_ARGUMENT`).
5. **gRPC không thể bật ngay**: `Program.cs:10-16` đặt `HttpProtocols.Http1AndHttp2` trên endpoint **cleartext**; không có ALPN ⇒ thêm `MapGrpcService` cũng fail. Phải tách endpoint HTTP/2 (h2c) hoặc bật TLS trước.
6. **Messaging**: MassTransit 8.3.0 + RabbitMQ + EF Outbox đã đăng ký (`Auction.Infrastructure/DependencyInjection.cs:23-48`) nhưng `ConfigureEndpoints` không có consumer ⇒ 0 message, 0 event class.
7. **Observability**: 0 package OpenTelemetry, 0 đọc `x-trace-id`. `Otel:Endpoint` trong `appsettings.json:18-20` và `OTEL_EXPORTER_OTLP_ENDPOINT` trong `docker-compose.yml:47` **không được code nào đọc**.
8. **Bảo mật**: 0 `AddAuthentication`/`JwtBearer` ⇒ mọi "user id" hiện phải tin từ client. Khi thêm endpoint phải giải quyết auth trước, không tự chế header.
9. **Mã chết đã xác nhận**: lớp `AuctionDbContext : AppDbContext` (`AppDbContext.cs:41`) 0 tham chiếu; 3 entity `BidInvalidation`/`AuditLog`/`IdempotencyKey` chỉ có `DbSet` + configuration, 0 nơi dùng.
**Quy tắc rút ra:**
- Tài liệu phải có banner trạng thái ✅/⚠️/❌ + nguồn sự thật + ngày, theo.
- Không viết tài liệu như thể đã có gRPC/event/test khi code chưa có.
- Code nghiệp vụ mới (CreateAuction, PlaceBid, CloseAuction) chỉ nên bắt đầu sau khi chốt: tên event, hợp đồng `.proto`, và cơ chế auth.