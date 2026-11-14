# Dựng mạng farm: modem Viettel + MikroTik RB5009 (chỉ đường line 1)

Mục tiêu:
- **Modem Viettel**: chỉ làm cửa ra Internet, LAN đổi thành `192.168.2.1`.
- **MikroTik**: làm router của farm, `192.168.1.1`, mạng `192.168.0.0/23` (510 địa chỉ), là nơi duy nhất cấp IP cho các box.
- **PC**: giữ `192.168.1.5`, nên điện thoại vẫn gọi server ở `192.168.1.5` như cũ.
- **Đường line 2** (WAN1 Bridge VLAN 10, thiết bị `192.168.88.1`): không đụng tới, giữ nguyên dây của nó trên modem.

**Lưu ý:** từ bước 2 đến hết bước 5, PC **không có Internet** (khoảng 5–10 phút). Hãy đọc hết một lượt rồi mới làm, và làm liền một mạch.

---

## Bước 1 — Chép file cấu hình vào MikroTik (lúc mạng còn chạy, chưa đổi gì)

1. Mở **WinBox**, đăng nhập MikroTik như đang làm.
2. Menu trái, bấm **Files**.
3. Kéo thả file `D:\ControlPhone\tools\mikrotik\farm-router.rsc` vào cửa sổ Files.
4. Thấy dòng `farm-router.rsc` trong danh sách là xong. Bước này **chưa thay đổi gì**.

## Bước 2 — Đổi IP LAN của modem Viettel

1. Trên Chrome, mở `https://192.168.1.1`, đăng nhập modem.
2. Vào trang **LAN** nâng cao (có các mục *Ethernet Port Configuration*, *DHCP*, *Primary Address*).
3. Ở mục **DHCP**:
   - **IP Address Pool**: `192.168.2.2` – `192.168.2.254`
4. Ở mục **Primary Address**:
   - **IP Address**: `192.168.2.1`
   - **Subnet Mask**: giữ `255.255.255.0 (/24)`
5. Bấm **Save** của mục đó.
6. Trang modem sẽ mất kết nối. Đó là bình thường, vì modem đã sang `192.168.2.1`.

Tuyệt đối **không bấm Delete** ở WAN0, WAN1, WAN6.

## Bước 3 — Cắm lại dây

| Dây | Cắm từ | Cắm vào |
|---|---|---|
| Internet cho farm | 1 cổng LAN **bất kỳ còn trống** của modem (không phải cổng đang cắm thiết bị line 2) | **Cổng 1** của MikroTik (cổng ghi **PoE IN / 2.5G**) |
| PC | Cổng mạng của PC | **Cổng 2** của MikroTik |
| Box phone | Dây LAN của box | **Cổng 3** (box sau: 4, 5, 6, 7) |

Rút **mọi dây khác của farm** đang cắm thẳng vào modem. Trên modem chỉ còn:
- dây sang cổng 1 MikroTik;
- dây của đường line 2 (giữ nguyên);
- thiết bị trong nhà nếu có.

## Bước 4 — Nạp cấu hình cho MikroTik (xoá cũ, chạy file mới)

1. Trong WinBox, bấm **New Terminal**.
2. Gõ đúng dòng sau rồi Enter:
   ```
   /system reset-configuration no-defaults=yes skip-backup=yes run-after-reset=farm-router.rsc
   ```
3. Nếu hỏi xác nhận thì gõ `y`. Router khởi động lại, WinBox mất kết nối. **Chờ khoảng 1–2 phút**.
4. Đăng nhập lại:
   - Mở WinBox, chọn tab **Neighbors**, bấm nút làm mới.
   - Bấm vào dòng có **địa chỉ MAC** của router (tên `farm-router`). Đừng bấm vào IP.
   - User `admin`, mật khẩu **in trên tem dưới đáy router**.
5. Đổi mật khẩu ngay: **System → Password** (hoặc **System → Users**).

## Bước 5 — Đổi IP của PC sang mạng mới

Mở **PowerShell bằng quyền Administrator**, chạy lần lượt 2 lệnh:

```powershell
netsh interface ipv4 set address name="Ethernet 2" static 192.168.1.5 255.255.254.0 192.168.1.1
```

```powershell
netsh interface ipv4 set dnsservers name="Ethernet 2" static 192.168.1.1 primary
```

## Bước 6 — Kiểm tra

Trong PowerShell:

```powershell
ping 192.168.1.1
```
```powershell
ping 8.8.8.8
```

- Cả hai đều có `Reply`: **đã có mạng**. Mở một trang web để chắc chắn.
- Trong WinBox, **IP → DHCP Client**: dòng `ether1` phải ở trạng thái **bound**, với IP `192.168.2.x` nhận từ modem.
- Trong WinBox, **IP → DHCP Server → Leases**: khi box ở chế độ LAN sẽ thấy danh sách điện thoại nhận IP.

## Bước 7 — Farm

1. Gạt box sang **LAN/OTG**.
2. Mở **ControlPhone 2.2.2** → **Cài đặt** → tick **Chế độ OTG/LAN** → **Lưu**.
3. Khoảng 1–2 phút sau, điện thoại lần lượt hiện vào đúng ô cũ.

---

## Nếu có trục trặc

| Hiện tượng | Cách xử lý |
|---|---|
| WinBox **Neighbors** không thấy router | Kiểm tra dây PC → **cổng 2** MikroTik. Bấm làm mới, chờ thêm 1 phút. |
| `ping 192.168.1.1` không được | Kiểm tra lại lệnh `netsh` ở bước 5 (mặt nạ `255.255.254.0`). Kiểm tra dây PC → cổng 2. |
| Ping được `192.168.1.1` nhưng không ra `8.8.8.8` | WinBox → **IP → DHCP Client**: nếu `ether1` không **bound** thì dây modem → **cổng 1** đang cắm sai, hoặc cắm nhầm vào cổng modem dành cho line 2. Đổi sang cổng LAN khác của modem. |
| WinBox → **Log** báo lỗi `farm-router.rsc` | Chụp màn hình Log gửi cho Claude. |
| Muốn quay về như cũ | Cắm PC thẳng vào modem, cho PC nhận IP tự động: `netsh interface ipv4 set address name="Ethernet 2" dhcp`. Mở `https://192.168.2.1`, đổi IP LAN của modem về `192.168.1.1` và dải `192.168.1.2–254`. Sau đó đặt lại PC: `netsh interface ipv4 set address name="Ethernet 2" static 192.168.1.5 255.255.255.0 192.168.1.1`. |
