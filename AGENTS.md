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
5. **Kế hoạch kiểm chứng (Verification)**: `dotnet build` và `dotnet test` tương ứng.

---

## 🧭 NGUYÊN TẮC 2: QUY TẮC PHẠM VI NỘI BỘ (INWARD-ONLY / SELF-CONTAINED)

AuctionService là một Git Repository độc lập (`AuctionService.git`):
- **TUYỆT ĐỐI CHỈ ĐƯỢC trỏ tới các tài nguyên nội bộ của AuctionService**.
- **TUYỆT ĐỐI KHÔNG ĐƯỢC trỏ ngược ra ngoài Root** (`../../../docs/...`) hay sang các Service khác (`../../Catalog/...`).
- Toàn bộ tài liệu, hướng dẫn và cấu hình trong repo này phải 100% tự cung tự cấp (Self-contained).

---

## 📚 NGUYÊN TẮC 3: DANH MỤC FILE NỘI BỘ BẮT BUỘC PHẢI ĐỌC TẠI AUCTION SERVICE

Khi bắt đầu làm việc tại AuctionService, AI Agent **BẮT BUỘC PHẢI ĐỌC** các file nội bộ sau theo thứ tự:
1. `specs/00_AGENT_MISSION_GUARDRAILS.md`: Bounded context phiên đấu giá, đặt giá, chốt phiên, port `8005`, DB `5437` (`auction_db`).
2. `specs/01_CLEAN_ARCHITECTURE_LAYERS.md`: Cấu trúc 4 tầng Clean Architecture, xử lý đồng quy đặt giá (bidding concurrency).
3. `specs/02_API_GRPC_CONTRACTS.md`: Hợp đồng gRPC Server (`AuctionApi`: CreateAuction, PlaceBid, CloseAuction).
4. `specs/03_EVENT_MESSAGING.md`: Sự kiện RabbitMQ (`AuctionCreatedEvent`, `BidPlacedEvent`, `AuctionWonEvent`...).
5. `specs/04_DATA_PERSISTENCE.md`: Schema PostgreSQL (`Auctions`, `Bids`, `AuctionWinners`).
6. `specs/05_DEV_VERIFICATION_CHECKLIST.md`: Lệnh build & test kiểm chứng service.

---

## 🔒 NGUYÊN TẮC 4: RÀO CHẮN BẤT BIẾN TẠI AUCTION SERVICE

1. **TUYỆT ĐỐI KHÔNG DÙNG REPOSITORY PATTERN BỌC NGOÀI EF CORE**: Sử dụng trực tiếp `AuctionDbContext` và `DbSet<T>` (hoặc interface `IAuctionDbContext`) trong tầng Application. Cấm tạo `IAuctionRepository`, `IBidRepository`, `UnitOfWork`.
2. **ĐẢM BẢO TÍNH TOÀN VẸN VÀ THỨ TỰ ĐẶT GIÁ**: Xử lý concurrency an toàn khi nhiều user cùng đặt giá (Bid) tại cùng 1 thời điểm.
3. **CẤU HÌNH PORT 8005 VÀ DATABASE 5437 LÀ BẤT BIẾN**.
4. **HỢP ĐỒNG PROTOBUF ĐỒNG BỘ 100% VỚI GATEWAY**.

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
   - Ví dụ: `AUCTION_NOT_FOUND`, `AUCTION_ENDED`, `BID_AMOUNT_TOO_LOW`, `VAL_INVALID_INPUT`, `UNAUTHORIZED`, `FORBIDDEN`.

---

## 🎯 NGUYÊN TẮC 6: ĐIỀU KIỆN HOÀN THÀNH NHIỆM VỤ (DEFINITION OF DONE)

Một task tại AuctionService chỉ hoàn tất khi:
- [ ] Đã lập Plan chi tiết trước khi thực hiện.
- [ ] Không chứa bất kỳ liên kết trỏ ngược ra ngoài AuctionService.
- [ ] `dotnet build AuctionService.sln -c Release` thành công 100% (0 error).
- [ ] `dotnet test AuctionService.sln -c Release` vượt qua 100% các bài test.
- [ ] Trạng thái Git sạch, không commit file rác (`bin/`, `obj/`, `.vs/`, `.idea/`).
