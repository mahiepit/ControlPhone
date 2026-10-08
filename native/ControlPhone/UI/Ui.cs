using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ControlPhone.Core;

namespace ControlPhone;

/// <summary>Mục menu chuột phải: null = đường kẻ ngang.</summary>
public sealed record MenuSpec(string Label, string Icon, Action? OnClick, bool Danger = false, bool Enabled = true);

public static class Ui
{
    public static Action<string, string, int>? ToastSink; // (msg, kind, ms)

    public static string T(string s, params object[] a) => I18n.T(s, a);
    public static void Toast(string msg, string kind = "", int ms = 3200) => ToastSink?.Invoke(T(msg), kind, ms);

    public static TextBlock Icon(string name, double size = 14, string? brushKey = null)
    {
        var tb = new TextBlock { Text = Icons.Get(name), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        tb.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey ?? "Text");
        return tb;
    }

    /// <summary>Nút có biểu tượng + chữ.</summary>
    public static Button Btn(string text, string? icon = null, string style = "Btn", RoutedEventHandler? click = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null)
        {
            var ic = Icon(icon, 13);
            ic.Margin = new Thickness(0, 0, text == "" ? 0 : 6, 0);
            if (style is "Primary" or "DangerFill") ic.Foreground = Brushes.White;
            else ic.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            sp.Children.Add(ic);
        }
        if (text != "")
        {
            var tb = new TextBlock { Text = T(text), VerticalAlignment = VerticalAlignment.Center };
            if (style is "Primary" or "DangerFill") tb.Foreground = Brushes.White;
            else tb.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            sp.Children.Add(tb);
        }
        var b = new Button { Content = sp };
        b.SetResourceReference(FrameworkElement.StyleProperty, style);
        if (click != null) b.Click += click;
        return b;
    }

    public static ContextMenu Menu(IEnumerable<MenuSpec?> items)
    {
        var cm = new ContextMenu();
        bool lastSep = true;
        foreach (var it in items)
        {
            if (it == null) { if (!lastSep) cm.Items.Add(new Separator()); lastSep = true; continue; }
            var mi = new MenuItem { Header = T(it.Label), Icon = Icon(it.Icon, 13, it.Danger ? "Bad" : null), IsEnabled = it.Enabled };
            if (it.Danger) mi.SetResourceReference(Control.ForegroundProperty, "Bad");
            var act = it.OnClick;
            if (act != null) mi.Click += (_, _) => act();
            cm.Items.Add(mi);
            lastSep = false;
        }
        if (cm.Items.Count > 0 && cm.Items[^1] is Separator) cm.Items.RemoveAt(cm.Items.Count - 1);
        return cm;
    }

    public static void ShowMenu(IEnumerable<MenuSpec?> items, UIElement? target = null)
    {
        var cm = Menu(items);
        if (target != null) { cm.PlacementTarget = target; cm.Placement = PlacementMode.Bottom; }
        else cm.Placement = PlacementMode.MousePoint;
        cm.IsOpen = true;
    }

    // ---------- tiêu đề cửa sổ tối ----------
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    public static void DarkTitleBar(Window w)
    {
        void Set()
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            int v = Theme.Current == "dark" ? 1 : 0;
            DwmSetWindowAttribute(h, 20, ref v, 4);
        }
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Set(); else w.SourceInitialized += (_, _) => Set();
    }

    // ---------- hộp thoại nhanh ----------
    public static bool Confirm(string title, string text, string ok = "Đồng ý", bool danger = false)
    {
        var d = new Dlg(title);
        d.AddText(text);
        bool res = false;
        d.AddAction("Huỷ");
        d.AddAction(ok, danger ? "DangerFill" : "Primary", () => { res = true; return true; });
        d.ShowDialog();
        return res;
    }

    public static string? Prompt(string title, string label, string value = "", string placeholder = "")
    {
        var d = new Dlg(title);
        var tb = d.AddInput(label, value, placeholder);
        string? res = null;
        d.AddAction("Huỷ");
        d.AddAction("Lưu", "Primary", () => { res = tb.Text; return true; });
        d.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { res = tb.Text; d.DialogResult = true; } };
        d.ShowDialog();
        return res;
    }
}

/// <summary>Hộp thoại kiểu thống nhất: tiêu đề, phần thân (các trường), hàng nút.</summary>
public class Dlg : Window
{
    public StackPanel Body { get; } = new() { Margin = new Thickness(18, 14, 18, 6) };
    readonly StackPanel actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 8, 18, 16) };
    public ScrollViewer Scroller { get; }

    public Dlg(string title, double width = 520)
    {
        Title = Ui.T(title);
        Width = width;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height * 0.92;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        Owner = Application.Current.MainWindow is { IsLoaded: true } mw && mw != this ? mw : null;
        SetResourceReference(BackgroundProperty, "Bg2");
        SetResourceReference(ForegroundProperty, "Text");
        FontSize = 12.5;
        var dock = new DockPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        dock.Children.Add(actions);
        Scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Body };
        dock.Children.Add(Scroller);
        Content = dock;
        Ui.DarkTitleBar(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    /// <summary>Tự kiểm tra: hiện, chụp ảnh, đóng (không chờ người dùng).</summary>
    public new bool? ShowDialog()
    {
        if (!SelfTest.On) return base.ShowDialog();
        Left = -20000; Top = 0; ShowActivated = false;
        base.Show();
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        SelfTest.Capture(this, "dlg_" + Title);
        Close();
        return false;
    }

    public new void Show()
    {
        if (SelfTest.On) { Left = -20000; Top = 0; ShowActivated = false; }
        base.Show();
    }

    public TextBlock AddText(string text, string? brush = null, double size = 12.5)
    {
        var tb = new TextBlock { Text = Ui.T(text), TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = new Thickness(0, 0, 0, 10) };
        if (brush != null) tb.SetResourceReference(TextBlock.ForegroundProperty, brush);
        Body.Children.Add(tb);
        return tb;
    }

    public Border AddNote(string text)
    {
        var b = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 10) };
        b.SetResourceReference(Border.BackgroundProperty, "Bg3");
        var tb = new TextBlock { Text = Ui.T(text), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        b.Child = tb;
        Body.Children.Add(b);
        return b;
    }

    public FrameworkElement Field(string label, FrameworkElement control)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        if (label != "")
        {
            var l = new TextBlock { Text = Ui.T(label), FontSize = 12, Margin = new Thickness(0, 0, 0, 5), TextWrapping = TextWrapping.Wrap };
            l.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            sp.Children.Add(l);
        }
        sp.Children.Add(control);
        return sp;
    }

    public TextBox AddInput(string label, string value = "", string placeholder = "", bool multiline = false)
    {
        var tb = new TextBox { Text = value, Tag = Ui.T(placeholder) };
        if (multiline) { tb.AcceptsReturn = true; tb.TextWrapping = TextWrapping.Wrap; tb.Height = 110; tb.VerticalContentAlignment = VerticalAlignment.Top; tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
        Body.Children.Add(Field(label, tb));
        return tb;
    }

    public CheckBox AddCheck(string text, bool value = false, bool enabled = true)
    {
        var c = new CheckBox { Content = Ui.T(text), IsChecked = value, IsEnabled = enabled, Margin = new Thickness(0, 0, 0, 10) };
        Body.Children.Add(c);
        return c;
    }

    public ComboBox AddCombo(string label, IEnumerable<(string Value, string Text)> items, string value)
    {
        var cb = new ComboBox();
        foreach (var (v, t) in items) cb.Items.Add(new ComboBoxItem { Content = Ui.T(t), Tag = v, IsSelected = v == value });
        Body.Children.Add(Field(label, cb));
        return cb;
    }

    /// <summary>Thêm nút: onClick trả về true = đóng hộp thoại.</summary>
    public Button AddAction(string text, string style = "Btn", Func<bool>? onClick = null, string? icon = null)
    {
        var b = Ui.Btn(text, icon, style);
        b.Margin = new Thickness(8, 0, 0, 0);
        b.MinWidth = 76;
        b.Click += (_, _) =>
        {
            bool close = onClick?.Invoke() ?? true;
            if (close) { try { DialogResult = true; } catch (InvalidOperationException) { Close(); } }
        };
        actions.Children.Add(b);
        return b;
    }

    public Button AddActionAsync(string text, string style, Func<Task<bool>> onClick, string? icon = null)
    {
        var b = Ui.Btn(text, icon, style);
        b.Margin = new Thickness(8, 0, 0, 0);
        b.MinWidth = 76;
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            bool close;
            try { close = await onClick(); } finally { b.IsEnabled = true; }
            if (close) { try { DialogResult = true; } catch (InvalidOperationException) { Close(); } }
        };
        actions.Children.Add(b);
        return b;
    }
}
