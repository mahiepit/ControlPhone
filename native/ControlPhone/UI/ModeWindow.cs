using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ControlPhone.Adb;
using ControlPhone.Core;

namespace ControlPhone;

/// <summary>
/// Màn hình chọn chế độ khi mở app: (1) Điều khiển tất cả máy — như trước, kết nối cả dàn; (2) Chỉ hiển thị 1 điện thoại —
/// chọn 1 máy trong danh sách, app chỉ kết nối máy đó (các máy khác không mở luồng) và hiện 1 màn hình lớn.
/// </summary>
public sealed class ModeWindow : Window
{
    /// <summary>null = điều khiển tất cả; ngược lại = serial USB / địa chỉ của máy được chọn.</summary>
    public string? SingleAddr { get; private set; }
    public bool Chosen { get; private set; }

    static string T(string s, params object[] a) => I18n.T(s, a);

    readonly Grid root = new();
    readonly CancellationTokenSource cts = new();
    readonly Dictionary<string, string> models = [];
    List<AdbDevice> devices = [];
    StackPanel? listPanel;
    TextBox? search;

    public ModeWindow(bool startWithList = false)
    {
        Title = "ControlPhone";
        Width = 760; Height = 560; MinWidth = 600; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        UseLayoutRounding = true;
        Content = root;
        Loaded += (_, _) => Ui.DarkTitleBar(this);
        Closed += (_, _) => cts.Cancel();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && listPanel != null && !startWithList) { e.Handled = true; ShowChoices(); } };
        if (startWithList) ShowList(); else ShowChoices();
    }

    // ---------- bước 1: 2 lựa chọn lớn ----------
    void ShowChoices()
    {
        listPanel = null;
        root.Children.Clear();
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24) };
        sp.Children.Add(new TextBlock { Text = "ControlPhone", FontSize = 24, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
        var sub = new TextBlock { Text = T("Chọn cách làm việc"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 22) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        sp.Children.Add(sub);
        var row = new UniformGrid { Columns = 2 };
        row.Children.Add(Card("", T("Điều khiển tất cả máy"), T("Kết nối cả dàn điện thoại, xem lưới, điều khiển đồng bộ nhiều máy cùng lúc."), () => Finish(null)));
        row.Children.Add(Card("", T("Chỉ hiển thị 1 điện thoại"), T("Chọn 1 máy trong danh sách — chỉ kết nối máy đó, hiện 1 màn hình lớn, mượt nhất."), ShowList));
        sp.Children.Add(row);
        root.Children.Add(sp);
    }

    Border Card(string icon, string title, string text, Action onClick)
    {
        var b = new Border { Width = 300, Margin = new Thickness(10), Padding = new Thickness(22, 24, 22, 24), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
        b.SetResourceReference(Border.BackgroundProperty, "Bg2");
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        var sp = new StackPanel();
        var ic = new TextBlock { Text = icon, FontSize = 34, Margin = new Thickness(0, 0, 0, 14) };
        ic.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        ic.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        sp.Children.Add(ic);
        sp.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        sp.Children.Add(t);
        b.Child = sp;
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BorderBrushProperty, "Accent");
        b.MouseLeave += (_, _) => b.SetResourceReference(Border.BorderBrushProperty, "Line");
        b.MouseLeftButtonUp += (_, _) => onClick();
        return b;
    }

    // ---------- bước 2: danh sách máy ----------
    void ShowList()
    {
        root.Children.Clear();
        var dock = new DockPanel { Margin = new Thickness(18) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var back = Ui.Btn(T("Quay lại"), "back", "Ghost", (_, _) => ShowChoices());
        var all = Ui.Btn(T("Điều khiển tất cả máy"), "grid", "Btn", (_, _) => Finish(null));
        DockPanel.SetDock(back, Dock.Left); DockPanel.SetDock(all, Dock.Right);
        head.Children.Add(back); head.Children.Add(all);
        head.Children.Add(new TextBlock { Text = T("Chọn điện thoại muốn hiển thị"), FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) });
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        search = new TextBox { Tag = T("Tìm số, tên, model, serial…"), Margin = new Thickness(0, 0, 0, 10) };
        search.TextChanged += (_, _) => RenderList();
        DockPanel.SetDock(search, Dock.Top);
        dock.Children.Add(search);
        listPanel = new StackPanel();
        dock.Children.Add(new ScrollViewer { Content = listPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(dock);
        RenderList();
        Dispatcher.BeginInvoke(() => search.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        StartTracking();
    }

    bool tracking;
    void StartTracking()
    {
        if (tracking) return;
        tracking = true;
        _ = Task.Run(async () =>
        {
            await AdbClient.StartServerAsync();
            await AdbClient.TrackDevicesAsync(l => Dispatcher.BeginInvoke(() => { devices = l.ToList(); FetchModels(); RenderList(); }), null, cts.Token);
        });
    }

    // đọc model từng máy (song song, 1 lần) để danh sách dễ nhận. Máy qua mạng (WiFi / OTG-LAN, địa chỉ là IP:cổng) còn đọc
    // serial USB thật (cần root, giống DeviceManager.ProbeAsync) → tra đúng số thứ tự của máy đó trong config.
    void FetchModels()
    {
        foreach (var d in devices.Where(x => x.State == "device" && !models.ContainsKey(x.Addr)))
        {
            models[d.Addr] = "";
            var addr = d.Addr;
            bool net = AdbClient.IsWifiSerial(addr);
            _ = Task.Run(async () =>
            {
                string m = "", sn = "";
                try
                {
                    var o = await AdbClient.ShellAsync(addr, "getprop ro.product.model" + (net ? "; echo \"USBSN=$(su -c 'cat /config/usb_gadget/*/strings/0x409/serialnumber /sys/class/android_usb/android0/iSerial' 2>/dev/null | head -n1)\"" : ""), 10000, cts.Token);
                    var lines = o.Split('\n').Select(s => s.Trim()).ToArray();
                    m = lines.FirstOrDefault(s => !s.StartsWith("USBSN=")) ?? "";
                    sn = lines.FirstOrDefault(s => s.StartsWith("USBSN="))?[6..] ?? "";
                }
                catch { }
                await Dispatcher.InvokeAsync(() => { models[addr] = m; if (sn.Length >= 6) usbSerials[addr] = sn; RenderList(); });
            });
        }
    }

    readonly Dictionary<string, string> usbSerials = [];

    /// <summary>
    /// Thông tin (số thứ tự, tên) của máy trong config. Khoá config là serial USB, nên máy qua mạng phải tra qua serial USB
    /// thật đọc từ máy, hoặc địa chỉ mạng đã nhớ (DeviceMeta.Wifi) — trước đây chỉ tra theo địa chỉ nên máy LAN/WiFi hiện "#—".
    /// </summary>
    DeviceMeta? MetaOf(string addr)
    {
        var all = Store.State.Devices;
        if (!AdbClient.IsWifiSerial(addr))
        {
            var serial = AdbClient.SplitAddr(addr).Serial;
            return all.GetValueOrDefault(serial) ?? all.FirstOrDefault(kv => kv.Key.StartsWith(serial + "#", StringComparison.Ordinal)).Value;
        }
        if (usbSerials.TryGetValue(addr, out var sn))
        {
            var m = all.GetValueOrDefault(sn) ?? all.FirstOrDefault(kv => kv.Key.StartsWith(sn + "#", StringComparison.Ordinal)).Value;
            if (m != null) return m;
        }
        // chưa đọc được serial USB (máy không root / đang đọc): địa chỉ đã nhớ — chỉ nhận khi đúng 1 máy khớp (IP gắn theo
        // cổng box, có thể từng thuộc máy khác)
        var hits = all.Values.Where(m => m.Wifi == addr).Take(2).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    void RenderList()
    {
        if (listPanel == null) return;
        listPanel.Children.Clear();
        var q = (search?.Text ?? "").Trim().ToLowerInvariant();
        var last = Store.Ui.LastSingle;
        var rows = devices.Select(d =>
        {
            var meta = MetaOf(d.Addr);
            return (Dev: d, Num: meta?.Num ?? 0, Label: meta?.Label ?? "", Model: models.GetValueOrDefault(d.Addr) ?? "");
        })
        .Where(r => q == "" || $"{(r.Num > 0 ? $"#{r.Num} h{r.Num}" : "")} {r.Label} {r.Model} {r.Dev.Addr} {usbSerials.GetValueOrDefault(r.Dev.Addr)}".ToLowerInvariant().Contains(q))
        .OrderBy(r => r.Dev.Addr == last ? 0 : 1).ThenBy(r => r.Num == 0 ? int.MaxValue : r.Num).ThenBy(r => r.Dev.Addr)
        .ToList();
        if (devices.Count == 0 || rows.Count == 0)
        {
            var t = new TextBlock { Text = devices.Count == 0 ? T("Đang tìm điện thoại… (cắm USB và bật Gỡ lỗi USB)") : T("Không có máy nào khớp."), Margin = new Thickness(6, 20, 6, 0), HorizontalAlignment = HorizontalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            listPanel.Children.Add(t);
            return;
        }
        foreach (var r in rows) listPanel.Children.Add(Row(r.Dev, r.Num, r.Label, r.Model, r.Dev.Addr == last));
    }

    Border Row(AdbDevice d, int num, string label, string model, bool isLast)
    {
        bool ready = d.State == "device";
        var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 6), BorderThickness = new Thickness(1), Cursor = ready ? Cursors.Hand : Cursors.Arrow };
        b.SetResourceReference(Border.BackgroundProperty, "Bg2");
        b.SetResourceReference(Border.BorderBrushProperty, isLast ? "Accent" : "Line");
        var dp = new DockPanel();
        var state = new TextBlock { Text = ready ? (AdbClient.IsWifiSerial(d.Addr) ? "WiFi" : "USB") : d.State, VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
        state.SetResourceReference(TextBlock.ForegroundProperty, ready ? "Ok" : "Warn");
        DockPanel.SetDock(state, Dock.Right);
        dp.Children.Add(state);
        var numT = new TextBlock { Text = num > 0 ? $"#{num:00}" : "#—", FontWeight = FontWeights.Bold, FontSize = 16, Width = 66, VerticalAlignment = VerticalAlignment.Center };
        numT.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        dp.Children.Add(numT);
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = label != "" ? label : model != "" ? model : d.Addr, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var sn = usbSerials.GetValueOrDefault(d.Addr);
        var s2 = new TextBlock { Text = (label != "" && model != "" ? model + " · " : "") + d.Addr + (sn != null ? "  ·  SN " + sn : "") + (isLast ? "  ·  " + T("dùng lần trước") : ""), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        s2.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        name.Children.Add(s2);
        dp.Children.Add(name);
        b.Child = dp;
        if (ready)
        {
            b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "Hover");
            b.MouseLeave += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "Bg2");
            b.MouseLeftButtonUp += (_, _) => Finish(d.Addr);
        }
        return b;
    }

    void Finish(string? single)
    {
        SingleAddr = single;
        Chosen = true;
        if (single != null) { Store.Ui.LastSingle = single; Store.SaveUi(); }
        Close();
    }
}
