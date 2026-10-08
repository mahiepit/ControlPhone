using System.Windows;
using System.Windows.Controls;
using ControlPhone.Core;

namespace ControlPhone;

public partial class MainWindow
{
    TextBlock? AbCount;

    // ---------------- thanh thao tác ----------------
    void InitActionBar()
    {
        AbCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0), Tag = "no-i18n" };
        AbCount.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        ActionBar.Children.Add(AbCount);
        void IconB(string icon, string tip, Action a)
        {
            var b = new Button { Content = Icons.Get(icon), ToolTip = T(tip), Margin = new Thickness(0, 2, 4, 2) };
            b.SetResourceReference(StyleProperty, "IconBtn");
            b.Click += (_, _) => a();
            ActionBar.Children.Add(b);
        }
        void TextB(string text, string icon, string tip, Action a)
        {
            var b = Ui.Btn(text, icon, "Btn", (_, _) => a());
            b.ToolTip = T(tip); b.Margin = new Thickness(0, 2, 4, 2);
            ActionBar.Children.Add(b);
        }
        void Sep() => ActionBar.Children.Add(new Border { Width = 1, Height = 20, Margin = new Thickness(4, 0, 8, 0), Background = Theme.Brush("Line") });
        IconB("checksq", "Chọn tất cả (Ctrl+A)", () => SelectWhere(MatchFilter));
        IconB("square", "Bỏ chọn (Esc)", ClearSelection);
        IconB("invert", "Đảo chọn", () =>
        {
            var next = manager.Devices.Where(d => !d.Pending && MatchFilter(d) && !selected.Contains(d.Id)).Select(d => d.Id).ToList();
            selected.Clear(); selected.UnionWith(next); RefreshSelection();
        });
        Sep();
        foreach (var (act, icon, tip) in new[]
        {
            ("back", "back", "Quay lại"), ("home", "home", "Màn hình chính"), ("recents", "recents", "Đa nhiệm"), ("power", "power", "Nút nguồn"),
            ("voldown", "voldown", "Giảm âm lượng"), ("volup", "volup", "Tăng âm lượng"), ("unlock", "unlock", "Bật màn hình + vuốt mở khoá"),
            ("screenoff", "screenoff", "Tắt màn hình thật (vẫn xem được, tiết kiệm pin)"), ("screenon", "sun", "Bật lại màn hình thật"),
        })
            IconB(icon, tip, () => { var v = SelOnline(); if (v.Count > 0) PhoneControl.Act(v, act); });
        Sep();
        TextB("Văn bản", "type", "Gửi văn bản (hỗ trợ tiếng Việt)", DlgText);
        TextB("Dán", "clipboard", "Dán clipboard máy tính vào tất cả máy đang chọn (Ctrl+V)", () =>
        {
            var v = SelOnline();
            if (v.Count > 0) PasteToPhones(v, Clipboard.ContainsText() ? Clipboard.GetText() : "");
        });
        TextB("Ứng dụng", "apps", "Mở / dừng / gỡ ứng dụng", () => { var ids = SelIds(); if (ids.Count > 0) DlgApps(ids, null); });
        TextB("Cài APK", "package", "Cài APK (hoặc kéo thả file .apk vào cửa sổ)", () => { if (SelIds().Count > 0) PickApks(null); });
        TextB("Gửi tệp", "upload", "Gửi tệp vào điện thoại", () => { if (SelIds().Count > 0) DlgPush(null, null); });
        TextB("URL", "link", "Mở đường dẫn", DlgUrl);
        TextB("Shell", "terminal", "Chạy lệnh ADB shell", () => DlgShell(null));
        TextB("Chụp", "camera", "Chụp màn hình các máy đã chọn", () => Batch("screenshot", SelIds(), null, "Chụp màn hình"));
        var more = new Button { Content = Icons.Get("more"), ToolTip = T("Thêm"), Margin = new Thickness(0, 2, 4, 2) };
        more.SetResourceReference(StyleProperty, "IconBtn");
        more.Click += (_, _) => Ui.ShowMenu(MoreMenu(), more);
        ActionBar.Children.Add(more);
    }

    BatchTask? Batch(string op, List<string> ids, Dictionary<string, object?>? p, string title, bool openResults = false)
    {
        if (ids.Count == 0) return null;
        try { return actions.RunBatch(op, ids, p, title, openResults); }
        catch (Exception e) { Ui.Toast(e.Message, "err"); return null; }
    }

    IEnumerable<MenuSpec?> MoreMenu() =>
    [
        new("Thêm vào nhóm…", "tag", () => DlgGroup(SelIds(), false)),
        new("Bỏ khỏi nhóm…", "tag", () => DlgGroup(SelIds(), true)),
        new("Đánh số lại theo thứ tự đang hiển thị", "hash", RenumberAll),
        null,
        new("Gửi clipboard máy tính tới máy chọn", "clipboard", SendClipboard),
        new("Kéo thanh thông báo", "bell", () => PhoneControl.Act(SelOnline(), "notif")),
        new("Xoay màn hình", "rotate", () => PhoneControl.Act(SelOnline(), "rotate")),
        new("Luôn sáng khi cắm sạc: BẬT", "sun", () => Batch("stayawake", SelIds(), new() { ["on"] = true }, "Luôn sáng")),
        new("Luôn sáng khi cắm sạc: TẮT", "moon", () => Batch("stayawake", SelIds(), new() { ["on"] = false }, "Tắt luôn sáng")),
        new("Độ sáng màn hình…", "sun", DlgBrightness),
        new("Bàn phím điện thoại: TẮT (chỉ nhập từ PC)", "keyboard", () => Batch("pckbd", SelIds(), null, "Chỉ nhập từ PC", true)),
        new("Bàn phím điện thoại: BẬT lại", "keyboard", () => Batch("phonekbd", SelIds(), null, "Bật lại bàn phím điện thoại", true)),
        null,
        new("USB → WiFi (giữ kết nối không dây)", "wifi", () => ToWifi(SelIds())),
        new("WiFi → USB (ngắt WiFi ADB)", "usb", () => ToUsb(SelIds())),
        new("ROOT: kiểm tra quyền root", "shield", () => Batch("rootcheck", SelIds(), null, "Kiểm tra root", true)),
        new("ROOT: cấy khoá uỷ quyền (khỏi hỏi \"Cho phép gỡ lỗi USB\")", "key", () => Batch("pushkey", SelIds(), null, "Cấy khoá uỷ quyền", true)),
        new("ROOT: bật ADB WiFi cố định (cổng 5555)", "shield", RootAdbWifi),
        null,
        new("Chỉ hiển thị các máy đã chọn", "eye", () => { var ids = SelIds(); if (ids.Count > 0) SetSolo(ids); }),
        Store.State.Solo != null ? new MenuSpec("Hiện lại tất cả máy", "grid", () => SetSolo(null)) : null,
        new("Tạm dừng xem các máy đã chọn", "pause", () => SetPaused(SelIds(), true)),
        new("Tiếp tục xem các máy đã chọn", "play", () => SetPaused(SelIds(), false)),
        new("Kết nối lại luồng hình", "refresh", () => { foreach (var id in SelIds()) if (manager.Get(id) is { } d) manager.RestartSessions(d); Ui.Toast("Đang kết nối lại…"); }),
        new("Khởi động lại điện thoại…", "power", RebootSel, Danger: true),
    ];

    // Lệnh ADB soạn sẵn (quản lý / xem thông tin — không phải tự động hoá)
    sealed record AdbPreset(string Label, string Icon, string Cmd, bool Show = false, string? Confirm = null);
    static readonly AdbPreset?[] AdbPresets =
    [
        new("Thông tin máy", "info", "echo \"Model: $(getprop ro.product.model)\"; echo \"Android: $(getprop ro.build.version.release) (SDK $(getprop ro.build.version.sdk))\"; echo \"Serial: $(getprop ro.serialno)\"; echo \"Bản ROM: $(getprop ro.build.display.id)\"", true),
        new("Pin & nhiệt độ", "zap", "dumpsys battery | grep -E 'level|temperature|status|powered|health'", true),
        new("Bộ nhớ trong", "folder", "df -h /data /sdcard 2>/dev/null", true),
        new("RAM", "grid", "grep -E 'MemTotal|MemAvailable' /proc/meminfo", true),
        new("IP / WiFi đang kết nối", "wifi", "ip -f inet addr show wlan0 | grep inet; dumpsys wifi | grep -m1 'mWifiInfo' | sed 's/, /\\n/g' | grep -E 'SSID|RSSI|Link speed'", true),
        new("Ứng dụng đang mở", "apps", "dumpsys activity activities | grep -m1 -E 'mResumedActivity|topResumedActivity'", true),
        new("Danh sách ứng dụng đã cài", "package", "pm list packages -3 | sed 's/package://' | sort", true),
        new("Độ phân giải màn hình", "phone", "wm size; wm density", true),
        new("Thời gian chạy (uptime)", "refresh", "uptime", true),
        null,
        // Android 10: game không tự ẩn thanh điều hướng → ép chế độ toàn màn hình theo từng ứng dụng (policy_control, bị bỏ từ Android 11)
        new("Ẩn thanh 3 nút cho ứng dụng đang mở (Android 10)", "phone",
            "if [ \"$(getprop ro.build.version.sdk)\" -ge 30 ]; then echo \"Android 11+: không cần/không hỗ trợ (game tự ẩn thanh điều hướng)\"; else P=$(dumpsys window | grep -m1 mCurrentFocus | sed 's/.* \\([^ /]*\\)\\/.*/\\1/'); case \"$P\" in *mCurrentFocus*|\"\") echo \"Không xác định được ứng dụng đang mở\";; *) C=$(settings get global policy_control); case \"$C\" in immersive.navigation=*) L=${C#immersive.navigation=};; *) L=\"\";; esac; case \",$L,\" in *\",$P,\"*) ;; *) L=\"${L:+$L,}$P\";; esac; settings put global policy_control \"immersive.navigation=$L\"; echo \"Đã ẩn thanh 3 nút trong: $L\";; esac; fi", true),
        new("Hiện lại thanh 3 nút (Android 10)", "phone", "settings delete global policy_control >/dev/null; echo \"Đã hiện lại thanh điều hướng ở mọi ứng dụng\"", true),
        null,
        new("Bật WiFi", "wifi", "svc wifi enable && echo \"Đã bật WiFi\""),
        new("Tắt WiFi", "wifioff", "svc wifi disable && echo \"Đã tắt WiFi\"", Confirm: "Máy đang nối ADB qua WiFi sẽ mất kết nối."),
        new("Bật dữ liệu di động", "zap", "svc data enable && echo \"Đã bật dữ liệu\""),
        new("Tắt dữ liệu di động", "stop", "svc data disable && echo \"Đã tắt dữ liệu\""),
        new("Dọn bộ nhớ đệm (cache) ứng dụng", "eraser", "pm trim-caches 999G && echo \"Đã dọn cache\""),
        new("Đóng ứng dụng chạy nền", "stop", "am kill-all && echo \"Đã đóng ứng dụng nền\""),
        // chỉ giả lập mức pin hiển thị (giữ đến khi khôi phục hoặc khởi động lại máy)
        new("Đặt pin 100% (giả lập)", "zap", "dumpsys battery set level 100 && echo \"Đã đặt pin 100%\""),
        new("Khôi phục pin thật", "refresh", "dumpsys battery reset && echo \"Đã khôi phục pin thật\""),
    ];

    void AdbMenu(PhoneDevice d)
    {
        var ids = MenuTargets(d);
        var n = ids.Count > 1 ? T(" — {0} máy", ids.Count) : "";
        var items = AdbPresets.Select(p => p == null ? null : new MenuSpec(p.Label, p.Icon, () =>
        {
            if (p.Confirm != null && !Ui.Confirm(p.Label, T(p.Confirm) + "\n" + T("Thực hiện trên {0} máy?", ids.Count), "Thực hiện", true)) return;
            Batch("shell", ids, new() { ["cmd"] = p.Cmd }, T(p.Label) + n, p.Show);
        })).ToList();
        items.Add(null);
        items.Add(new MenuSpec("Lệnh tuỳ chỉnh…", "terminal", () => DlgShell(ids)));
        items.Add(new MenuSpec("Khởi động lại máy…", "power", () =>
        {
            if (Ui.Confirm("Khởi động lại", T("Khởi động lại {0} máy?", ids.Count), "Khởi động lại", true)) Batch("reboot", ids, null, "Khởi động lại");
        }, Danger: true));
        Ui.ShowMenu(items);
    }

    void TileMenu(PhoneDevice d)
    {
        if (d.Pending) { Ui.ShowMenu([new MenuSpec(d.Error != "" ? d.Error : d.Status, "info", null)]); return; }
        List<string> one = [d.Id];
        var ids = MenuTargets(d);
        var n = ids.Count > 1 ? T(" ({0} máy)", ids.Count) : "";
        void ShellOn(string cmd, string title) => Batch("shell", ids, new() { ["cmd"] = cmd }, T(title) + n);
        Ui.ShowMenu(
        [
            new(T("Phóng to #{0}", d.Num), "maximize", () => OpenViewer(d.Id)),
            new(T("Cài APK") + n + "…", "package", () => PickApks(ids)),
            new(T("Nhập tệp vào máy") + n + "…", "upload", () => DlgPush(null, ids)),
            new("Xuất tệp ra máy tính…", "download", () => DlgFiles(d)),
            new(T("Lệnh ADB") + n + "  ▸", "terminal", () => Dispatcher.BeginInvoke(() => AdbMenu(d))),
            new(T("Mở cài đặt mạng") + n, "wifi", () => ShellOn("am start -a android.settings.WIRELESS_SETTINGS 2>&1 | tail -1", "Cài đặt mạng")),
            new(T("Mở Cài đặt") + n, "settings", () => ShellOn("am start -a android.settings.SETTINGS 2>&1 | tail -1", "Cài đặt")),
            d.Paused ? new(T("Tiếp tục xem") + n, "play", () => SetPaused(ids, false)) : new(T("Tạm dừng xem") + n, "pause", () => SetPaused(ids, true)),
            d.PcKeyboardOn
                ? new(T("Bàn phím điện thoại: BẬT lại") + n, "keyboard", () => Batch("phonekbd", ids, null, "Bật lại bàn phím điện thoại", true))
                : new(T("Bàn phím điện thoại: TẮT (chỉ nhập từ PC)") + n, "keyboard", () => Batch("pckbd", ids, null, "Chỉ nhập từ PC", true)),
            new(ids.Count > 1 ? T("Chỉ hiển thị nhóm này ({0} máy)", ids.Count) : T("Chỉ hiển thị máy này"), "eye", () => SetSolo(ids)),
            Store.State.Solo != null ? new MenuSpec("Hiện lại tất cả máy", "grid", () => SetSolo(null)) : null,
            null,
            new("Đổi tên…", "type", () => RenameDevice(d)),
            new("Đổi số thứ tự…", "hash", () => RenumberDevice(d)),
            new("Thêm vào nhóm…", "tag", () => DlgGroup(one, false)),
            new("Ứng dụng…", "apps", () => DlgApps(one, d.Id)),
            new("Thông tin", "info", () => DlgInfo(d)),
            new("Xem ảnh chụp màn hình", "camera", () => _ = SaveShot(d, open: true)),
            new("Mở bằng scrcpy gốc", "monitor", () => OpenScrcpy(d)),
            null,
            d.HasWifi ? new("WiFi → USB", "usb", () => ToUsb(one)) : new("USB → WiFi", "wifi", () => ToWifi(one)),
            new("Kết nối lại luồng hình", "refresh", () => manager.RestartSessions(d)),
            new("Khởi động lại máy", "power", () =>
            {
                if (Ui.Confirm("Khởi động lại", T("Khởi động lại máy \"{0}\"?", d.DisplayName), "Khởi động lại", true)) Batch("reboot", one, null, "Khởi động lại");
            }, Danger: true),
        ]);
    }

    // ---------------- thao tác nhỏ ----------------
    /// <summary>Tạm dừng xem: điện thoại ngừng gửi hình nhưng vẫn nhận điều khiển & đồng bộ.</summary>
    void SetPaused(List<string> ids, bool paused)
    {
        if (ids.Count == 0) return;
        manager.SetPaused(ids, paused, out var n);
        Ui.Toast(paused ? T("Đã tạm dừng xem {0} máy (vẫn điều khiển được)", n) : T("Đã tiếp tục xem {0} máy", n), "ok");
        orderSig = ""; RenderAll();
    }

    void RenameDevice(PhoneDevice d)
    {
        var v = Ui.Prompt("Đổi tên máy", T("Tên hiển thị cho #{0} ({1})", d.Num, d.Model), d.Label, d.Model);
        if (v == null) return;
        Store.Meta(d.Id).Label = v.Trim()[..Math.Min(40, v.Trim().Length)];
        Store.Save();
        d.Changed(); orderSig = ""; RenderAll();
    }

    void RenumberDevice(PhoneDevice d)
    {
        var v = Ui.Prompt("Đổi số thứ tự", "Số thứ tự mới", d.Num.ToString());
        if (v == null) return;
        if (!int.TryParse(v, out var n) || n <= 0) { Ui.Toast("Số không hợp lệ", "err"); return; }
        var meta = Store.Meta(d.Id);
        // số đã có máy khác dùng → 2 máy đổi số cho nhau (tránh 2 máy trùng số)
        foreach (var (id, m) in Store.State.Devices) if (id != d.Id && m.Num == n) { m.Num = meta.Num; manager.Get(id)?.Changed(); }
        meta.Num = n;
        Store.State.NextNum = Math.Max(Store.State.NextNum, n + 1);
        Store.Save();
        d.Changed(); orderSig = ""; RenderAll();
    }

    void RenumberAll()
    {
        var ids = VisibleDevices().Where(d => !d.Pending).Select(d => d.Id).ToList();
        if (ids.Count == 0) return;
        if (!Ui.Confirm("Đánh số lại", T("Đánh số lại {0} máy theo thứ tự đang hiển thị (1 → {1})?", ids.Count, ids.Count))) return;
        int n = 1;
        foreach (var id in ids) Store.Meta(id).Num = n++;
        Store.State.NextNum = Math.Max(n, Store.State.Devices.Values.Max(x => x.Num) + 1);
        Store.Save();
        foreach (var d in manager.Devices) d.Changed();
        orderSig = ""; RenderAll();
        Ui.Toast("Đã đánh số lại", "ok");
    }

    void SendClipboard()
    {
        var v = SelOnline();
        if (v.Count == 0) return;
        if (!Clipboard.ContainsText()) { Ui.Toast("Không đọc được clipboard", "err"); return; }
        var text = Clipboard.GetText();
        foreach (var d in v) d.ControlSession?.Send(Scrcpy.ControlMessages.SetClipboardMsg(text, false));
        Ui.Toast(T("Đã gửi clipboard tới {0} máy", v.Count), "ok");
    }

    void RebootSel()
    {
        var ids = SelIds();
        if (ids.Count == 0) return;
        if (Ui.Confirm("Khởi động lại", T("Khởi động lại {0} máy?", ids.Count), "Khởi động lại", true)) Batch("reboot", ids, null, "Khởi động lại");
    }

    void ToWifi(List<string> ids)
    {
        if (ids.Count == 0) return;
        // adb tcpip khởi động lại adbd: máy root được cấy khoá tự động; máy không root chỉ an toàn nếu đã "Luôn cho phép"
        int noRoot = ids.Count(id => manager.Get(id)?.Root != true);
        if (noRoot > 0 && !Ui.Confirm("USB → WiFi", T("{0} máy không root: chuyển WiFi sẽ khởi động lại ADB trên máy. Nếu lúc cho phép gỡ lỗi chưa tick \"Luôn cho phép từ máy tính này\", máy sẽ hỏi uỷ quyền lại (máy không màn hình sẽ mất kết nối). Tiếp tục?", noRoot), "Chuyển WiFi", true)) return;
        Batch("tcpip", ids, null, "USB → WiFi", true);
    }

    void ToUsb(List<string> ids)
    {
        if (ids.Count == 0) return;
        // máy chỉ có WiFi (không cắm cáp) sẽ mất kết nối cho tới khi cắm USB
        int wifiOnly = ids.Count(id => manager.Get(id)?.HasUsb != true);
        if (wifiOnly > 0 && !Ui.Confirm("WiFi → USB", T("{0} máy chưa cắm cáp USB sẽ mất kết nối cho tới khi cắm cáp. Tiếp tục?", wifiOnly), "Chuyển về USB", true)) return;
        Batch("tousb", ids, null, "WiFi → USB", true);
    }

    void RootAdbWifi()
    {
        var ids = SelIds();
        if (ids.Count == 0) return;
        if (Ui.Confirm("ADB WiFi cố định (root)", T("Bật ADB qua WiFi cố định (cổng 5555) trên {0} máy có root? ADB sẽ khởi động lại (mất kết nối vài giây), sau đó có thể kết nối WiFi ngay cả sau khi khởi động lại máy.", ids.Count)))
            Batch("rootadbwifi", ids, null, "ADB WiFi cố định", true);
    }
}
