# Settings/Setup reliability — durable verification summary

Ngày tổng hợp: 2026-11-24. Base của final fix wave: `5d5acc730252b9e8683a14df76109304e2398956`.

Tài liệu này chỉ giữ số đo và kết luận cần tra cứu lâu dài. Không chứa credential, đường dẫn profile cá nhân, model/raw fixture hoặc log chưa khử thông tin. Bằng chứng RED/GREEN và lệnh đầy đủ của wave nằm trong `.superpowers/sdd/2026-11-21-settings-setup-reliability/final-fix-report.md`.

## Final automated gate

- Full C# suite: **1.129 passed, 0 failed, 2 skipped / 1.131**. Hai skip vẫn là real-sidecar PaddleOCR tests vì import/model warm-up không khả dụng; không được tính là pass.
- `dotnet build "ReTran Project.slnx" -c Release`: **PASS**, 0 error, 34 analyzer warning `xUnit1051` trong test code.
- `npm run build` trong `ReTran.App/vite-project`: **PASS** (`tsc && vite build`). Các cảnh báo Vite `__dirname`/font runtime đã có từ trước.
- `git diff --check`: **PASS**; không có whitespace error.
- Controlled regressions bao phủ deletion `Pending → DeletedConfirmed` (reject trước xoá, lỗi sau xoá, unknown, retry cùng session và restart); ngưỡng IO 5% + high-water tăng; popup pre-accept/generation; duplicate Start; strict settings Update; cleanup RPC failure; runtime error-writer seam.

## Số đo RPC/model thật từ smoke ngày 2026-11-23

Các số dưới đây là smoke bằng production Release classes trong fixture nhân bản, **không phải E2E App UI/game** và không phải phép đo lại của final fix wave:

| Measurement | Kết quả đã ghi nhận |
| --- | ---: |
| Local 1.8B, runtime b12532, process đầu thành công (“cold”, N=1, không purge OS cache) | **59,653 s** tới health |
| Local 1.8B, warm process (N=1) | **2,225 s** tới health |
| IO tại cold ready | **18.241.534 / 1.169.028.064 byte** |
| IO tại warm ready | **512 byte** |
| WorkingSet llama-server sau ready-poll, cold/warm | **1.576,5 / 1.559,8 MiB** |
| Peak WorkingSet llama-server, cold/warm | **1.868,5 / 1.559,8 MiB** |
| Status RPC round-trip trong cold load, mean/p95/max (N=86) | **0,527 / 1,351 / 3,957 ms** |
| Status wire khi ready, cold/warm | **671.902 / 662.150 B/s** |
| Cancel RPC giữa load | Stop được xác nhận sau **139,632 ms**; process con thoát sau **179,343 ms** |
| OCR medium CPU inference trên fixture 960×240 | **4.825 ms**, 2 spots; active pair vẫn medium sau list lại |

IO smoke là lý do final wave chỉ cho determinate percent/ETA khi IO high-water đạt ít nhất 5% GGUF **và còn tăng**; mọi trường hợp khác dùng phase vô định, chỉ health mới báo 100%. IO vẫn là tín hiệu stall detector.

## Blocker và giới hạn không được che

1. Chưa chạy E2E App/Home/floating-menu popup, nút Hủy trên UI, Google/API bằng App thật, Setup Applying hoặc Razor Settings remount trong final wave. Automation tương ứng không thay thế UI smoke.
2. Runtime active thật `b12556` thiếu `llama-server.exe`; `b12546` không ready trong các lần thử trước. Không repair state thật và không gán nhầm đây là regression của final wave.
3. OCR GPU vẫn bị chặn bởi `cudnn64_9.dll` error 126; kết quả CPU không che GPU blocker.
4. Không đo game + streaming, model hàng trăm GB, real 600 giây stall, huge download hoặc HTTP timeout thật. Controlled fakes/tests là bằng chứng cho các nhóm đó.
5. Cold/warm đều N=1 và không purge OS cache; không suy ra benchmark tổng hoặc gaming budget từ các số trên.
6. Một full-suite run bị crash bởi race fixture trong `LlmModelManagerTests.Delete_RefusesActive_RefusesDownloading`: test mở gate rồi xoá temp trước khi background download dừng. Test hiện chờ artifact trước Dispose; isolated class và full suite tiếp theo đều pass. Không thay đổi production cho race này.

## Isolation

Mọi deletion/settings regression dùng thư mục/file tạm và fake RPC/catalog. Final wave không xoá model thật, không ghi settings/runtime thật, không dừng process của user, không thêm package, không push và không sao chép model/raw fixture vào `Docs/verification/`.
