# 05 — DANH SÁCH KIỂM CHỨNG & NGHIỆM THU (AUCTION SERVICE CHECKLIST)

---

## 1. LỆNH BIÊN DỊCH & CHẠY THỬ NGHIỆM

```bash
# 1. Biên dịch solution Auction
dotnet build Auction.sln -c Release

# 2. Khởi chạy service (gRPC port 8005)
dotnet run --project Auction.Api

# 3. Thử nghiệm qua ApiGateway:
# Mở gateway.runtime.json, đổi "auction": { "Mode": "Real", "Address": "http://localhost:8005" }
# Gọi GET /api/v1/auctions trên Swagger Gateway xem kết quả thật trả về.
```

---

## 2. CHECKLIST TRƯỚC KHI COMMIT CHO AI AGENTS

- [ ] **Biên dịch thành công 100%**: Không lỗi cảnh báo nghiêm trọng.
- [ ] **Không vi phạm rào chắn**: Không dùng Repository Pattern bọc ngoài EF Core.
- [ ] **Bảo đảm Concurrency**: Logic đặt giá bid phải có cơ chế chống race condition.
- [ ] **Port & Database**: Lắng nghe đúng port `8005`, kết nối `auction_db` trên port `5436`.
- [ ] **Git Status**: Không commit file rác (`bin/`, `obj/`, `.idea/`, `.env`).
