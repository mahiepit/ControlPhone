using System.Windows;
using System.Windows.Threading;
using ControlPhone.Core;

namespace ControlPhone;

public partial class MainWindow
{
    /// <summary>Kịch bản tự kiểm tra giao diện (CP_SELFTEST): chỉ mở/chụp/đóng, không gửi thao tác nào tới điện thoại.</summary>
    async Task RunSelfTest()
    {
        async Task Idle(int ms = 400) { await Task.Delay(ms); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
        async Task Step(string name, Action a)
        {
            try { a(); await Idle(); SelfTest.Log("ran " + name); }
            catch (Exception e) { SelfTest.Log($"ERR {name}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
        }
        // chờ máy online
        for (int i = 0; i < 60 && manager.Devices.Count(d => d.IsOnline) < Math.Max(1, manager.Devices.Count(d => !d.Pending)); i++) await Task.Delay(1000);
        await Task.Delay(4000);
        SelfTest.Capture(this, "01_grid");
        var d = manager.Devices.First(x => x.IsOnline);
        var d2 = manager.Devices.Where(x => x.IsOnline).Skip(1).First();

        await Step("select", () => { selected.Clear(); selected.Add(d.Id); selected.Add(d2.Id); RefreshSelection(); });
        await Idle(800);
        SelfTest.Capture(this, "02_selected_sync");
        await Step("viewer", () => OpenViewer(d.Id));
        await Task.Delay(5000);
        SelfTest.Capture(this, "03_viewer");
        await Step("viewer next", () => StepViewer(1));
        await Task.Delay(4000);
        SelfTest.Capture(this, "04_viewer_next");
        await Step("viewer close", CloseViewer);
        await Task.Delay(4000);
        SelfTest.Capture(this, "05_viewer_closed");

        // menu (chỉ dựng, không mở)
        await Step("menus", () => { Ui.Menu(MoreMenu()); TileMenuSpecsCheck(d); });
        // hộp thoại
        await Step("DlgText", DlgText);
        await Step("DlgUrl", DlgUrl);
        await Step("DlgShell", () => DlgShell(null));
        await Step("DlgBrightness", DlgBrightness);
        await Step("DlgPush", () => DlgPush(null, null));
        await Step("DlgGroup", () => DlgGroup([d.Id], false));
        await Step("DlgInfo", () => DlgInfo(d));
        await Step("DlgSettings", DlgSettings);
        await Step("DlgScan", DlgScan);
        await Step("DlgPair", DlgPair);
        await Step("DlgOtg", DlgOtg);
        await Step("DlgDonate", DlgDonate);
        await Step("DlgDiag", DlgDiag);
        // hộp thoại không chặn (ứng dụng / tệp): mở, chờ tải, chụp, đóng
        foreach (var (name, open) in new (string, Action)[] { ("apps", () => DlgApps([d.Id], d.Id)), ("files", () => DlgFiles(d)) })
        {
            try
            {
                var before = Application.Current.Windows.OfType<Dlg>().ToList();
                open();
                await Task.Delay(3500);
                foreach (var w in Application.Current.Windows.OfType<Dlg>().Except(before).ToList()) { SelfTest.Capture(w, "dlg_" + name); w.Close(); }
            }
            catch (Exception e) { SelfTest.Log($"ERR {name}: {e.Message}"); }
        }
        // thao tác hàng loạt chỉ đọc + bảng tiến trình + kết quả
        await Step("batch", () => Batch("shell", [d.Id, d2.Id], new() { ["cmd"] = "getprop ro.product.model" }, "Shell: getprop ro.product.model", true));
        await Task.Delay(4000);
        foreach (var w in Application.Current.Windows.OfType<Dlg>().ToList()) { SelfTest.Capture(w, "dlg_results"); w.Close(); }
        SelfTest.Capture(this, "06_tasks_panel");
        // chụp màn hình (đọc ảnh PNG từ điện thoại, không ghi clipboard của người dùng)
        try { var png = await CapturePng(d); SelfTest.Log($"screencap: {png.Length / 1024} KB"); } catch (Exception e) { SelfTest.Log("ERR screencap " + e.Message); }
        // bộ lọc / tìm kiếm / sắp xếp / cỡ ô / chỉ hiển thị / tạm dừng xem (trả lại như cũ sau khi thử)
        var origFilter = filter; var origW = PhoneTile.TileWidth;
        await Step("filter usb", () => SetFilter("usb")); SelfTest.Capture(this, "07_filter_usb"); await Step("filter back", () => SetFilter(origFilter));
        await Step("search", () => SearchBox.Text = d.Num.ToString()); SelfTest.Capture(this, "08_search"); await Step("search clear", () => SearchBox.Text = "");
        await Step("sort battery", () => SortSel.SelectedIndex = 3); await Step("sort num", () => SortSel.SelectedIndex = 0);
        await Step("fit", FitTiles); await Idle(600); SelfTest.Capture(this, "09_fit"); await Step("size back", () => SetTileW(origW));
        await Step("solo", () => SetSolo([d.Id, d2.Id])); await Task.Delay(4000); SelfTest.Capture(this, "10_solo");
        await Step("solo off", () => SetSolo(null)); await Task.Delay(3000);
        await Step("pause", () => SetPaused([d.Id], true)); await Task.Delay(3000); SelfTest.Capture(this, "11_paused");
        await Step("resume", () => SetPaused([d.Id], false)); await Task.Delay(3000);
        await Step("live off", () => SetLive(false)); SelfTest.Capture(this, "12_live_off"); await Step("live on", () => SetLive(true));
        // giao diện sáng
        await Step("theme light", () => { Theme.Apply("light"); UpdateSyncUi(); UpdateThemeIcon(); });
        await Idle(800);
        SelfTest.Capture(this, "13_light");
        await Step("DlgSettings light", DlgSettings);
        await Step("theme dark", () => { Theme.Apply(Store.Ui.Theme); UpdateSyncUi(); UpdateThemeIcon(); });
        await Task.Delay(2000);
        SelfTest.Capture(this, "14_end");
        SelfTest.Log("done");
        SelfTest.Flush();
        Close();
    }

    void TileMenuSpecsCheck(PhoneDevice d)
    {
        // dựng đủ các mục menu của ô và menu lệnh ADB (không mở popup)
        var ids = MenuTargets(d);
        _ = ids;
        Ui.Menu(AdbPresets.Select(p => p == null ? null : new MenuSpec(p.Label, p.Icon, null)));
    }
}
