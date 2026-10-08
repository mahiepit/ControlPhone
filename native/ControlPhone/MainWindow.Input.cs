using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ControlPhone.Core;
using ControlPhone.Scrcpy;

namespace ControlPhone;

public partial class MainWindow
{
    string? lastClickedId;
    (string Id, HashSet<string> Prev, long At)? lastPlainClick;

    // kéo khung chọn
    sealed class DragState { public Point Start; public string? TileId; public bool Moved, Add, Shift, Ctrl; public HashSet<string> Base = []; }
    DragState? drag;

    // chạm (ngón tay ảo)
    sealed class TouchState { public List<PhoneDevice> Targets = []; public PhoneTile? Tile; public FrameworkElement Box = null!; public bool Pinch; public bool Viewer; public Point? Pending; public long LastMove; }
    TouchState? touch;
    PhoneDevice? hoverDev;

    void InitInput()
    {
        GridArea.PreviewMouseDown += GridMouseDown;
        GridArea.PreviewMouseMove += GridMouseMove;
        GridArea.PreviewMouseUp += GridMouseUp;
        GridArea.PreviewMouseWheel += GridWheel;
        GridArea.MouseMove += (_, e) => hoverDev = TileAt(e.OriginalSource, out var t) is { } d && t != null && IsOver(t.ScreenArea, e) && d.IsOnline ? d : null;
        GridArea.MouseLeave += (_, _) => hoverDev = null;
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        PreviewTextInput += OnTextInput;
        DragEnter += OnDragEnter;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += OnDrop;
    }

    static PhoneDevice? TileAt(object src, out PhoneTile? tile)
    {
        tile = null;
        for (var o = src as DependencyObject; o != null; o = o is Visual || o is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o))
            if (o is PhoneTile t) { tile = t; return t.Device; }
        return null;
    }

    static bool IsOver(FrameworkElement el, MouseEventArgs e)
    {
        var p = e.GetPosition(el);
        return p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight;
    }

    // ---------------- chuột trên lưới ----------------
    void GridMouseDown(object sender, MouseButtonEventArgs e)
    {
        var d = TileAt(e.OriginalSource, out var tile);
        bool onScreen = tile != null && IsOver(tile.ScreenArea, e);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (d != null && e.ChangedButton == MouseButton.Right) { e.Handled = true; TileMenu(d); return; } // chuột phải trên ô = menu
        // thao tác trên màn hình nhỏ: máy ngoài nhóm → chọn riêng máy này; máy trong nhóm → giữ nhóm (đồng bộ)
        if (d != null && tile != null && onScreen && !ctrl)
        {
            e.Handled = true;
            if (d.Pending) return;
            if (!selected.Contains(d.Id)) SelectOnly(d.Id);
            if (!d.IsOnline) return;
            var targets = TargetsFor(d);
            if (e.ChangedButton == MouseButton.Middle) { PhoneControl.Act(targets, "home"); return; }
            if (e.ChangedButton == MouseButton.XButton1) { PhoneControl.Act(targets, "back"); return; }
            if (e.ChangedButton != MouseButton.Left) return;
            StartTouch(targets, tile, tile.ScreenArea, tile.Norm(e.GetPosition(tile.ScreenArea)), viewer: false);
            GridArea.CaptureMouse();
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        // nhấp đúp lên tiêu đề ô = mở màn hình lớn (trả lại nhóm đang chọn trước cú click đầu tiên)
        if (e.ClickCount == 2 && d is { Pending: false })
        {
            e.Handled = true;
            if (lastPlainClick is { } lp && lp.Id == d.Id && Environment.TickCount64 - lp.At < 700) { selected.Clear(); selected.UnionWith(lp.Prev); lastPlainClick = null; RefreshSelection(); }
            OpenViewer(d.Id);
            return;
        }
        drag = new DragState { Start = e.GetPosition(GridArea), TileId = d?.Id, Add = ctrl || shift, Shift = shift, Ctrl = ctrl, Base = [.. selected] };
        GridArea.CaptureMouse();
        e.Handled = d != null; // ô: không để ScrollViewer lấy focus; vùng trống: cho phép cuộn
    }

    void GridMouseMove(object sender, MouseEventArgs e)
    {
        if (touch is { Viewer: false } t)
        {
            t.Pending = t.Tile!.Norm(e.GetPosition(t.Box));
            FlushMoveThrottled();
            return;
        }
        if (drag == null) return;
        var p = e.GetPosition(GridArea);
        if (!drag.Moved && (p - drag.Start).Length < 6) return;
        drag.Moved = true;
        double x1 = Math.Min(drag.Start.X, p.X), y1 = Math.Min(drag.Start.Y, p.Y), x2 = Math.Max(drag.Start.X, p.X), y2 = Math.Max(drag.Start.Y, p.Y);
        Marquee.Visibility = Visibility.Visible;
        Canvas.SetLeft(Marquee, x1); Canvas.SetTop(Marquee, y1);
        Marquee.Width = x2 - x1; Marquee.Height = y2 - y1;
        var box = new Rect(x1, y1, x2 - x1, y2 - y1);
        selected.Clear();
        if (drag.Add) selected.UnionWith(drag.Base);
        foreach (var tile in PhoneTile.Live)
        {
            if (tile.Device is not { Pending: false } d || !tile.IsVisible) continue;
            try { if (tile.TransformToAncestor(GridArea).TransformBounds(new Rect(0, 0, tile.ActualWidth, tile.ActualHeight)).IntersectsWith(box)) selected.Add(d.Id); }
            catch (InvalidOperationException) { }
        }
        // tự cuộn khi kéo sát mép
        if (p.Y > GridArea.ActualHeight - 30) GridScroll.ScrollToVerticalOffset(GridScroll.VerticalOffset + 20);
        else if (p.Y < 30) GridScroll.ScrollToVerticalOffset(GridScroll.VerticalOffset - 20);
        RefreshSelection();
    }

    void GridMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (touch is { Viewer: false }) { EndTouch(touch.Tile!.Norm(e.GetPosition(touch.Box))); GridArea.ReleaseMouseCapture(); e.Handled = true; return; }
        if (drag == null) return;
        var dd = drag;
        drag = null;
        GridArea.ReleaseMouseCapture();
        Marquee.Visibility = Visibility.Collapsed;
        if (dd.Moved) return;
        if (dd.TileId == null) { if (!dd.Add) ClearSelection(); return; }
        var d = manager.Get(dd.TileId);
        if (d == null || d.Pending) return;
        // Click = chọn 1 máy · Ctrl+click = thêm/bớt vào nhóm · Shift+click = chọn cả dãy
        if (dd.Ctrl || dd.Shift) ToggleSelect(d.Id, dd.Shift);
        else
        {
            long now = Environment.TickCount64;
            bool second = lastPlainClick is { } lp && lp.Id == d.Id && now - lp.At < 700 && selected.Count == 1 && selected.Contains(d.Id);
            if (!second) lastPlainClick = (d.Id, [.. selected], now);
            SelectOnly(d.Id);
        }
    }

    void GridWheel(object sender, MouseWheelEventArgs e)
    {
        // ô nhỏ: lăn chuột thường để cuộn danh sách máy, Shift+lăn mới cuộn trong điện thoại
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        var d = TileAt(e.OriginalSource, out var tile);
        if (d is not { IsOnline: true } || tile == null || !IsOver(tile.ScreenArea, e)) return;
        e.Handled = true;
        var p = tile.Norm(e.GetPosition(tile.ScreenArea));
        float v = Math.Clamp(e.Delta / 120f, -16, 16);
        foreach (var x in TargetsFor(d)) x.ControlSession?.Scroll(p.X, p.Y, 0, v);
    }

    // ---------------- chạm (dùng chung lưới + màn hình lớn) ----------------
    void StartTouch(List<PhoneDevice> targets, PhoneTile? tile, FrameworkElement box, Point p, bool viewer)
    {
        bool pinch = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        touch = new TouchState { Targets = targets, Tile = tile, Box = box, Pinch = pinch, Viewer = viewer };
        SendTouch(ControlMessages.ActionDown, p);
    }

    void SendTouch(byte action, Point p)
    {
        if (touch == null) return;
        foreach (var d in touch.Targets)
        {
            var s = d.ControlSession;
            if (s == null) continue;
            if (touch.Pinch)
            {
                s.Touch(action, p.X, p.Y, 1);
                s.Touch(action, 1 - p.X, 1 - p.Y, 2);
            }
            else s.Touch(action, p.X, p.Y);
        }
    }

    void FlushMoveThrottled()
    {
        if (touch?.Pending is not { } p) return;
        long now = Environment.TickCount64;
        if (now - touch.LastMove < 12) return; // gửi tối đa ~80 lần/giây
        touch.LastMove = now;
        touch.Pending = null;
        SendTouch(ControlMessages.ActionMove, p);
    }

    void EndTouch(Point p)
    {
        if (touch == null) return;
        if (touch.Pending is { } pend) SendTouch(ControlMessages.ActionMove, pend);
        SendTouch(ControlMessages.ActionUp, p);
        touch = null;
    }

    // ---------------- bàn phím → điện thoại ----------------
    PhoneDevice? KeyboardTarget()
    {
        if (viewerId != null && (VScreen.IsKeyboardFocused || VScreen.IsMouseOver)) return manager.Get(viewerId);
        return hoverDev;
    }

    static readonly Dictionary<Key, int> SpecialKeys = new()
    {
        [Key.Enter] = 66, [Key.Back] = 67, [Key.Delete] = 112, [Key.Tab] = 61,
        [Key.Up] = 19, [Key.Down] = 20, [Key.Left] = 21, [Key.Right] = 22,
        [Key.Home] = 122, [Key.End] = 123, [Key.PageUp] = 92, [Key.PageDown] = 93, [Key.Insert] = 124,
    };

    static int Meta() => (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0x41 : 0) | (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 0x3000 : 0) | (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? 0x12 : 0);

    static int AndroidKeycode(Key k)
    {
        if (SpecialKeys.TryGetValue(k, out var c)) return c;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control) || mods.HasFlag(ModifierKeys.Alt))
        {
            if (k >= Key.A && k <= Key.Z) return 29 + (k - Key.A);
            if (k >= Key.D0 && k <= Key.D9) return 7 + (k - Key.D0);
        }
        return 0;
    }

    static bool InTextField() => Keyboard.FocusedElement is TextBox or ComboBox or PasswordBox;

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (InTextField()) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control && key == Key.V)
        {
            var targets = PasteTargets();
            if (targets.Count == 0) return;
            e.Handled = true;
            PasteToPhones(targets, Clipboard.ContainsText() ? Clipboard.GetText() : "");
            return;
        }
        // Esc luôn đóng màn hình phóng to; nút Quay lại của điện thoại = chuột phải / nút bên phải
        if (key == Key.Escape && viewerId != null) { e.Handled = true; CloseViewer(); return; }
        var kd = KeyboardTarget();
        if (kd is { IsOnline: true })
        {
            if (mods == ModifierKeys.Control && key == Key.C) { e.Handled = true; PhoneControl.Act([kd], "copy"); return; }
            if (key is not (Key.F3 or Key.F5 or Key.F11 or Key.F12))
            {
                int code = AndroidKeycode(key);
                if (code != 0)
                {
                    e.Handled = true;
                    FlushText();
                    foreach (var d in TargetsFor(kd)) d.ControlSession?.Send(ControlMessages.Keycode(0, code, 0, Meta()));
                    return;
                }
            }
        }
        // phím tắt ứng dụng
        if (key == Key.F3) { e.Handled = true; SetSyncOff(!syncOff); return; }
        if (mods == ModifierKeys.Control && key == Key.A) { e.Handled = true; SelectWhere(MatchFilter); return; }
        if (key == Key.Escape) ClearSelection();
    }

    void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (InTextField()) return;
        var kd = KeyboardTarget();
        if (kd is not { IsOnline: true }) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Control && key is Key.V or Key.C) return;
        int code = AndroidKeycode(key);
        if (code != 0) { e.Handled = true; foreach (var d in TargetsFor(kd)) d.ControlSession?.Send(ControlMessages.Keycode(1, code, 0, Meta())); }
    }

    // chữ (kể cả tiếng Việt từ bộ gõ): gom 25 ms rồi gửi một lần
    string textBuf = "";
    List<PhoneDevice>? textTargets;
    System.Windows.Threading.DispatcherTimer? textTimer;

    void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (InTextField() || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        var kd = KeyboardTarget();
        if (kd is not { IsOnline: true }) return;
        e.Handled = true;
        var targets = TargetsFor(kd);
        if (textTargets != null && !textTargets.SequenceEqual(targets)) FlushText();
        textTargets = targets;
        textBuf += e.Text;
        textTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(25), System.Windows.Threading.DispatcherPriority.Input, (_, _) => FlushText(), Dispatcher);
        textTimer.Stop(); textTimer.Start();
    }

    void FlushText()
    {
        textTimer?.Stop();
        if (textBuf != "" && textTargets != null) foreach (var d in textTargets) d.ControlSession?.TypeText(textBuf);
        textBuf = "";
        textTargets = null;
    }

    // ---------------- clipboard ----------------
    /// <summary>Máy nhận Ctrl+V: mọi máy đang chọn (online). Chưa chọn máy nào → máy đang trỏ chuột / màn hình lớn.</summary>
    List<PhoneDevice> PasteTargets()
    {
        var kd = KeyboardTarget();
        if (syncOff && kd != null) return [kd];
        var set = selected.Select(manager.Get).Where(d => d is { IsOnline: true }).Cast<PhoneDevice>().ToList();
        if (set.Count == 0 && kd != null) set.Add(kd);
        return set;
    }

    void PasteToPhones(List<PhoneDevice> targets, string text, bool enter = false)
    {
        if (string.IsNullOrEmpty(text)) { Ui.Toast("Clipboard máy tính đang trống (chỉ dán được văn bản)", "err"); return; }
        FlushText();
        foreach (var d in targets)
        {
            var s = d.ControlSession;
            if (s == null) continue;
            s.TypeText(text, paste: true);
            if (enter) _ = Task.Delay(150).ContinueWith(_ => s.KeyPress(AndroidKey.Enter));
        }
        var preview = text.Length > 40 ? text[..40] + "…" : text;
        Ui.Toast(T("Đã dán vào {0} máy: \"{1}\"", targets.Count, preview), "ok");
    }

    void OnPhoneClipboard(PhoneDevice d, string text)
    {
        try { Clipboard.SetText(text); Ui.Toast("Đã sao chép clipboard từ điện thoại", "ok"); } catch { }
    }

    // ---------------- kéo thả tệp ----------------
    void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        DropText.Text = T("Thả tệp: .apk → cài đặt · tệp khác → gửi vào /sdcard/Download ({0} máy đang chọn)", selected.Count);
        DropOverlay.Visibility = Visibility.Visible;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        if (SelIds().Count == 0) return;
        var apks = files.Where(f => Path.GetExtension(f).Equals(".apk", StringComparison.OrdinalIgnoreCase)).ToList();
        var others = files.Where(f => File.Exists(f) && !apks.Contains(f)).ToList();
        if (apks.Count > 0) InstallApks(apks, null);
        if (others.Count > 0) DlgPush(others, null);
    }
}
