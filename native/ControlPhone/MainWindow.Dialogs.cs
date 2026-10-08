using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ControlPhone.Adb;
using ControlPhone.Core;
using Microsoft.Win32;

namespace ControlPhone;

public partial class MainWindow
{
    static string FmtSize(long n) => n < 1024 ? $"{n} B" : n < 1048576 ? $"{n / 1024.0:0.#} KB" : n < 1073741824 ? $"{n / 1048576.0:0.#} MB" : $"{n / 1073741824.0:0.##} GB";

    static Border ListBox(out StackPanel items, double height = 380)
    {
        items = new StackPanel();
        var b = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(4), Height = height };
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        b.SetResourceReference(Border.BackgroundProperty, "Bg");
        b.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = items };
        return b;
    }

    static Border Row(FrameworkElement content)
    {
        var r = new Border { Padding = new Thickness(8, 5, 6, 5), CornerRadius = new CornerRadius(6), Child = content, Background = Brushes.Transparent };
        r.MouseEnter += (_, _) => r.SetResourceReference(Border.BackgroundProperty, "Hover");
        r.MouseLeave += (_, _) => r.Background = Brushes.Transparent;
        return r;
    }

    static Button SmallBtn(string text, string? icon, Action a, string style = "Btn")
    {
        var b = Ui.Btn(text, icon, style, (_, _) => a());
        b.MinHeight = 24; b.Padding = new Thickness(7, 1, 7, 1); b.Margin = new Thickness(4, 0, 0, 0); b.FontSize = 11.5;
        return b;
    }

    // ---------------- văn bản / URL / shell / độ sáng ----------------
    void DlgText()
    {
        var v = SelOnline();
        if (v.Count == 0) return;
        var d = new Dlg(T("Gửi văn bản tới {0} máy", v.Count));
        var tb = d.AddInput("Nội dung (hỗ trợ tiếng Việt, emoji). Hãy chạm vào ô nhập trên điện thoại trước.", "", "", multiline: true);
        var enter = d.AddCheck("Nhấn Enter sau khi gửi");
        d.AddNote("Văn bản được dán qua clipboard nên gõ được mọi ký tự có dấu.");
        d.AddAction("Đóng");
        d.AddAction("Gửi", "Primary", () =>
        {
            if (tb.Text == "") return false;
            var t = SelOnline();
            PasteToPhones(t, tb.Text, enter.IsChecked == true);
            return false;
        }, "type");
        d.Loaded += (_, _) => tb.Focus();
        d.ShowDialog();
    }

    void DlgUrl()
    {
        var ids = SelIds();
        if (ids.Count == 0) return;
        var d = new Dlg(T("Mở URL trên {0} máy", ids.Count));
        var tb = d.AddInput("Đường dẫn", "", "https://...");
        d.AddAction("Huỷ");
        d.AddAction("Mở", "Primary", () =>
        {
            var u = tb.Text.Trim();
            if (u == "") return false;
            if (!System.Text.RegularExpressions.Regex.IsMatch(u, @"^[a-z][\w+.-]*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) u = "https://" + u;
            Batch("url", SelIds(), new() { ["url"] = u }, "Mở URL");
            return true;
        });
        d.Loaded += (_, _) => tb.Focus();
        d.ShowDialog();
    }

    void DlgShell(List<string>? targetIds)
    {
        var ids = targetIds ?? SelIds();
        if (ids.Count == 0) return;
        bool anyRoot = ids.Any(id => manager.Get(id)?.Root == true);
        var hist = Store.Ui.ShellHist;
        var d = new Dlg(T("ADB shell trên {0} máy", ids.Count));
        var cb = new TextBox { Tag = T("vd: getprop ro.build.version.release") };
        var histBtn = new Button { Content = Icons.Get("more"), ToolTip = T("Lệnh đã dùng"), Margin = new Thickness(6, 0, 0, 0), IsEnabled = hist.Count > 0 };
        histBtn.SetResourceReference(StyleProperty, "IconBtn");
        histBtn.Click += (_, _) => Ui.ShowMenu(hist.Select(h => (MenuSpec?)new MenuSpec(h.Length > 90 ? h[..90] + "…" : h, "terminal", () => cb.Text = h)), histBtn);
        var cmdRow = new DockPanel(); DockPanel.SetDock(histBtn, Dock.Right); cmdRow.Children.Add(histBtn); cmdRow.Children.Add(cb);
        d.Body.Children.Add(d.Field("Lệnh", cmdRow));
        var root = d.AddCheck(T("Chạy với quyền root (su -c)") + (anyRoot ? "" : " " + T("— không có máy root")), false, anyRoot);
        d.AddNote("Kết quả của từng máy sẽ hiện ra khi chạy xong.");
        d.AddAction("Huỷ");
        d.AddAction("Chạy", "Primary", () =>
        {
            var cmd = cb.Text.Trim();
            if (cmd == "") return false;
            Store.Ui.ShellHist = new[] { cmd }.Concat(hist.Where(x => x != cmd)).Take(30).ToList(); Store.SaveUi();
            Batch("shell", ids, new() { ["cmd"] = cmd, ["root"] = root.IsChecked == true }, "Shell: " + cmd[..Math.Min(40, cmd.Length)], true);
            return true;
        }, "terminal");
        d.Loaded += (_, _) => cb.Focus();
        d.ShowDialog();
    }

    void DlgBrightness()
    {
        var ids = SelIds();
        if (ids.Count == 0) return;
        var d = new Dlg(T("Độ sáng ({0} máy)", ids.Count));
        var val = new TextBlock { Text = "40", FontWeight = FontWeights.Bold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = 30 };
        var sl = new Slider { Minimum = 0, Maximum = 255, Value = 40, Width = 360 };
        sl.ValueChanged += (_, e) => val.Text = ((int)e.NewValue).ToString();
        var sp = new StackPanel { Orientation = Orientation.Horizontal }; sp.Children.Add(sl); sp.Children.Add(val);
        d.Body.Children.Add(d.Field("Mức sáng (0–255). Mức thấp giúp tiết kiệm pin khi treo máy.", sp));
        d.AddAction("Huỷ");
        d.AddAction("Áp dụng", "Primary", () => { Batch("brightness", SelIds(), new() { ["value"] = (int)sl.Value }, "Độ sáng"); return true; });
        d.ShowDialog();
    }

    // ---------------- APK / tệp ----------------
    void PickApks(List<string>? ids)
    {
        var ofd = new OpenFileDialog { Filter = "APK (*.apk;*.apks;*.xapk)|*.apk;*.apks;*.xapk|*.*|*.*", Multiselect = true, Title = T("Cài APK") };
        if (ofd.ShowDialog(this) == true) InstallApks(ofd.FileNames.ToList(), ids);
    }

    void InstallApks(List<string> files, List<string>? targetIds)
    {
        var ids = targetIds ?? SelIds();
        if (ids.Count == 0 || files.Count == 0) return;
        var n = ids.Count > 1 ? T(" ({0} máy)", ids.Count) : "";
        foreach (var f in files) Batch("install", ids, new() { ["file"] = f }, T("Cài {0}", Path.GetFileName(f)) + n);
    }

    void DlgPush(List<string>? preFiles, List<string>? targetIds)
    {
        var ids = targetIds ?? SelIds();
        if (ids.Count == 0) return;
        var files = preFiles ?? [];
        var d = new Dlg(T("Gửi tệp tới {0} máy", ids.Count));
        var fl = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        fl.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        void Show() => fl.Text = files.Count > 0 ? string.Join(", ", files.Select(f => $"{Path.GetFileName(f)} ({FmtSize(new FileInfo(f).Length)})")) : T("chưa chọn");
        Show();
        var pick = Ui.Btn("Chọn tệp…", "file", "Btn", (_, _) =>
        {
            var ofd = new OpenFileDialog { Multiselect = true };
            if (ofd.ShowDialog(d) == true) { files = ofd.FileNames.ToList(); Show(); }
        });
        var row = new DockPanel(); row.Children.Add(pick); row.Children.Add(fl);
        d.Body.Children.Add(d.Field("Tệp", row));
        var dir = d.AddInput("Thư mục trên điện thoại", Store.Ui.PushDir);
        d.AddNote("Mẹo: kéo thả tệp bất kỳ vào cửa sổ để gửi nhanh (tệp .apk sẽ được cài đặt).");
        d.AddAction("Huỷ");
        d.AddAction("Gửi", "Primary", () =>
        {
            if (files.Count == 0) { Ui.Toast("Chưa chọn tệp", "err"); return false; }
            var dd = dir.Text.Trim(); if (dd == "") dd = "/sdcard/Download/";
            Store.Ui.PushDir = dd; Store.SaveUi();
            foreach (var f in files) Batch("push", ids, new() { ["file"] = f, ["dir"] = dd, ["name"] = Path.GetFileName(f) }, T("Gửi {0}", Path.GetFileName(f)));
            return true;
        }, "upload");
        d.ShowDialog();
    }

    // ---------------- ứng dụng ----------------
    async void DlgApps(List<string> ids, string? sourceId)
    {
        var src = manager.Get(sourceId ?? ids[0]);
        if (src?.ActiveAddr == null) return;
        List<string> TargetIds() => sourceId != null ? MenuTargets(src) : ids;
        string TargetsLabel() => sourceId != null ? (TargetIds().Count > 1 ? T("{0} máy (đồng bộ)", TargetIds().Count) : T("1 máy")) : T("{0} máy", ids.Count);
        var d = new Dlg("Ứng dụng", 760);
        var filterBox = new TextBox { Tag = T("Lọc hoặc nhập tên gói (vd: com.android.chrome)") };
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var openBtn = Ui.Btn("Mở gói đã nhập", "play", "Primary"); openBtn.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(openBtn, Dock.Right); row.Children.Add(openBtn); row.Children.Add(filterBox);
        d.Body.Children.Add(row);
        d.AddNote(T("Danh sách lấy từ máy #{0} {1}", src.Num, src.DisplayName) + "\n" + T("Áp dụng cho:") + " " + TargetsLabel());
        d.Body.Children.Add(ListBox(out var list));
        list.Children.Add(new TextBlock { Text = T("Đang tải…"), Margin = new Thickness(8) });
        List<string> apps = [];
        void Run(string op, string pkg)
        {
            var t = TargetIds();
            if (op == "uninstall" && !Ui.Confirm("Gỡ cài đặt", T("Gỡ \"{0}\" trên {1} máy?", pkg, t.Count), "Gỡ", true)) return;
            if (op == "clearapp" && !Ui.Confirm("Xoá dữ liệu", T("Xoá toàn bộ dữ liệu của \"{0}\" trên {1} máy?", pkg, t.Count), "Xoá", true)) return;
            var titles = new Dictionary<string, string> { ["launch"] = "Mở", ["stopapp"] = "Dừng", ["clearapp"] = "Xoá dữ liệu", ["uninstall"] = "Gỡ" };
            Batch(op, t, new() { ["pkg"] = pkg }, T(titles[op]) + " " + pkg, op is "uninstall" or "clearapp");
            if (op == "uninstall") { apps.Remove(pkg); Render(); }
        }
        void Render()
        {
            var q = filterBox.Text.Trim().ToLowerInvariant();
            list.Children.Clear();
            var shown = apps.Where(a => q == "" || a.ToLowerInvariant().Contains(q)).ToList();
            if (shown.Count == 0) { list.Children.Add(new TextBlock { Text = T("Không có ứng dụng (người dùng cài) nào."), Margin = new Thickness(8) }); return; }
            foreach (var a in shown)
            {
                var dp = new DockPanel();
                var acts = new StackPanel { Orientation = Orientation.Horizontal };
                acts.Children.Add(SmallBtn("Mở", "play", () => Run("launch", a)));
                acts.Children.Add(SmallBtn("Dừng", "stop", () => Run("stopapp", a)));
                acts.Children.Add(SmallBtn("Xoá dữ liệu", "eraser", () => Run("clearapp", a)));
                acts.Children.Add(SmallBtn("Gỡ", "trash", () => Run("uninstall", a), "Danger"));
                DockPanel.SetDock(acts, Dock.Right); dp.Children.Add(acts);
                var ic = Ui.Icon("package", 13, "Muted"); ic.Margin = new Thickness(0, 0, 8, 0); dp.Children.Add(ic);
                dp.Children.Add(new TextBlock { Text = a, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                list.Children.Add(Row(dp));
            }
        }
        filterBox.TextChanged += (_, _) => Render();
        openBtn.Click += (_, _) => { var p = filterBox.Text.Trim(); if (p != "") Run("launch", p); };
        d.AddAction("Đóng", "Primary");
        d.Show();
        try { apps = await Actions.ListAppsAsync(src.ActiveAddr); Render(); }
        catch (Exception e) { list.Children.Clear(); list.Children.Add(new TextBlock { Text = e.Message, Margin = new Thickness(8) }); }
    }

    // ---------------- tệp ----------------
    async void DlgFiles(PhoneDevice dev)
    {
        if (dev.ActiveAddr == null) return;
        var cur = Store.Ui.FilesPath;
        var d = new Dlg(T("Tệp — #{0} {1}", dev.Num, dev.DisplayName), 820);
        var path = new TextBox();
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var up = new Button { Content = Icons.Get("up"), ToolTip = T("Lên thư mục cha"), Margin = new Thickness(0, 0, 6, 0) }; up.SetResourceReference(StyleProperty, "IconBtn");
        var go = new Button { Content = Icons.Get("refresh"), ToolTip = T("Làm mới"), Margin = new Thickness(6, 0, 0, 0) }; go.SetResourceReference(StyleProperty, "IconBtn");
        var upl = Ui.Btn("Tải lên đây", "upload", "Primary"); upl.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(up, Dock.Left); DockPanel.SetDock(upl, Dock.Right); DockPanel.SetDock(go, Dock.Right);
        bar.Children.Add(up); bar.Children.Add(upl); bar.Children.Add(go); bar.Children.Add(path);
        d.Body.Children.Add(bar);
        var all = d.AddCheck(T("Khi tải lên: gửi tới tất cả máy đang chọn ({0}) thay vì chỉ máy này", selected.Count));
        d.Body.Children.Add(ListBox(out var list, 420));
        async Task Load(string p)
        {
            list.Children.Clear();
            list.Children.Add(new TextBlock { Text = T("Đang tải…"), Margin = new Thickness(8) });
            try
            {
                var (np, items) = await Actions.ListFilesAsync(dev.ActiveAddr!, p);
                cur = np; Store.Ui.FilesPath = cur; Store.SaveUi();
                path.Text = cur;
                list.Children.Clear();
                if (items.Count == 0) list.Children.Add(new TextBlock { Text = T("(Thư mục trống)"), Margin = new Thickness(8) });
                foreach (var it in items)
                {
                    var full = cur + it.Name;
                    var dp = new DockPanel();
                    var acts = new StackPanel { Orientation = Orientation.Horizontal };
                    if (!it.Dir) acts.Children.Add(SmallBtn("", "download", () => _ = Pull(full, it.Name)));
                    acts.Children.Add(SmallBtn("", "trash", async () =>
                    {
                        if (!Ui.Confirm("Xoá", T("Xoá \"{0}\" trên máy này?", full), "Xoá", true)) return;
                        try { await Actions.DeletePathAsync(dev.ActiveAddr!, full); await Load(cur); } catch (Exception e) { Ui.Toast(e.Message, "err"); }
                    }, "Danger"));
                    DockPanel.SetDock(acts, Dock.Right); dp.Children.Add(acts);
                    var meta = new TextBlock { Text = (it.Dir ? "" : FmtSize(it.Size)) + "   " + (it.MTime?.ToString("g") ?? ""), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), FontSize = 11.5 };
                    meta.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                    DockPanel.SetDock(meta, Dock.Right); dp.Children.Add(meta);
                    var ic = Ui.Icon(it.Dir ? "folder" : "file", 13, it.Dir ? "Accent" : "Muted"); ic.Margin = new Thickness(0, 0, 8, 0); dp.Children.Add(ic);
                    var nm = new TextBlock { Text = it.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Cursor = it.Dir ? Cursors.Hand : null };
                    dp.Children.Add(nm);
                    var r = Row(dp);
                    if (it.Dir) r.MouseLeftButtonUp += async (_, e) => { if (e.OriginalSource is not FrameworkElement fe || fe.TemplatedParent is not Button) await Load(full + "/"); };
                    list.Children.Add(r);
                }
            }
            catch (Exception e) { list.Children.Clear(); list.Children.Add(new TextBlock { Text = e.Message, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap }); }
        }
        async Task Pull(string full, string name)
        {
            var sfd = new SaveFileDialog { FileName = name };
            if (sfd.ShowDialog(d) != true) return;
            try { await AdbClient.ExecToFileAsync(dev.ActiveAddr!, "cat " + AdbClient.ShellQuote(full), sfd.FileName); Ui.Toast(T("Đã lưu: {0}", sfd.FileName), "ok"); }
            catch (Exception e) { Ui.Toast(e.Message, "err"); }
        }
        go.Click += async (_, _) => await Load(path.Text.Trim());
        path.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Load(path.Text.Trim()); } };
        up.Click += async (_, _) => { var p = cur.TrimEnd('/'); await Load(p.Contains('/') ? p[..(p.LastIndexOf('/') + 1)] : "/"); };
        upl.Click += async (_, _) =>
        {
            var ofd = new OpenFileDialog { Multiselect = true };
            if (ofd.ShowDialog(d) != true) return;
            var ids = all.IsChecked == true ? SelIds() : [dev.Id];
            if (ids.Count == 0) return;
            foreach (var f in ofd.FileNames) Batch("push", ids, new() { ["file"] = f, ["dir"] = cur, ["name"] = Path.GetFileName(f) }, T("Gửi {0}", Path.GetFileName(f)));
            await Task.Delay(1500);
            await Load(cur);
        };
        d.AddAction("Đóng", "Primary");
        d.Show();
        await Load(cur);
    }

    // ---------------- thông tin ----------------
    void DlgInfo(PhoneDevice dev)
    {
        var d = new Dlg(T("Thông tin #{0}", dev.Num), 560);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Fill()
        {
            grid.Children.Clear(); grid.RowDefinitions.Clear();
            (string K, string V)[] rows =
            [
                ("Số thứ tự", dev.Num.ToString()), ("Tên", dev.Label != "" ? dev.Label : "—"), ("Model", $"{dev.Manufacturer} {dev.Model}"),
                ("Android", $"{dev.Android} (SDK {dev.Sdk})"), ("Serial phần cứng", dev.Id),
                ("Kết nối", string.Join("\n", dev.Transports.Select(t => t.Value.ToUpper() + ": " + t.Key))),
                ("Đang dùng", dev.Transport != null ? dev.Transport.ToUpper() + " · " + dev.Serial : "—"), ("IP WiFi", dev.Ip != "" ? dev.Ip : "—"),
                ("Pin", dev.Battery is int b ? $"{b}%{(dev.Charging ? " " + T("(đang sạc)") : "")}{(dev.Temp is double t ? $" · {t}°C" : "")}" : "—"),
                ("Màn hình", dev.ScreenOn == null ? "—" : T(dev.ScreenOn == true ? "Đang bật" : "Đang tắt")), ("Root", T(dev.Root ? "Có" : "Không")),
                ("Luồng lưới", dev.VideoW > 0 ? $"{dev.VideoW}×{dev.VideoH}" : "—"), ("Nhóm", dev.Groups.Count > 0 ? string.Join(", ", dev.Groups) : "—"),
                ("Trạng thái", dev.Status + (dev.Error != "" ? " — " + T(dev.Error) : "")),
            ];
            int i = 0;
            foreach (var (k, v) in rows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var kt = new TextBlock { Text = T(k), Margin = new Thickness(0, 3, 10, 3) }; kt.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                var vt = new TextBox { Text = v, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap };
                Grid.SetRow(kt, i); Grid.SetRow(vt, i); Grid.SetColumn(vt, 1);
                grid.Children.Add(kt); grid.Children.Add(vt);
                i++;
            }
        }
        Fill();
        d.Body.Children.Add(grid);
        d.AddAction("Cập nhật", "Btn", () => { manager.RefreshInfo(dev); Ui.Toast("Đang cập nhật…"); _ = Task.Delay(2500).ContinueWith(_ => Dispatcher.Invoke(Fill)); return false; }, "refresh");
        d.AddAction("Đóng", "Primary");
        d.ShowDialog();
    }

    // ---------------- nhóm ----------------
    void DlgGroup(List<string> ids, bool remove)
    {
        if (ids.Count == 0) return;
        var d = new Dlg(remove ? T("Bỏ {0} máy khỏi nhóm", ids.Count) : T("Thêm {0} máy vào nhóm", ids.Count));
        var name = d.AddInput(remove ? "Chọn nhóm" : "Chọn nhóm có sẵn hoặc nhập tên nhóm mới", "", "vd: Nhóm A");
        var wp = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var g in Store.State.Groups) { var gg = g; var b = SmallBtn(gg, "tag", () => name.Text = gg); b.Margin = new Thickness(0, 0, 6, 6); wp.Children.Add(b); }
        d.Body.Children.Add(wp);
        d.AddAction("Huỷ");
        d.AddAction(remove ? "Bỏ khỏi nhóm" : "Thêm", "Primary", () =>
        {
            var n = name.Text.Trim();
            if (n == "") return false;
            GroupAdd(ids, n[..Math.Min(30, n.Length)], remove);
            Ui.Toast(remove ? T("Đã bỏ khỏi nhóm") : T("Đã thêm vào nhóm \"{0}\"", n), "ok");
            return true;
        });
        d.Loaded += (_, _) => name.Focus();
        d.ShowDialog();
    }

    // ---------------- cài đặt ----------------
    void DlgSettings()
    {
        var s = Store.Settings;
        var d = new Dlg("Cài đặt", 720);
        TextBox Num(string label, int v, out FrameworkElement field) { var tb = new TextBox { Text = v.ToString() }; field = d.Field(label, tb); return tb; }
        Grid Row3(params FrameworkElement[] fields)
        {
            var g = new Grid();
            for (int i = 0; i < fields.Length; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(fields[i], i);
                if (fields[i] is FrameworkElement fe && i < fields.Length - 1) fe.Margin = new Thickness(0, 0, 10, 0);
                g.Children.Add(fields[i]);
            }
            return g;
        }
        var pre = new WrapPanel();
        d.Body.Children.Add(d.Field("Chất lượng lưới (xem nhiều máy) — mức nhẹ giúp chạy được 100 máy", pre));
        var tMax = Num("Độ phân giải tối đa (px cạnh dài)", s.ThumbMaxSize, out var f1);
        var tBr = Num("Bitrate (kbps)", s.ThumbBitRate / 1000, out var f2);
        var tFps = Num("FPS tối đa", s.ThumbFps, out var f3);
        d.Body.Children.Add(Row3(f1, f2, f3));
        foreach (var (label, mx, br, fps) in new[] { ("Siêu nhẹ (360p/10fps)", 360, 300, 10), ("Cân bằng (480p/15fps)", 480, 600, 15), ("Rõ nét (720p/24fps)", 720, 1500, 24) })
        {
            var b = SmallBtn(label, null, () => { tMax.Text = mx.ToString(); tBr.Text = br.ToString(); tFps.Text = fps.ToString(); });
            b.Margin = new Thickness(0, 0, 6, 0); pre.Children.Add(b);
        }
        d.AddText("Màn hình lớn (1 máy)", "Muted", 12);
        var hMax = Num("Độ phân giải tối đa", s.HqMaxSize, out var g1);
        var hBr = Num("Bitrate (kbps)", s.HqBitRate / 1000, out var g2);
        var hFps = Num("FPS tối đa", s.HqFps, out var g3);
        d.Body.Children.Add(Row3(g1, g2, g3));
        var pref = new ComboBox();
        pref.Items.Add(new ComboBoxItem { Content = T("USB (ổn định hơn)"), Tag = "usb", IsSelected = s.PreferTransport != "wifi" });
        pref.Items.Add(new ComboBoxItem { Content = "WiFi", Tag = "wifi", IsSelected = s.PreferTransport == "wifi" });
        var mps = Num("Số máy khởi động luồng cùng lúc", s.MaxParallelStart, out var h2);
        var mpj = Num("Số máy chạy thao tác cùng lúc (cài APK…)", s.MaxParallelJobs, out var h3);
        d.Body.Children.Add(Row3(d.Field("Ưu tiên kết nối khi máy có cả USB và WiFi", pref), h2, h3));
        var shotDir = new TextBox { Text = s.ScreenshotDir };
        var browse = Ui.Btn("", "folder", "Btn", (_, _) =>
        {
            var dlg = new OpenFolderDialog { InitialDirectory = Directory.Exists(shotDir.Text) ? shotDir.Text : "" };
            if (dlg.ShowDialog(d) == true) shotDir.Text = dlg.FolderName;
        });
        browse.Margin = new Thickness(6, 0, 0, 0);
        var sdRow = new DockPanel(); DockPanel.SetDock(browse, Dock.Right); sdRow.Children.Add(browse); sdRow.Children.Add(shotDir);
        d.Body.Children.Add(d.Field("Thư mục lưu ảnh chụp", sdRow));
        var screenOff = d.AddCheck("Tự tắt màn hình điện thoại khi kết nối (vẫn xem & điều khiển được, tiết kiệm pin — thoát app màn hình tự bật lại)", s.ScreenOffOnConnect);
        var powerOn = d.AddCheck("Đánh thức điện thoại khi kết nối", s.PowerOnConnect);
        var autoWifi = d.AddCheck("Tự kết nối lại các máy WiFi đã từng kết nối", s.AutoReconnectWifi);
        var stopAdb = d.AddCheck("Tắt ADB khi thoát ControlPhone (bỏ chọn nếu công cụ khác cũng dùng ADB, ví dụ script tự động)", s.StopAdbOnExit);
        var autoKey = d.AddCheck("ROOT: tự cấy khoá uỷ quyền khi máy kết nối (khỏi hỏi \"Cho phép gỡ lỗi USB\")", s.AutoPersistKey);
        d.AddNote(string.Join("\n", new[]
        {
            "Thay đổi chất lượng lưới sẽ kết nối lại luồng của tất cả máy.",
            "Chuột trên màn hình nhỏ = ngón tay (chạm, vuốt, nhấn giữ).",
            "Chuột phải: menu (Phóng to, Cài APK, Nhập/Xuất tệp, Lệnh ADB, Cài đặt).",
            "Nút bên chuột: Quay lại · Chuột giữa: Home.",
            "Shift + lăn chuột: cuộn trong điện thoại · Alt + kéo: thu phóng 2 ngón.",
            "Gõ phím khi trỏ chuột trên ô: chữ được gửi vào máy đó.",
            "Click: chọn 1 máy · Ctrl + click: thêm/bớt máy vào nhóm · Kéo khung: chọn nhiều máy.",
            "Khi chọn từ 2 máy, thao tác trên một máy trong nhóm sẽ tự đồng bộ cho cả nhóm (F3: tắt/bật tạm).",
            "Ctrl + A: chọn tất cả · Esc: bỏ chọn · Ctrl + V: dán vào các máy đang chọn.",
        }.Select(x => "• " + T(x))));
        d.AddAction("Huỷ");
        d.AddAction("Lưu", "Primary", () =>
        {
            int I(TextBox tb, int def, int min, int max) => int.TryParse(tb.Text, out var v) ? Math.Clamp(v, min, max) : def;
            var before = (s.ThumbMaxSize, s.ThumbBitRate, s.ThumbFps, s.PowerOnConnect, s.ScreenOffOnConnect);
            var beforeHq = (s.HqMaxSize, s.HqBitRate, s.HqFps);
            var prefBefore = s.PreferTransport;
            s.ThumbMaxSize = I(tMax, 480, 160, 1920); s.ThumbBitRate = I(tBr, 600, 100, 20000) * 1000; s.ThumbFps = I(tFps, 15, 1, 60);
            s.HqMaxSize = I(hMax, 1280, 320, 2560); s.HqBitRate = I(hBr, 4000, 500, 40000) * 1000; s.HqFps = I(hFps, 30, 5, 60);
            s.PreferTransport = (string)((ComboBoxItem)pref.SelectedItem).Tag;
            s.MaxParallelStart = I(mps, 6, 1, 30); s.MaxParallelJobs = I(mpj, 10, 1, 50);
            s.ScreenshotDir = shotDir.Text.Trim() != "" ? shotDir.Text.Trim() : s.ScreenshotDir;
            s.ScreenOffOnConnect = screenOff.IsChecked == true; s.PowerOnConnect = powerOn.IsChecked == true;
            s.AutoReconnectWifi = autoWifi.IsChecked == true; s.StopAdbOnExit = stopAdb.IsChecked == true; s.AutoPersistKey = autoKey.IsChecked == true;
            Store.Save();
            manager.ResetStartLimiter();
            if (before != (s.ThumbMaxSize, s.ThumbBitRate, s.ThumbFps, s.PowerOnConnect, s.ScreenOffOnConnect)) manager.RestartAll();
            else if (beforeHq != (s.HqMaxSize, s.HqBitRate, s.HqFps) && viewerId != null && manager.Get(viewerId) is { } vd) { manager.ReleaseHq(vd); manager.AcquireHq(vd); }
            if (prefBefore != s.PreferTransport) manager.ReapplyTransportPreference();
            Ui.Toast("Đã lưu cài đặt", "ok");
            return true;
        });
        d.ShowDialog();
    }

    // ---------------- kết nối ----------------
    void DlgScan()
    {
        var subs = Actions.LocalSubnets();
        var d = new Dlg("Quét mạng LAN tìm điện thoại", 620);
        var sn = new TextBox { Text = subs.FirstOrDefault().Subnet ?? "192.168.1" };
        var sp = new TextBox { Text = "5555" };
        var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        var f1 = d.Field("Dải mạng (3 số đầu)", sn); f1.Margin = new Thickness(0, 0, 10, 0);
        var f2 = d.Field("Cổng ADB", sp); Grid.SetColumn(f2, 1);
        g.Children.Add(f1); g.Children.Add(f2);
        d.Body.Children.Add(g);
        d.AddNote(T("Máy tính:") + " " + (subs.Count > 0 ? string.Join(" · ", subs.Select(x => $"{x.Name} {x.Address}")) : T("không tìm thấy mạng")) + "\n" +
                  T("Điện thoại cần bật ADB qua TCP (đã từng \"USB → WiFi\" hoặc root \"ADB WiFi cố định\")."));
        var result = new StackPanel();
        d.Body.Children.Add(result);
        d.AddAction("Đóng");
        d.AddActionAsync("Quét", "Primary", async () =>
        {
            result.Children.Clear();
            result.Children.Add(new TextBlock { Text = T("Đang quét 254 địa chỉ…"), Margin = new Thickness(0, 4, 0, 4) });
            var r = await actions.ScanAsync(sn.Text.Trim(), int.TryParse(sp.Text, out var p) ? p : 5555);
            result.Children.Clear();
            result.Children.Add(new TextBlock { Text = T("Tìm thấy {0} thiết bị mở cổng ADB.", r.Found), Margin = new Thickness(0, 4, 0, 6) });
            foreach (var (addr, ok, msg) in r.Results) result.Children.Add(ResultRow(ok, addr, msg));
            return false;
        }, "scan");
        d.ShowDialog();
    }

    void DlgPair()
    {
        var d = new Dlg("Ghép nối gỡ lỗi không dây (Android 11+)", 600);
        d.AddNote("Trên điện thoại: Tuỳ chọn nhà phát triển → Gỡ lỗi không dây → Ghép nối thiết bị bằng mã ghép nối. Nhập IP:cổng và mã 6 số hiển thị.");
        var pa = d.AddInput("IP:cổng ghép nối", "", "192.168.1.20:37099");
        var pc = d.AddInput("Mã ghép nối", "", "123456");
        var pk = d.AddInput("Sau khi ghép: IP:cổng kết nối (ở màn hình Gỡ lỗi không dây)", "", "192.168.1.20:41234");
        d.AddAction("Đóng");
        d.AddActionAsync("Ghép nối & kết nối", "Primary", async () =>
        {
            var r = await actions.PairAsync(pa.Text, pc.Text);
            Ui.Toast(r.Msg, r.Ok ? "ok" : "err", 6000);
            if (r.Ok && pk.Text.Trim() != "") { var c = await actions.ConnectAsync(pk.Text); Ui.Toast(c.Msg, c.Ok ? "ok" : "err", 6000); }
            return r.Ok;
        });
        d.ShowDialog();
    }

    void DlgOtg()
    {
        var first = selected.Select(manager.Get).FirstOrDefault(x => x != null);
        var usb = first?.Transports.FirstOrDefault(t => t.Value == "usb").Key ?? "";
        var d = new Dlg("Chế độ OTG (giả lập bàn phím/chuột qua USB)", 600);
        d.AddNote(T("OTG (scrcpy --otg) điều khiển điện thoại qua USB như bàn phím và chuột vật lý, không cần bật Gỡ lỗi USB.") + "\n" +
                  T("Không có hình ảnh: hãy nhìn trực tiếp màn hình điện thoại. Hữu ích cho máy chưa bật ADB hoặc bị khoá ADB.") + "\n\n" +
                  T("Lưu ý Windows: thiết bị không được đồng thời bị ADB chiếm. Nếu báo lỗi truy cập USB, hãy rút các kết nối ADB của máy đó hoặc bấm \"Khởi động lại ADB\" sau khi tắt Gỡ lỗi USB."));
        var os = d.AddInput("Serial USB (để trống nếu chỉ cắm 1 máy)", usb);
        d.AddAction("Đóng");
        d.AddAction("Mở OTG", "Primary", () =>
        {
            var r = actions.LaunchScrcpy(os.Text.Trim() != "" ? os.Text.Trim() : null, ["--otg"]);
            Ui.Toast(r.Msg, r.Ok ? "ok" : "err");
            return false;
        }, "usb");
        d.ShowDialog();
    }

    void DlgDiag()
    {
        var d = new Dlg("Chẩn đoán kết nối", 820);
        d.AddNote("Nếu thiếu máy: chụp bảng này hoặc bấm Sao chép báo cáo rồi gửi cho tác giả.");
        var body = new StackPanel();
        d.Body.Children.Add(body);
        string report = "";
        async void Load()
        {
            var r = manager.Diagnostics();
            var ver = (await AdbClient.RunAsync(["version"], 10000)).Out.Split('\n').Take(2);
            body.Children.Clear();
            var kv = new TextBlock { Margin = new Thickness(0, 0, 0, 8) };
            kv.Text = $"{T("Kết nối ADB")}: {r.AdbConnections}    {T("Máy sẵn sàng")}: {r.DeviceConnections}    {T("Số ô trên màn hình")}: {r.Tiles}    {T("Serial bị trùng")}: {r.DupSerials}";
            body.Children.Add(kv);
            var note = new TextBlock { Text = AdbClient.AdbPath + "\n" + string.Join(" ", ver).Trim(), FontSize = 11.5, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
            note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            body.Children.Add(note);
            body.Children.Add(ListBox(out var list, 360));
            foreach (var x in r.Rows)
            {
                var dp = new DockPanel();
                var ic = Ui.Icon(x.Ok ? "check" : "info", 13, x.Ok ? "Ok" : "Warn"); ic.Margin = new Thickness(0, 0, 8, 0); dp.Children.Add(ic);
                var a = new TextBlock { Text = x.Addr, FontFamily = new FontFamily("Consolas"), Width = 230, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; dp.Children.Add(a);
                var st = new TextBlock { Text = x.State + (x.Tid != "" ? " · t" + x.Tid : ""), Width = 130, VerticalAlignment = VerticalAlignment.Center }; st.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); dp.Children.Add(st);
                var tl = new TextBlock { Text = x.Tile, Width = 46, VerticalAlignment = VerticalAlignment.Center }; dp.Children.Add(tl);
                dp.Children.Add(new TextBlock { Text = T(x.Note), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = T(x.Note) });
                list.Children.Add(Row(dp));
            }
            report = string.Join("\n", new[] { $"ControlPhone diag — adb={r.AdbConnections} device={r.DeviceConnections} tiles={r.Tiles} dupSerials={r.DupSerials}", AdbClient.AdbPath, string.Join(" ", ver) }
                .Concat(r.Rows.Select(x => $"{x.Addr}\t{x.State}\tt{x.Tid}\t{x.Tile}\t{x.Note}")));
        }
        d.AddAction("Làm mới", "Btn", () => { Load(); return false; }, "refresh");
        d.AddAction("Sao chép báo cáo", "Btn", () => { try { Clipboard.SetText(report); Ui.Toast("Đã sao chép báo cáo", "ok"); } catch { } return false; }, "copy");
        d.AddAction("Đóng", "Primary");
        Load();
        d.ShowDialog();
    }

    // ---------------- ủng hộ ----------------
    const string DonateGithub = "https://github.com/mahiepit/ControlPhone";
    const string DonatePaypal = "https://paypal.me/thaogia";
    const string DonateEvm = "0xd09c2E60cbC8526976C436e316630FA64296E824";

    void DlgDonate()
    {
        var d = new Dlg("Ủng hộ tác giả", 640);
        d.AddText("ControlPhone miễn phí và mã nguồn mở.");
        d.AddText("Nếu phần mềm giúp ích cho bạn, hãy ủng hộ để tác giả có thêm động lực duy trì và phát triển. Cảm ơn bạn!");
        void Card(string head, string value, string? url, string? note)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = head, FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(0, 0, 0, 6) });
            sp.Children.Add(new TextBox { Text = value, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 0, 0, 8) });
            var acts = new StackPanel { Orientation = Orientation.Horizontal };
            if (url != null) { var b = Ui.Btn("Mở PayPal", "heart", "Primary", (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })); b.Margin = new Thickness(0, 0, 6, 0); acts.Children.Add(b); }
            acts.Children.Add(Ui.Btn(url != null ? "Sao chép" : "Sao chép địa chỉ", "copy", url != null ? "Btn" : "Primary", (_, _) => { try { Clipboard.SetText(value); Ui.Toast("Đã sao chép", "ok"); } catch { Ui.Toast("Không sao chép được — hãy bôi đen và sao chép thủ công", "err"); } }));
            sp.Children.Add(acts);
            if (note != null) { var n = new TextBlock { Text = T(note), TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0) }; n.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); sp.Children.Add(n); }
            var b2 = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10), Child = sp, BorderThickness = new Thickness(1) };
            b2.SetResourceReference(Border.BackgroundProperty, "Bg3"); b2.SetResourceReference(Border.BorderBrushProperty, "Line");
            d.Body.Children.Add(b2);
        }
        Card("PayPal", DonatePaypal, DonatePaypal, null);
        Card("BNB / ETH", DonateEvm, null, "Mạng: BNB Smart Chain (BEP-20) hoặc Ethereum (ERC-20). Hãy kiểm tra đúng mạng trước khi gửi.");
        var gh = new TextBlock { Text = DonateGithub.Replace("https://", ""), Cursor = Cursors.Hand, TextDecorations = TextDecorations.Underline };
        gh.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        gh.MouseLeftButtonUp += (_, _) => Process.Start(new ProcessStartInfo(DonateGithub) { UseShellExecute = true });
        d.Body.Children.Add(gh);
        d.AddAction("Đóng", "Primary");
        d.ShowDialog();
    }

    static FrameworkElement ResultRow(bool ok, string name, string msg)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var ic = Ui.Icon(ok ? "check" : "x", 13, ok ? "Ok" : "Bad"); ic.Margin = new Thickness(0, 2, 8, 0); ic.VerticalAlignment = VerticalAlignment.Top;
        var nm = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0) };
        var m = new TextBox { Text = msg, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 11.5, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(nm, 1); Grid.SetColumn(m, 2);
        g.Children.Add(ic); g.Children.Add(nm); g.Children.Add(m);
        return g;
    }
}
