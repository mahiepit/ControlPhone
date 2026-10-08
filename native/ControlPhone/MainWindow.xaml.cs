using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ControlPhone.Adb;
using ControlPhone.Core;
using ControlPhone.Video;

namespace ControlPhone;

public partial class MainWindow : Window
{
    readonly DeviceManager manager;
    readonly Actions actions;
    readonly ListCollectionView view;
    readonly HashSet<string> selected = [];
    string filter = Store.Ui.Filter;
    string search = "";
    string sort = Store.Ui.Sort;
    bool syncOff = Store.Ui.SyncOff;
    string orderSig = "";
    readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer statsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer tickTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    long lastBytes;
    readonly Stopwatch statsClock = Stopwatch.StartNew();
    static readonly string Version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "2";

    static string T(string s, params object[] a) => I18n.T(s, a);

    public MainWindow()
    {
        InitializeComponent();
        Ui.DarkTitleBar(this);
        if (SelfTest.On) { WindowState = WindowState.Normal; Left = -20000; Top = 0; Width = 1700; Height = 1000; ShowActivated = false; ShowInTaskbar = false; }
        manager = new DeviceManager(Dispatcher);
        actions = new Actions(manager, Dispatcher);
        foreach (var id in Store.Ui.Selected) selected.Add(id);

        view = (ListCollectionView)CollectionViewSource.GetDefaultView(manager.Devices);
        view.Filter = o => MatchFilter((PhoneDevice)o);
        view.CustomSort = new DeviceSorter(this);
        GridItems.ItemsSource = view;

        manager.ListChanged += () => refreshTimer.Start();
        manager.Log += (m, err) => Ui.Toast(m, err ? "err" : "");
        manager.Clipboard += OnPhoneClipboard;
        actions.TaskUpdated += OnTask;
        refreshTimer.Tick += (_, _) => { refreshTimer.Stop(); RenderAll(); };
        statsTimer.Tick += (_, _) => UpdateBandwidth();
        tickTimer.Tick += (_, _) => { UpdateVisibility(); manager.WatchTick(); };
        CompositionTarget.Rendering += OnRender;

        InitTopbar();
        InitSidebar();
        InitActionBar();
        InitViewer();
        InitInput();
        InitToasts();

        Loaded += (_, _) =>
        {
            I18n.Apply(this);
            SideFoot.Text = $"ControlPhone v{Version} (native)\nADB: {AdbClient.AdbPath}\nscrcpy-server 5.0";
            try { SideFoot.Text += $" · FFmpeg {Ffmpeg.VersionString(Ffmpeg.avcodec_version())}"; }
            catch (Exception e) { Ui.Toast("Không nạp được FFmpeg: " + e.Message, "err", 8000); }
            manager.Start();
            statsTimer.Start();
            tickTimer.Start();
            RenderAll();
            if (SelfTest.On) _ = RunSelfTest();
        };
        Closing += OnClosing;
    }

    // ---------------- vòng vẽ ----------------
    void OnRender(object? sender, EventArgs e)
    {
        foreach (var t in PhoneTile.Live) t.UpdateFrame();
        UpdateViewerFrame();
    }

    /// <summary>Ô nằm trong vùng nhìn thấy mới giải mã (100 máy vẫn nhẹ).</summary>
    void UpdateVisibility()
    {
        var vp = new Rect(0, -150, GridScroll.ActualWidth, GridScroll.ActualHeight + 300);
        var shown = new HashSet<PhoneDevice>();
        foreach (var t in PhoneTile.Live)
        {
            if (t.Device is not { } d || !t.IsVisible) continue;
            try
            {
                var r = t.TransformToAncestor(GridScroll).TransformBounds(new Rect(0, 0, t.ActualWidth, t.ActualHeight));
                if (r.IntersectsWith(vp)) shown.Add(d);
            }
            catch (InvalidOperationException) { }
        }
        foreach (var d in manager.Devices) manager.SetVisible(d, shown.Contains(d));
    }

    // ---------------- danh sách / lọc / sắp xếp ----------------
    sealed class DeviceSorter(MainWindow w) : IComparer
    {
        static int Rank(PhoneDevice d) => d.Status == "online" ? 0 : d.Status == "connecting" ? 1 : 2;
        public int Compare(object? x, object? y)
        {
            var a = (PhoneDevice)x!; var b = (PhoneDevice)y!;
            if (a.Pending != b.Pending) return a.Pending ? 1 : -1;
            int c = w.sort switch
            {
                "name" => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase),
                "status" => Rank(a) - Rank(b),
                "battery" => (a.Battery ?? 999) - (b.Battery ?? 999),
                _ => 0,
            };
            return c != 0 ? c : a.Num != b.Num ? a.Num - b.Num : string.CompareOrdinal(a.Id, b.Id);
        }
    }

    bool MatchFilter(PhoneDevice d)
    {
        if (d.SoloHidden) return false; // chế độ "Chỉ hiển thị các máy này"
        var f = filter;
        if (f == "sel" && !selected.Contains(d.Id)) return false;
        if (f == "usb" && d.Transport != "usb") return false;
        if (f == "wifi" && d.Transport != "wifi") return false;
        if (f == "bad" && d.IsOnline) return false;
        if (f == "paused" && !d.Paused) return false;
        if (f.StartsWith("group:") && !d.Groups.Contains(f[6..])) return false;
        if (search != "")
        {
            var hay = string.Join(' ', new[] { d.Num.ToString(), d.Label, d.Model, d.Serial, d.Ip, d.Id }.Concat(d.Transports.Keys)).ToLowerInvariant();
            if (!hay.Contains(search.ToLowerInvariant())) return false;
        }
        return true;
    }

    Func<PhoneDevice, bool> FilterFn(string k) => k switch
    {
        "all" => _ => true,
        "usb" => d => d.Transport == "usb",
        "wifi" => d => d.Transport == "wifi",
        "bad" => d => !d.IsOnline,
        "paused" => d => d.Paused,
        _ when k.StartsWith("group:") => d => d.Groups.Contains(k[6..]),
        _ => _ => false,
    };

    /// <summary>Danh sách ô đang hiển thị, đúng thứ tự trên màn hình.</summary>
    List<PhoneDevice> VisibleDevices() => view.Cast<PhoneDevice>().ToList();

    void RenderAll()
    {
        // chỉ dựng lại lưới khi thứ tự / bộ lọc thực sự đổi (dựng lại tạo lại ô)
        var sorter = new DeviceSorter(this);
        var list = manager.Devices.Where(MatchFilter).ToList();
        list.Sort((a, b) => sorter.Compare(a, b));
        var sig = string.Join(',', list.Select(x => x.Id));
        if (sig != orderSig) { orderSig = sig; view.Refresh(); }
        selected.RemoveWhere(id => manager.Get(id) is { SoloHidden: true }); // máy bị ẩn không còn nhận thao tác đồng bộ
        foreach (var d in manager.Devices) d.IsSelected = selected.Contains(d.Id);
        RenderSidebar();
        RenderStats();
        UpdateSoloUi();
        UpdateEmpty();
        if (viewerId != null) { if (manager.Get(viewerId) == null) CloseViewer(); else UpdateViewerHead(); }
    }

    void UpdateEmpty()
    {
        int shown = view.Count;
        if (manager.Devices.Count == 0)
        {
            EmptyIcon.Text = Icons.Get("phone");
            EmptyTitle.Text = T("Chưa có thiết bị");
            EmptyText.Text = T("Cắm điện thoại qua USB (bật Gỡ lỗi USB) hoặc kết nối WiFi ở thanh bên.");
        }
        else { EmptyIcon.Text = Icons.Get("search"); EmptyTitle.Text = ""; EmptyText.Text = T("Không có máy nào khớp bộ lọc."); }
        EmptyState.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void RenderStats()
    {
        var all = manager.Devices.Where(d => !d.Pending).ToList();
        StatOnline.Text = all.Count(d => d.IsOnline).ToString();
        StatTotal.Text = $"/{all.Count} online";
        StatSel.Text = T("{0} đã chọn", selected.Count);
        if (AbCount != null) AbCount.Text = T("Áp dụng cho {0} máy:", selected.Count);
        UpdateSyncUi();
        Store.Ui.Selected = selected.ToList();
        Store.SaveUi();
    }

    void UpdateBandwidth()
    {
        long bytes = 0;
        foreach (var d in manager.Devices) { bytes += d.Thumb?.BytesReceived ?? 0; bytes += d.Hq?.BytesReceived ?? 0; }
        double sec = statsClock.Elapsed.TotalSeconds; statsClock.Restart();
        double kbps = Math.Max(0, bytes - lastBytes) * 8 / sec / 1000;
        lastBytes = bytes;
        StatBw.Text = kbps >= 1000 ? $"{kbps / 1000:0.0} Mbps" : $"{kbps:0} kbps";
        RenderStats();
    }

    // ---------------- chọn máy ----------------
    void RefreshSelection()
    {
        foreach (var d in manager.Devices) d.IsSelected = selected.Contains(d.Id);
        if (filter == "sel") { orderSig = ""; RenderAll(); }
        RenderStats();
        RenderSidebarCounts();
        UpdateViewerHead();
    }

    void SelectOnly(string id)
    {
        if (selected.Count != 1 || !selected.Contains(id)) { selected.Clear(); selected.Add(id); }
        lastClickedId = id;
        RefreshSelection();
    }

    void ToggleSelect(string id, bool range)
    {
        if (range && lastClickedId != null)
        {
            var ids = VisibleDevices().Select(d => d.Id).ToList();
            int a = ids.IndexOf(lastClickedId), b = ids.IndexOf(id);
            if (a >= 0 && b >= 0)
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) if (manager.Get(ids[i]) is { Pending: false }) selected.Add(ids[i]);
        }
        else if (!selected.Remove(id)) selected.Add(id);
        lastClickedId = id;
        RefreshSelection();
    }

    void SelectWhere(Func<PhoneDevice, bool> fn, bool add = false)
    {
        if (!add) selected.Clear();
        foreach (var d in manager.Devices) if (!d.Pending && !d.SoloHidden && fn(d)) selected.Add(d.Id);
        RefreshSelection();
    }

    void ClearSelection() { selected.Clear(); RefreshSelection(); }

    bool SyncActive => !syncOff && selected.Count > 1;

    /// <summary>Máy chịu tác động khi thao tác trên máy d: cả nhóm nếu d thuộc nhóm đang đồng bộ.</summary>
    List<PhoneDevice> TargetsFor(PhoneDevice d)
    {
        var set = new List<PhoneDevice> { d };
        if (SyncActive && selected.Contains(d.Id))
            foreach (var id in selected) if (manager.Get(id) is { IsOnline: true } x && x != d) set.Add(x);
        return set;
    }

    List<string> MenuTargets(PhoneDevice d) => TargetsFor(d).Select(x => x.Id).ToList();

    List<string> SelIds(bool quiet = false)
    {
        var ids = selected.Where(id => manager.Get(id) is { Pending: false } d && d.Status != "offline").ToList();
        if (ids.Count == 0 && !quiet) Ui.Toast("Hãy chọn ít nhất 1 máy (click vào ô, kéo khung, hoặc Ctrl+A)", "err");
        return ids;
    }

    List<PhoneDevice> SelOnline() => SelIds().Select(manager.Get).Where(d => d is { IsOnline: true }).Cast<PhoneDevice>().ToList();

    // ---------------- thanh trên ----------------
    void InitTopbar()
    {
        BtnSidebar.Click += (_, _) => { Store.Ui.Sidebar = Sidebar.Visibility != Visibility.Visible; Sidebar.Visibility = Store.Ui.Sidebar ? Visibility.Visible : Visibility.Collapsed; Store.SaveUi(); };
        Sidebar.Visibility = Store.Ui.Sidebar ? Visibility.Visible : Visibility.Collapsed;
        BtnSync.Click += (_, _) => SetSyncOff(!syncOff);
        BtnSolo.Click += (_, _) => SetSolo(null);
        BtnLive.Click += (_, _) => SetLive(!PhoneDevice.Live);
        LiveLabel.Text = T("Đang xem");
        SizeSlider.Value = Math.Clamp(Store.Ui.TileW, 110, 520);
        PhoneTile.TileWidth = SizeSlider.Value;
        SizeSlider.ValueChanged += (_, e) => SetTileW(e.NewValue);
        BtnFit.Click += (_, _) => FitTiles();
        foreach (ComboBoxItem it in SortSel.Items) if ((string)it.Tag == sort) SortSel.SelectedItem = it;
        SortSel.SelectionChanged += (_, _) => { sort = (string)((ComboBoxItem)SortSel.SelectedItem).Tag; Store.Ui.Sort = sort; Store.SaveUi(); orderSig = ""; RenderAll(); };
        foreach (var (code, name) in I18n.Langs) LangSel.Items.Add(new ComboBoxItem { Content = name, Tag = code, IsSelected = code == I18n.Lang });
        LangSel.SelectionChanged += (_, _) =>
        {
            var code = (string)((ComboBoxItem)LangSel.SelectedItem).Tag;
            if (code == I18n.Lang) return;
            Store.Ui.Lang = code; Store.FlushUi();
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = false });
            Close();
        };
        BtnTheme.Click += (_, _) => { Theme.Apply(Theme.Current == "light" ? "dark" : "light"); Store.Ui.Theme = Theme.Current; Store.SaveUi(); UpdateThemeIcon(); UpdateSyncUi(); Ui.DarkTitleBar(this); };
        UpdateThemeIcon();
        BtnSettings.Click += (_, _) => DlgSettings();
        BtnDonate.Click += (_, _) => DlgDonate();
    }

    void UpdateThemeIcon() => BtnTheme.Content = Icons.Get(Theme.Current == "light" ? "moon" : "sun");

    void SetTileW(double w)
    {
        w = Math.Clamp(Math.Round(w), 110, 520);
        PhoneTile.TileWidth = w;
        foreach (var t in PhoneTile.Live) t.SetTileWidth(w);
        if (Math.Abs(SizeSlider.Value - w) > 0.5) SizeSlider.Value = w;
        Store.Ui.TileW = w; Store.SaveUi();
    }

    void FitTiles()
    {
        int n = Math.Max(1, view.Count);
        // vùng lưới (trừ padding 6px mỗi bên + thanh cuộn)
        double W = GridScroll.ActualWidth - 12 - 12, H = GridScroll.ActualHeight - 12;
        // một ô rộng w: chiếm (w + 10) ngang; cao = (w - 14) × tỉ lệ + 36 (lề 10 + viền/đệm 4 + tiêu đề 22)
        const double ratio = 2.055;
        double best = 110;
        for (int cols = 1; cols <= n; cols++)
        {
            int rows = (int)Math.Ceiling(n / (double)cols);
            double byW = W / cols - 10;
            double byH = (H / rows - 36) / ratio + 14;
            best = Math.Max(best, Math.Min(byW, byH));
        }
        SetTileW(Math.Floor(best) - 1);
    }

    void SetSyncOff(bool off)
    {
        syncOff = off;
        Store.Ui.SyncOff = off; Store.SaveUi();
        if (!off && selected.Count < 2) Ui.Toast("Đồng bộ tự bật khi chọn nhóm từ 2 máy (Ctrl+click)");
        RenderStats();
        UpdateViewerHead();
    }

    void UpdateSyncUi()
    {
        bool on = SyncActive;
        SyncSw.Background = Theme.Brush(on ? "Warn" : syncOff ? "Bad" : "Line");
        SyncKnob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        SyncLabel.Text = T("Đồng bộ") + (on ? $" ({selected.Count})" : syncOff ? T(": tắt") : "");
        BtnSync.ToolTip = syncOff
            ? T("Đồng bộ đang TẮT — thao tác chỉ áp dụng cho máy đang chạm. Bấm (F3) để bật lại.")
            : T("Tự động đồng bộ khi chọn từ 2 máy trở lên (Ctrl+click để chọn nhóm). Bấm (F3) để tắt tạm.");
        Application.Current.Resources["SelBrush"] = Theme.Brush(on ? "Warn" : "Accent");
    }

    void SetLive(bool on)
    {
        manager.SetLive(on);
        LiveIcon.Text = Icons.Get(on ? "eye" : "pause");
        LiveLabel.Text = T(on ? "Đang xem" : "Đã tạm dừng");
        BtnLive.SetResourceReference(StyleProperty, on ? "Ghost" : "Primary");
    }

    // ---------------- chỉ hiển thị một số máy ----------------
    void SetSolo(IReadOnlyCollection<string>? ids)
    {
        if (ids is { Count: > 0 })
        {
            if (Store.State.Solo == null) { Store.Ui.TileWBeforeSolo = PhoneTile.TileWidth; Store.SaveUi(); }
            manager.SetSolo(ids);
            Ui.Toast(T("Chỉ hiển thị {0} máy — các máy khác đã ẩn và tạm dừng xem", ids.Count), "ok");
            selected.RemoveWhere(id => manager.Get(id)?.SoloHidden == true);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, FitTiles);
        }
        else
        {
            manager.SetSolo(null);
            Ui.Toast("Đã hiển thị lại tất cả máy", "ok");
            if (Store.Ui.TileWBeforeSolo > 0) SetTileW(Store.Ui.TileWBeforeSolo);
        }
        orderSig = "";
        RenderAll();
    }

    void UpdateSoloUi()
    {
        var solo = Store.State.Solo;
        BtnSolo.Visibility = solo is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (solo != null) SoloLabel.Text = T("Chỉ hiển thị {0} máy", solo.Count) + "  ✕";
    }

    // ---------------- thanh bên ----------------
    readonly Dictionary<string, TextBlock> filterCounts = [];

    void InitSidebar()
    {
        SearchBox.TextChanged += (_, _) => { search = SearchBox.Text.Trim(); orderSig = ""; RenderAll(); };
        BtnNewGroup.Click += (_, _) => NewGroup();
        BtnConnect.Click += async (_, _) => await ConnectAddr();
        ConnAddr.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await ConnectAddr(); };
        BtnConnHist.Click += (_, _) =>
        {
            var hist = Store.State.WifiHistory.AsEnumerable().Reverse().Take(20).ToList();
            if (hist.Count == 0) { Ui.Toast("Chưa có địa chỉ nào"); return; }
            Ui.ShowMenu(hist.Select(h => (MenuSpec?)new MenuSpec(h, "wifi", () => ConnAddr.Text = h)), ConnAddr);
        };
        void Add(string text, string icon, Action a) { var b = Ui.Btn(text, icon, "Btn", (_, _) => a()); b.HorizontalContentAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 0, 0, 6); ConnPanel.Children.Add(b); }
        Add("USB → WiFi (máy đã chọn)", "wifi", () => ToWifi(SelIds()));
        Add("Quét mạng LAN", "scan", DlgScan);
        Add("Ghép nối mã (Android 11+)", "key", DlgPair);
        Add("Chế độ OTG (scrcpy)", "usb", DlgOtg);
        Add("Khởi động lại ADB", "refresh", AdbRestart);
        Add("Chẩn đoán kết nối", "info", DlgDiag);
        Add("Xoá máy mất kết nối", "eraser", () => { manager.RemoveOffline(); Ui.Toast("Đã xoá các máy mất kết nối khỏi danh sách"); });
    }

    async Task ConnectAddr()
    {
        var addr = ConnAddr.Text.Trim();
        if (addr == "") { Ui.Toast("Nhập địa chỉ IP (vd 192.168.1.10:5555)", "err"); return; }
        var r = await actions.ConnectAsync(addr);
        Ui.Toast(r.Msg != "" ? r.Msg : r.Ok ? "Đã kết nối" : "Lỗi", r.Ok ? "ok" : "err", 5000);
    }

    async void AdbRestart()
    {
        if (!Ui.Confirm("Khởi động lại ADB", "Tất cả kết nối sẽ bị ngắt và tự kết nối lại sau vài giây.", "Khởi động lại")) return;
        await AdbClient.RunAsync(["kill-server"], 15000);
        await AdbClient.StartServerAsync();
        Ui.Toast("ADB đã khởi động lại", "ok");
    }

    void RenderSidebar()
    {
        var devs = manager.Devices.Where(d => !d.SoloHidden).ToList();
        int Cnt(Func<PhoneDevice, bool> f) => devs.Count(f);
        (string Key, string Icon, string Label, int N)[] filters =
        [
            ("all", "grid", "Tất cả", devs.Count),
            ("sel", "checksq", "Đang chọn", selected.Count),
            ("usb", "usb", "USB", Cnt(d => d.Transport == "usb")),
            ("wifi", "wifi", "WiFi", Cnt(d => d.Transport == "wifi")),
            ("bad", "plug", "Lỗi / mất kết nối", Cnt(d => !d.IsOnline)),
            ("paused", "pause", "Đang tạm dừng xem", Cnt(d => d.Paused)),
        ];
        FiltersPanel.Children.Clear();
        filterCounts.Clear();
        foreach (var (k, ic, label, n) in filters) FiltersPanel.Children.Add(FilterRow(k, ic, T(label), n, k != "sel", null));
        GroupsPanel.Children.Clear();
        if (Store.State.Groups.Count == 0)
        {
            var tb = new TextBlock { Text = T("Chưa có nhóm. Chọn máy → ⋯ → Thêm vào nhóm."), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 2, 4, 2) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            GroupsPanel.Children.Add(tb);
        }
        foreach (var g in Store.State.Groups) GroupsPanel.Children.Add(FilterRow("group:" + g, "tag", g, Cnt(d => d.Groups.Contains(g)), true, g));
    }

    void RenderSidebarCounts() { if (filterCounts.TryGetValue("sel", out var t)) t.Text = selected.Count.ToString(); }

    FrameworkElement FilterRow(string key, string icon, string label, int n, bool selectBtn, string? group)
    {
        var row = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 5, 6, 5), Margin = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand };
        row.SetResourceReference(Border.BackgroundProperty, filter == key ? "AccentSoft" : "Bg2");
        var dp = new DockPanel();
        var cnt = new TextBlock { Text = n.ToString(), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        cnt.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        filterCounts[key] = cnt;
        var acts = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        if (selectBtn)
        {
            var b = new Button { Content = T("chọn"), FontSize = 10.5, Padding = new Thickness(6, 0, 6, 0), MinHeight = 20, Margin = new Thickness(4, 0, 0, 0), ToolTip = T(group != null ? "Chọn cả nhóm" : "Chọn tất cả máy trong mục này") };
            b.Click += (_, e) => { e.Handled = true; SelectWhere(FilterFn(key)); Ui.Toast(T("Đã chọn {0} máy", selected.Count)); };
            acts.Children.Add(b);
        }
        if (group != null)
        {
            var del = new Button { Content = Icons.Get("x"), FontSize = 9, Padding = new Thickness(5, 0, 5, 0), MinHeight = 20, Margin = new Thickness(3, 0, 0, 0), ToolTip = T("Xoá nhóm") };
            del.SetResourceReference(FontFamilyProperty, "IconFont");
            del.Click += (_, e) =>
            {
                e.Handled = true;
                if (!Ui.Confirm("Xoá nhóm", T("Xoá nhóm \"{0}\"? (Không ảnh hưởng tới máy)", group), "Xoá", true)) return;
                Store.State.Groups.Remove(group);
                foreach (var m in Store.State.Devices.Values) m.Groups.Remove(group);
                Store.Save();
                if (filter == "group:" + group) SetFilter("all"); else RenderAll();
            };
            acts.Children.Add(del);
        }
        DockPanel.SetDock(acts, Dock.Right); DockPanel.SetDock(cnt, Dock.Right);
        dp.Children.Add(acts); dp.Children.Add(cnt);
        var ic = Ui.Icon(icon, 13, "Muted"); ic.Margin = new Thickness(0, 0, 8, 0);
        dp.Children.Add(ic);
        dp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        row.Child = dp;
        row.MouseEnter += (_, _) => { acts.Visibility = Visibility.Visible; if (filter != key) row.SetResourceReference(Border.BackgroundProperty, "Hover"); };
        row.MouseLeave += (_, _) => { acts.Visibility = Visibility.Collapsed; row.SetResourceReference(Border.BackgroundProperty, filter == key ? "AccentSoft" : "Bg2"); };
        row.MouseLeftButtonUp += (_, _) => SetFilter(key);
        return row;
    }

    void SetFilter(string f)
    {
        filter = f;
        Store.Ui.Filter = f; Store.SaveUi();
        orderSig = "";
        RenderAll();
    }

    void NewGroup()
    {
        var name = Ui.Prompt("Tạo nhóm", "Tên nhóm", "", "vd: Nhóm A")?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        name = name[..Math.Min(30, name.Length)];
        if (!Store.State.Groups.Contains(name)) Store.State.Groups.Add(name);
        if (selected.Count > 0 && Ui.Confirm("Thêm máy", T("Thêm {0} máy đang chọn vào nhóm \"{1}\"?", selected.Count, name), "Thêm"))
            GroupAdd(selected.ToList(), name, remove: false);
        Store.Save();
        RenderAll();
    }

    void GroupAdd(List<string> ids, string name, bool remove)
    {
        if (!remove && !Store.State.Groups.Contains(name)) Store.State.Groups.Add(name);
        foreach (var id in ids)
        {
            var m = Store.Meta(id);
            m.Groups.Remove(name);
            if (!remove) m.Groups.Add(name);
            manager.Get(id)?.Changed();
        }
        Store.Save();
        orderSig = "";
        RenderAll();
    }

    // ---------------- thoát ----------------
    bool closingDone;
    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closingDone) return;
        e.Cancel = true;
        Hide();
        CompositionTarget.Rendering -= OnRender;
        if (viewerId != null && manager.Get(viewerId) is { } vd) manager.ReleaseHq(vd);
        manager.Dispose();
        await Task.Delay(1500); // để điện thoại kịp dọn (bật lại màn hình thật…) trước khi cắt adb
        bool scrcpyOpen;
        lock (actions.ScrcpyChildren) scrcpyOpen = actions.ScrcpyChildren.Any(p => !p.HasExited);
        if (Store.Settings.StopAdbOnExit && !scrcpyOpen && !SelfTest.On)
        {
            try { await Task.WhenAny(AdbClient.StopServerIfUnusedAsync(), Task.Delay(6000)); } catch { }
        }
        closingDone = true;
        Close();
    }
}
