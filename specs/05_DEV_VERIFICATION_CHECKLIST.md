# 05 — DANH SÁCH KIỂM CHỨNG & NGHIỆM THU (AUCTION SERVICE CHECKLIST)
> **Trạng thái triển khai (2026-10-24):** ⚠️ Một phần — `dotnet build Auction.sln -c Release` **chạy được**; `dotnet run --project Auction.Api` **chạy được nhưng ở port `5230`/`7202`** (không phải `8005`); `dotnet test` **không thể chạy** vì repo **0 test project**; CI **không có bước test**. Không có endpoint gRPC/REST nào để nghiệm thu ngoài `GET /health`.
> **Nguồn sự thật:** code trong repo này (`file:line` ghi kèm trong từng bảng).
> **Cập nhật lần cuối:** 2026-10-24.
---
## 1. LỆNH BIÊN DỊCH & CHẠY THỬ NGHIỆM
### 1.1. Lệnh đúng với repo hiện tại
```bash
# 1. Biên dịch solution — TÊN FILE LÀ Auction.sln (không phải AuctionService.sln)
dotnet build Auction.sln -c Release
# 2. Chạy service ở profile local. Port thật: http 5230, https 7202
#    (Auction.Api/Properties/launchSettings.json:17,27)
dotnet run --project Auction.Api
# 3. Muốn chạy đúng port 8005 như Dockerfile/docker-compose thì phải override URL:
ASPNETCORE_URLS=http://+:8005 dotnet run --project Auction.Api
# 4. Kiểm tra service còn sống (endpoint duy nhất hiện có)
curl http://localhost:5230/health
# → {"status":"Healthy","service":"Auction.Api"}   (Auction.Api/Program.cs:58)
```
> ⚠️ `GET /health` trả `Healthy` **vô điều kiện** — nó không kiểm tra DB, không kiểm tra RabbitMQ. DB chết thì `/health` vẫn `Healthy`.
### 1.2. Trạng thái từng lệnh (đối chiếu thực tế)
| Lệnh (đặc tả) | Trạng thái | Bằng chứng |
|---|---|---|
| `dotnet build Auction.sln -c Release` | ✅ CÓ | `Auction.sln` tồn tại ở gốc repo; 4 project `Auction.Api`/`Application`/`Domain`/`Infrastructure` |
| `dotnet run --project Auction.Api` | ⚠️ MỘT PHẦN | chạy được, nhưng port thật `5230`/`7202` (`launchSettings.json:17,27`) — **không** phải `8005` |
| `dotnet test Auction.sln -c Release` | ❌ CHƯA CÓ | repo **0 test project** (`*.Tests.csproj` = 0; `Auction.sln` chỉ có 4 project production) ⇒ lệnh không có gì để chạy |
| Bước test trong CI | ❌ CHƯA CÓ | `.github/workflows/ci-cd.yml` chỉ build/push Docker image; không có `dotnet test` |
| Test qua ApiGateway bằng `gateway.runtime.json` | ❌ KHÔNG DÙNG ĐƯỢC | file này **không nằm trong repo này** (thuộc ApiGateway) ⇒ vi phạm quy tắc Inward-Only; và Auction hiện **0 endpoint nghiệp vụ** để gateway gọi |
### 1.3. Cách nghiệm thu tạm khi chưa có test project
Hiện chỉ có thể nghiệm thu bằng tay qua 2 điểm:
1. `dotnet build Auction.sln -c Release` → 0 error.
2. `curl /health` + kiểm tra log migration ở `Auction.Api/Program.cs:42-56`.
---
## 2. CHECKLIST TRƯỚC KHI COMMIT CHO AI AGENTS
- [ ] **Biên dịch thành công 100%**: `dotnet build Auction.sln -c Release` → 0 error.
- [ ] **Không vi phạm rào chắn**: Không dùng Repository Pattern bọc ngoài EF Core (hiện `*Repository|UnitOfWork` = 0 kết quả ✅).
- [ ] **Bảo đảm Concurrency**: Logic đặt giá phải chống race condition. ⚠️ Hiện **chưa có** logic đặt giá; `RowVersion` đã khai báo trên `AuctionState` và đánh dấu `IsConcurrencyToken()` nhưng **chưa từng được kiểm tra** vì `SaveChangesAsync` có 0 lời gọi.
- [ ] **Port & Database**: Container lắng nghe `8005` (`Dockerfile:3-4`); chạy local mặc định `5230`/`7202`; DB `auction_db` port `5437` (`appsettings.json:10`). Ghi rõ port nào khi báo cáo, không được nói "8005" cho môi trường local.
- [ ] **Git Status**: Không commit file rác (`bin/`, `obj/`, `.idea/`, `.env`).
- [ ] **Mọi tài liệu sửa phải có banner trạng thái** theo.