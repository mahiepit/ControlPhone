using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ControlPhone.Adb;
using ControlPhone.Core;
using ControlPhone.Scrcpy;

namespace ControlPhone;

public partial class MainWindow
{
    string? viewerId;
    WriteableBitmap? vBmp;

    void InitViewer()
    {
        VClose.Click += (_, _) => CloseViewer();
        VPrev.Click += (_, _) => StepViewer(-1);
        VNext.Click += (_, _) => StepViewer(1);
        SizeChanged += (_, _) => LayoutViewer();

        (string Act, string Icon, string Tip)?[] tools =
        [
            ("back", "back", "Quay lại (chuột phải)"), ("home", "home", "Màn hình chính (chuột giữa)"), ("recents", "recents", "Đa nhiệm"), null,
            ("power", "power", "Nguồn"), ("volup", "volup", "Tăng âm lượng"), ("voldown", "voldown", "Giảm âm lượng"), ("rotate", "rotate", "Xoay màn hình"), null,
            ("notif", "bell", "Kéo thanh thông báo"), ("unlock", "unlock", "Bật màn hình + vuốt mở khoá"), ("screenoff", "screenoff", "Tắt màn hình thật (vẫn xem)"), ("screenon", "sun", "Bật màn hình thật"), null,
            ("@paste", "clipboard", "Dán clipboard máy tính (Ctrl+V)"), ("@shotclip", "shotcopy", "Chụp màn hình vào clipboard (dán bằng Ctrl+V)"), ("@shot", "camera", "Chụp và tải ảnh"),
        ];
        foreach (var t in tools)
        {
            if (t == null) { VTools.Children.Add(new Border { Height = 8 }); continue; }
            var (act, icon, tip) = t.Value;
            var b = new Button { Content = Icons.Get(icon), ToolTip = T(tip), Width = 38, Height = 34, Margin = new Thickness(0, 0, 0, 4) };
            b.SetResourceReference(StyleProperty, "IconBtn");
            b.Click += async (_, _) => await ViewerTool(act, b);
            VTools.Children.Add(b);
        }
        void Foot(string text, string icon, Action a, string? tip = null)
        {
            var b = Ui.Btn(text, icon, "Btn", (_, _) => a());
            b.Margin = new Thickness(0, 0, 6, 0); b.MinHeight = 28; b.Padding = new Thickness(9, 3, 9, 3);
            if (tip != null) b.ToolTip = T(tip);
            VFoot.Children.Add(b);
        }
        Foot("Ứng dụng", "apps", () => { if (ViewerDev is { } d) DlgApps([d.Id], d.Id); });
        Foot("Tệp", "folder", () => { if (ViewerDev is { } d) DlgFiles(d); });
        Foot("Thông tin", "info", () => { if (ViewerDev is { } d) DlgInfo(d); });
        Foot("scrcpy", "monitor", () => { if (ViewerDev is { } d) OpenScrcpy(d); }, "Mở cửa sổ scrcpy gốc");

        // chạm trên màn hình lớn
        VScreen.MouseDown += (_, e) =>
        {
            var d = ViewerDev;
            if (d == null) return;
            VScreen.Focus();
            var targets = TargetsFor(d);
            e.Handled = true;
            if (e.ChangedButton == MouseButton.Right || e.ChangedButton == MouseButton.XButton1) { PhoneControl.Act(targets, "back"); return; }
            if (e.ChangedButton == MouseButton.Middle) { PhoneControl.Act(targets, "home"); return; }
            if (e.ChangedButton != MouseButton.Left) return;
            StartTouch(targets, null, VScreen, VNorm(e.GetPosition(VScreen)), viewer: true);
            VScreen.CaptureMouse();
        };
        VScreen.MouseMove += (_, e) =>
        {
            if (touch is not { Viewer: true } t) return;
            t.Pending = VNorm(e.GetPosition(VScreen));
            FlushMoveThrottled();
        };
        VScreen.MouseUp += (_, e) =>
        {
            if (touch is not { Viewer: true }) return;
            EndTouch(VNorm(e.GetPosition(VScreen)));
            VScreen.ReleaseMouseCapture();
            e.Handled = true;
        };
        VScreen.MouseWheel += (_, e) =>
        {
            var d = ViewerDev;
            if (d == null) return;
            e.Handled = true;
            var p = VNorm(e.GetPosition(VScreen));
            foreach (var x in TargetsFor(d)) x.ControlSession?.Scroll(p.X, p.Y, 0, Math.Clamp(e.Delta / 120f, -16, 16));
        };
    }

    PhoneDevice? ViewerDev => viewerId != null ? manager.Get(viewerId) : null;

    Point VNorm(Point p)
    {
        double W = VScreen.ActualWidth, H = VScreen.ActualHeight;
        double vw = vBmp?.PixelWidth ?? 9, vh = vBmp?.PixelHeight ?? 18.5;
        double sc = Math.Min(W / vw, H / vh);
        double w = vw * sc, h = vh * sc;
        return new Point(Math.Clamp((p.X - (W - w) / 2) / w, 0, 1), Math.Clamp((p.Y - (H - h) / 2) / h, 0, 1));
    }

    void OpenViewer(string id)
    {
        var d = manager.Get(id);
        if (d == null || d.Pending) return;
        if (viewerId == id) return;
        if (viewerId != null && manager.Get(viewerId) is { } old) { manager.ReleaseHq(old); old.IsActive = false; }
        viewerId = id;
        vBmp = null;
        VImg.Source = null;
        VOv.Visibility = Visibility.Visible;
        d.HqFrames?.MarkDirty();
        manager.AcquireHq(d);
        d.IsActive = true;
        ViewerPane.Visibility = Visibility.Visible;
        UpdateViewerHead();
        LayoutViewer();
        Dispatcher.BeginInvoke(() => VScreen.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    void CloseViewer()
    {
        if (viewerId != null && manager.Get(viewerId) is { } d) { manager.ReleaseHq(d); d.IsActive = false; }
        viewerId = null;
        vBmp = null;
        VImg.Source = null;
        ViewerPane.Visibility = Visibility.Collapsed;
    }

    void StepViewer(int dir)
    {
        var ids = VisibleDevices().Where(d => !d.Pending).Select(d => d.Id).ToList();
        if (ids.Count == 0) return;
        int i = viewerId != null ? ids.IndexOf(viewerId) : -1;
        OpenViewer(ids[((i + dir) % ids.Count + ids.Count) % ids.Count]);
    }

    void UpdateViewerHead()
    {
        var d = ViewerDev;
        if (d == null) return;
        VTitle.Text = $"#{d.Num:00} {d.DisplayName}";
        VSub.Text = $"{d.Model} · Android {d.Android} · {(d.Transport ?? "—").ToUpper()}{(d.Battery is int b ? $" · {b}%" : "")}";
        int n = TargetsFor(d).Count;
        VSyncText.Text = T("Đồng bộ {0} máy", n);
        VSync.Visibility = n > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    void LayoutViewer()
    {
        if (viewerId == null) return;
        double w = vBmp?.PixelWidth ?? 9, h = vBmp?.PixelHeight ?? 18.5;
        double availH = ActualHeight - 48 - 46 - 44 - 60;
        double maxW = Math.Max(240, ActualWidth * 0.5);
        double H = Math.Max(200, availH), W = H * w / h;
        if (W > maxW) { W = maxW; H = W * h / w; }
        VScreen.Width = Math.Round(W);
        VScreen.Height = Math.Round(H);
    }

    void UpdateViewerFrame()
    {
        var fb = ViewerDev?.HqFrames;
        if (fb == null) return;
        bool resized = false;
        fb.TryTake((px, w, h) =>
        {
            if (vBmp == null || vBmp.PixelWidth != w || vBmp.PixelHeight != h)
            {
                vBmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                VImg.Source = vBmp;
                resized = true;
            }
            vBmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        });
        if (resized) LayoutViewer();
        if (vBmp != null && VOv.Visibility == Visibility.Visible) VOv.Visibility = Visibility.Collapsed;
    }

    async Task ViewerTool(string act, Button b)
    {
        var d = ViewerDev;
        if (d == null) return;
        if (!act.StartsWith('@')) { PhoneControl.Act(TargetsFor(d), act); return; }
        switch (act)
        {
            case "@paste":
                if (Clipboard.ContainsText()) PasteToPhones(TargetsFor(d), Clipboard.GetText());
                else Ui.Toast("Clipboard máy tính đang trống (chỉ dán được văn bản)", "err");
                break;
            case "@shotclip":
                b.IsEnabled = false;
                try { await ShotToClipboard(d); } finally { b.IsEnabled = true; }
                break;
            case "@shot":
                b.IsEnabled = false;
                try { await SaveShot(d, open: false); } finally { b.IsEnabled = true; }
                break;
        }
    }

    // ---------------- chụp màn hình ----------------
    static async Task<byte[]> CapturePng(PhoneDevice d)
    {
        var png = await AdbClient.ExecOutAsync(d.ActiveAddr!, "screencap -p", 30000);
        if (png.Length < 100 || png[1] != 0x50) throw new IOException(I18n.T("ảnh không hợp lệ"));
        return png;
    }

    /// <summary>Chụp (độ phân giải gốc) và chép thẳng ảnh vào clipboard — dán bằng Ctrl+V.</summary>
    async Task ShotToClipboard(PhoneDevice d)
    {
        try
        {
            var png = await CapturePng(d);
            var img = new BitmapImage();
            img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = new MemoryStream(png); img.EndInit(); img.Freeze();
            var data = new DataObject();
            data.SetImage(img);
            data.SetData("PNG", new MemoryStream(png)); // ứng dụng hỗ trợ PNG (Zalo, Telegram…) giữ nguyên chất lượng
            Clipboard.SetDataObject(data, true);
            Ui.Toast("Đã chép ảnh màn hình — dán bằng Ctrl+V", "ok");
        }
        catch (Exception e) { Ui.Toast(T("Không chép được ảnh vào clipboard: ") + e.Message, "err"); }
    }

    async Task SaveShot(PhoneDevice d, bool open)
    {
        try
        {
            var png = await CapturePng(d);
            var dir = Store.Settings.ScreenshotDir;
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{d.Num:000}_{Actions.SafeName(d.Label != "" ? d.Label : d.Model)}_{DateTime.Now:yyyyMMdd-HHmmss}.png");
            await File.WriteAllBytesAsync(file, png);
            if (open) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
            else Ui.Toast(T("Đã lưu ảnh: {0}", file), "ok", 5000);
        }
        catch (Exception e) { Ui.Toast(e.Message, "err"); }
    }

    void OpenScrcpy(PhoneDevice d)
    {
        var r = actions.LaunchScrcpy(d.ActiveAddr, ["--max-size", "1280", "--no-audio"], manager.LabelOf(d));
        Ui.Toast(r.Msg, r.Ok ? "" : "err");
    }
}
