# Nexora Mobile — Hồ sơ kiểm thử bảo mật & tuân thủ Google Play

> **Phạm vi**: toàn bộ source `nexora-mobile` tại commit `c59e138` (branch `main`), Expo SDK 57 / React Native 0.86.3.
> **Ngày thực hiện**: 27-09-2026
> **Mục tiêu**: xác định mọi điểm chưa đáp ứng chính sách Google Play + rủi ro bảo mật, và lập kế hoạch sửa chi tiết theo từng bước.
> **Trạng thái kết luận**: ❌ **CHƯA ĐỦ ĐIỀU KIỆN SUBMIT LÊN GOOGLE PLAY**. Có 8 blocker (P0) chắc chắn bị từ chối.

---

## Cấu trúc tài liệu

| File | Nội dung | Đọc khi nào |
|---|---|---|
| [`00-EXECUTIVE-SUMMARY.md`](./00-EXECUTIVE-SUMMARY.md) | Tóm tắt điều hành, bảng blocker, ước lượng effort, timeline | Đọc đầu tiên |
| [`01-SECURITY-AUDIT.md`](./01-SECURITY-AUDIT.md) | Báo cáo kiểm thử bảo mật chi tiết: 35 finding, có `file:line`, PoC, impact | Khi cần bằng chứng kỹ thuật |
| [`02-PLAY-POLICY-COMPLIANCE.md`](./02-PLAY-POLICY-COMPLIANCE.md) | Đối chiếu từng chính sách Google Play + trích dẫn nguyên văn điều khoản | Khi tranh luận với reviewer / chuẩn bị appeal |
| [`03-REMEDIATION-PLAN.md`](./03-REMEDIATION-PLAN.md) | **Kế hoạch sửa step-by-step**, chia 6 phase, từng bước có file đích + acceptance criteria | Khi bắt đầu code |
| [`04-PLAY-CONSOLE-CHECKLIST.md`](./04-PLAY-CONSOLE-CHECKLIST.md) | Checklist khai báo Play Console: Data Safety, Content Rating, AI declaration, Deletion URL | Trước khi bấm Submit |
| [`05-TEST-PLAN.md`](./05-TEST-PLAN.md) | Kế hoạch kiểm thử xác nhận (security + functional + device matrix) | Sau khi fix, trước khi build production |

---

## Quy ước mức độ nghiêm trọng

| Mức | Ý nghĩa | Hệ quả nếu submit |
|---|---|---|
| **P0 — Blocker** | Vi phạm chính sách rõ ràng hoặc tính năng cốt lõi hỏng | Bị **reject**, nguy cơ **suspend / terminate account** nếu tái phạm |
| **P1 — High** | Lỗ hổng bảo mật / rủi ro dữ liệu người dùng thực sự | Có thể qua review nhưng rủi ro rò rỉ dữ liệu, bad review, Play Vitals xấu |
| **P2 — Medium** | Nợ kỹ thuật, hygiene, rủi ro gián tiếp | Không chặn release, cần xử lý trong 1–2 sprint |
| **P3 — Low** | Cải thiện tùy chọn | Backlog |

---

## Nguyên tắc khi thực thi kế hoạch

1. **KHÔNG sửa code trước khi đọc** [`03-REMEDIATION-PLAN.md`](./03-REMEDIATION-PLAN.md) — thứ tự các phase có phụ thuộc lẫn nhau.
2. Mọi API Expo phải tra đúng doc phiên bản: **https://docs.expo.dev/versions/v57.0.0/** (theo `AGENTS.md` của repo). Tên option đã đổi giữa các SDK (ví dụ `enableProguardInReleaseBuilds` → `enableMinifyInReleaseBuilds`).
3. Mỗi P0 phải có bằng chứng verify (screenshot / log / test) lưu vào `docs/release-audit/evidence/` trước khi đóng.
4. Không tự ý thu hẹp phạm vi: một P0 còn mở = không submit.

---

## Nguồn chính sách tham chiếu

- [Payments policy](https://support.google.com/googleplay/android-developer/answer/9858738)
- [AI-Generated Content policy](https://support.google.com/googleplay/android-developer/answer/13985936)
- [App account deletion requirements](https://support.google.com/googleplay/android-developer/answer/13327111)
- [Target API level requirements](https://support.google.com/googleplay/android-developer/answer/11926878)
- [Support 16 KB page sizes](https://developer.android.com/guide/practices/page-sizes)
- [Expo SDK 57 reference](https://docs.expo.dev/versions/v57.0.0/)
