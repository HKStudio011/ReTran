# Xoá camera ảo (virtual camera) — 2026-10-25

## Đã xoá gì

- **Project native `ReTran.Camera/`** (toàn bộ): filter DirectShow `ReTran.Camera.dll`, source C++, baseclasses, build output.
- **`ReTran.App/Services/Camera/`** (toàn bộ): `ICameraOutput`, `CameraOutputService`, `CameraProtocol`, `CameraRegistrar`.
- Đăng ký camera lúc mở app (`MainPage.OnAppearing`), đăng ký DI `ICameraOutput`, target build `CopyCameraDll` (App + Tests), entry project trong `ReTran Project.slnx`.
- Đường publish khung annotated sang camera trong `PipelineController` (field `_camera`, tham số ctor, `Hold()`, block submit).
- UI: step "Camera ảo" trong chuỗi tín hiệu trên trang điều khiển + toàn bộ string i18n camera (`home.card.camera`, `home.camera.*`, `deck.chain.camera`, `deck.camera.*`); giá trị `deck.status.live` không còn nhắc camera.
- 4 file test camera trong `ReTran.Tests` + fake `FakeCameraOutput` trong `PipelineControllerTests`.

App **không còn** đăng ký (HKCU) hay ghi shared memory `Local\ReTran.Camera.V1` (khung) và `Local\ReTran.Camera.Heartbeat` (heartbeat) nữa.

## Dọn máy thủ công (quan trọng — làm TRƯỚC khi xoá DLL cũ)

Nếu máy đã từng chạy bản build có camera, thiết bị "ReTran Camera" vẫn còn nằm trong danh sách thiết bị (đăng ký per-user trong HKCU trỏ tới DLL cũ). Gỡ đăng ký **trước** khi xoá file DLL, không thì Windows giữ tham chiếu tới DLL đã mất:

```bat
regsvr32 /u "<đường dẫn cũ>\ReTran.Camera.dll"
```

`<đường dẫn cũ>` là nơi từng copy DLL cạnh output app (ví dụ `...\ReTran.App\bin\Debug\net10.0-windows10.0.19041.0\win10-x64\`). Sau khi `regsvr32 /u` thành công mới xoá file DLL; các bản build output cũ chứa `ReTran.Camera.dll` cũng nên xoá luôn.

## OBS nên chuyển sang gì

OBS không dùng thiết bị "ReTran Camera" nữa — trên OBS thêm nguồn **Browser Source** rồi dán URL copy từ card "Overlay cho OBS" trên trang điều khiển của app (làm theo hướng dẫn trong card: đặt kích thước 1920×1080; overlay là trang trong suốt, hộp/bản dịch cập nhật qua SSE).
