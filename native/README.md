# ControlPhone 2 (native)

Bản viết lại ControlPhone thành ứng dụng Windows chạy trực tiếp (C# / WPF), không cần web server hay trình duyệt.

- Kết nối adb trực tiếp qua socket (127.0.0.1:5037), không sinh tiến trình adb.exe cho từng lệnh.
- Mỗi máy một phiên scrcpy-server 5.0; video H.264 được đọc và giải mã trên luồng riêng của máy đó.
- Giải mã bằng FFmpeg native (`avcodec`/`avutil` đi kèm scrcpy), đổi YUV → BGRA bằng bảng tra, vẽ bằng WPF (Direct3D).
- Dùng chung `data/config.json` với bản web (tên, số thứ tự máy, cài đặt luồng).

## Trạng thái

Giai đoạn 1 (lõi): lưới tất cả máy, chạm / vuốt / cuộn bằng chuột, chuột phải = Quay lại, đổi cỡ ô.
Chưa có: màn hình phóng to, đồng bộ nhóm, công cụ ADB, cài APK, tệp, WiFi… (đang chuyển dần từ bản web).

Đo trên 20 máy SM-G960F (480p, 15 fps): CPU 3% (0,66 lõi), RAM ~360 MB — bản web cùng điều kiện: CPU 4%, RAM ~1,4 GB.

## Build

```bash
dotnet build native/ControlPhone.Native.sln -c Release
```

Cần thư mục `vendor/scrcpy-win64-v5.0` (chạy `npm run setup` ở thư mục gốc) để có FFmpeg, scrcpy-server và adb.
