# ControlPhone 2 (native)

ControlPhone viết lại thành ứng dụng Windows chạy trực tiếp (C# / WPF), không cần web server hay trình duyệt. Có đầy đủ chức năng của bản web 1.x.

- Kết nối adb trực tiếp qua socket (127.0.0.1:5037), không sinh tiến trình adb.exe cho từng lệnh.
- Mỗi máy một phiên scrcpy-server 5.0; video H.264 được đọc và giải mã trên luồng riêng của máy đó.
- Giải mã bằng FFmpeg native (`avcodec`/`avutil` đi kèm scrcpy), đổi YUV → BGRA bằng bảng tra, vẽ bằng WPF (Direct3D). Ô ngoài vùng nhìn thấy không giải mã.
- Dùng chung `data/config.json` (tên, số thứ tự, nhóm, cài đặt) và bản dịch `public/js/lang/*.js` với bản web.

## Cấu trúc

| Thư mục | Nội dung |
|---|---|
| `ControlPhone/Adb` | client adb (track-devices, shell, exec, cấy khoá, tắt adb khi thoát) |
| `ControlPhone/Scrcpy` | phiên scrcpy (video / chỉ điều khiển), lệnh điều khiển |
| `ControlPhone/Video` | FFmpeg P/Invoke, bộ giải mã H.264, bộ đệm khung hình kép |
| `ControlPhone/Core` | quản lý thiết bị, thao tác hàng loạt, lưu trữ, đa ngôn ngữ |
| `ControlPhone/UI`, `MainWindow.*.cs` | giao diện: lưới, chọn/đồng bộ, phóng to, menu, hộp thoại, tiến trình |
| `keyboard` | ControlPhone Keyboard — bàn phím Android không giao diện cho chế độ "chỉ nhập từ PC" (build: `keyboard/build.ps1`; khoá ký ở `%USERPROFILE%\.controlphone\keyboard.jks`, không đưa lên git) |
| `CoreTest` | kiểm thử lõi trên điện thoại thật (chỉ lệnh chỉ đọc), luôn dùng **bản sao** của `data/` trong thư mục tạm: `dotnet run -c Release [-- kbd \| store \| xaml backup]` |

Tự kiểm tra giao diện (không cần thao tác): đặt `CP_SELFTEST=<thư mục>` rồi chạy app — app tự mở từng hộp thoại / màn hình, chụp ảnh vào thư mục đó và thoát.

## Build

```bash
dotnet build native/ControlPhone.Native.sln -c Release
```

Cần thư mục `vendor/scrcpy-win64-v5.0` (chạy `npm run setup` ở thư mục gốc) để có FFmpeg, scrcpy-server và adb.

Đóng gói bản chạy độc lập:

```bash
dotnet publish native/ControlPhone/ControlPhone.csproj -c Release -r win-x64 --self-contained true -o dist/ControlPhone-v2-win64
```
