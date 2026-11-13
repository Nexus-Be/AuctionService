# 13 · GET /api/v1/admin/auctions/{id}/deposits

| | |
|---|---|
| Service | Auction · **MỚI** |
| Caller | FE (Admin, Staff) |
| Auth | JWT, role Admin hoặc Staff (chỉ xem) |
| Query | `status`, `page`, `pageSize` |

## 1. Giải quyết bài toán gì
Admin/Staff **đối soát cọc của một phiên**: ai đã cọc, trạng thái, đã Release/Capture chưa, kèm mã tham chiếu Gateway. Dùng khi force-cancel, khi có khiếu nại, hoặc khi cần kiểm tra Capture/Release bị kẹt.

## 2. Response 200 (happy case)
```json
{
  "auctionId": "…",
  "summary": {
    "total": 12,
    "authorized": 3, "released": 8, "captured": 1, "failed": 0, "expired": 0,
    "amountHeld": 150000, "amountCaptured": 50000
  },
  "items": [
    {
      "depositId": "…", "bidderId": "…", "amount": 50000, "ratePercent": 5,
      "status": "Captured", "capturePurpose": "Forfeit",
      "gatewayReference": "…", "policyVersion": 3,
      "authorizedAt": "…", "releasedAt": null, "capturedAt": "…",
      "pendingAction": null
    }
  ],
  "page": 1, "pageSize": 20, "totalItems": 12
}
```
`pendingAction`: `RELEASE_RETRYING` · `CAPTURE_RETRYING` · `null` (thao tác đang chờ job quét gọi lại). Thêm `needsAttention: true` khi đã retry quá 10 lần, cần Admin xử lý tay.

## 3. Các case lỗi
| HTTP | code | Khi nào |
|---|---|---|
| 401 | `UNAUTHORIZED` | |
| 403 | `FORBIDDEN` | Không phải Admin/Staff |
| 404 | `AUCTION_NOT_FOUND` | |
| 400 | `VALIDATION_ERROR` | `status` lạ, `pageSize` quá lớn |

## 4. Ghi chú
- Chỉ đọc. Không có thao tác sửa cọc thủ công; nếu cần xử lý tay, tạo API riêng có audit.
- Mỗi lần xem bởi Admin/Staff ghi log truy cập nếu team coi dữ liệu thanh toán là nhạy cảm.
