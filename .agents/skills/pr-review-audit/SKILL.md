---
name: pr-review-audit
description: Audit and review Pull Requests in AuctionService (port 8005). Validates API documentation, thin controllers, and business logic against canonical standards. Generates persistent 3-file audit reports in .agents/review/{PR_SLUG}/ with human-only logic sign-off checklists, enforcing format-only fixes upon user approval.
---

# Pull Request Review and Audit Skill (`pr-review-audit`) - AuctionService

> **Scope:** AuctionService repository only (`src/Services/Auction/`). 100% Inward-Only. Never reference the root workspace or sibling services.

This skill governs the standardized quality audit and review process for Pull Requests in AuctionService.

---

## 1. Rào Chắn Kích Hoạt & Ràng Buộc Bất Biến

### 1.1 Chỉ Kích Hoạt Khi Review Pull Request (PR-Only)
* Kỹ năng này **CHỈ ĐƯỢC CHẠY** khi người dùng yêu cầu review Pull Request trong AuctionService.
* Cấm tự ý chạy trong các tác vụ code thông thường.

### 1.2 Rào Chắn Kiểm Tra Nhánh Git (Checkout Preflight Verification)
1. Kiểm tra nhánh hiện tại: `git rev-parse --abbrev-ref HEAD` hoặc `git status`.
2. Nếu đang ở `dev` hoặc `main`: **DỪNG LẠI NGAY**. Yêu cầu người dùng checkout sang nhánh của PR trước khi review.

### 1.3 Quy Tắc Ký Duyệt Nghiệp Vụ Của Con Người (Human-Only Logic Sign-off)
* Agent **tuyệt đối không tự ý đánh dấu `[x]`** vào các checklist logic, usecase và nghiệp vụ.
* Toàn bộ checklist trong `03_LOGIC_BUSINESS_AUDIT.md` phải để trống `[ ]` để Reviewer / Con người trực tiếp kiểm tra và tích hoàn thành.

### 1.4 Rào Chắn Không Sửa Logic (Strict Format-Only Fix)
* Lỗi logic/nghiệp vụ: Agent chỉ ghi chú `[NOTE - LOGIC ISSUE]` kèm vị trí `file:line` để tác giả PR tự sửa.
* Agent **tuyệt đối không tự sửa logic nghiệp vụ**. Pha 2 chỉ sửa lỗi format/conventions khi được người dùng duyệt.

### 1.5 Báo Cáo Không Bị Xóa & Không Commit Lên Git
* Báo cáo trong `.agents/review/` là hồ sơ kiểm định, không xóa sau khi sửa format.
* `.agents/review/` nằm trong `.gitignore` để không bị commit vào Git.

### 1.6 Báo Cáo Lưu Cục Bộ Nội Bộ Service (Local Service Invariant)
* Báo cáo review PR của AuctionService **BẮT BUỘC** lưu tại `.agents/review/{PR_SLUG}/` nội bộ trong repository AuctionService.
* **TUYỆT ĐỐI KHÔNG** tạo báo cáo ở Root repository (`Nexus-Be/.agents/review/`).

### 1.7 Quy Chuẩn Commit & Tiêu Đề PR (Zero Emoji / Icon Rule - Rule 18)
* Tiêu đề PR và toàn bộ commit messages **TUYỆT ĐỐI CẤM** chứa emoji hoặc icon (`✨`, `🐛`, `🔧`, `♻️`, `🧪`, `📝`, `⚡`, `👷`, `💄`, `🔒`, `🚧`...).
* Tuân thủ 100% Conventional Commits thuần túy bằng tiếng Anh: `<type>(<scope>): <subject>` hoặc `<type>: <subject>`.

---

## 2. Cấu Trúc Báo Cáo Tại AuctionService

Lưu tại (nội bộ AuctionService): `.agents/review/{PR_CODE}-{GIT_AUTHOR}-{COMMIT_SUMMARY_SLUG}/`
* `01_API_DOCS_AUDIT.md`: Kiểm tra tài liệu API nội bộ trong `docs/api/` đối soát với C# code.
* `02_CONTROLLER_AUDIT.md`: Kiểm tra Thin Controller (`Auction.Api/Controllers/Public/`), route số nhiều (`/api/v1/auctions`, `/api/v1/bids`), pure DTO return, XML summary, bidder identity extraction từ HttpContext.
* `03_LOGIC_BUSINESS_AUDIT.md`: Kiểm tra DataAnnotations, phân trang SQL `Skip/Take`, kiểm tra tính toàn vẹn trạng thái phiên đấu giá, cấm repository pattern over EF Core.

---

## 3. Quy Trình 2 Pha

1. **Pha 1 (Read-Only):** Quét diff `git diff $(git merge-base HEAD origin/dev)...HEAD --name-only`, kiểm tra quy chuẩn, tạo 3 file báo cáo với checklist trống `[ ]`, hiển thị tóm tắt.
2. **Pha 2 (Format-Only Fix):** Khi người dùng duyệt plan sửa format, Agent chỉ sửa format/conventions. Sửa xong cập nhật `[x]` cho mục format đó. Giữ nguyên ghi chú logic cho tác giả PR tự sửa.
