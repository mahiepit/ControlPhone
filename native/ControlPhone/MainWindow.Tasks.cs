using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ControlPhone.Core;

namespace ControlPhone;

public partial class MainWindow
{
    readonly Dictionary<int, Border> taskCards = [];

    // ---------------- thông báo ----------------
    void InitToasts()
    {
        Ui.ToastSink = (msg, kind, ms) => Dispatcher.BeginInvoke(() => ShowToast(msg, kind, ms));
    }

    void ShowToast(string msg, string kind, int ms)
    {
        var tb = new TextBlock { Text = msg, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, FontSize = 12.5 };
        var b = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 6, 0, 0), Child = tb, Opacity = 0 };
        b.Background = kind switch { "ok" => new SolidColorBrush(Color.FromRgb(0x1E, 0x7A, 0x4C)), "err" => new SolidColorBrush(Color.FromRgb(0xA8, 0x3A, 0x38)), _ => new SolidColorBrush(Color.FromRgb(0x2B, 0x31, 0x40)) };
        b.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.35 };
        b.MouseLeftButtonUp += (_, _) => ToastList.Items.Remove(b);
        ToastList.Items.Add(b);
        while (ToastList.Items.Count > 5) ToastList.Items.RemoveAt(0);
        b.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => ToastList.Items.Remove(b);
            b.BeginAnimation(OpacityProperty, fade);
        };
        t.Start();
    }

    // ---------------- tiến trình ----------------
    void OnTask(BatchTask task)
    {
        if (!taskCards.TryGetValue(task.Id, out var card))
        {
            card = BuildTaskCard(task);
            taskCards[task.Id] = card;
            TaskList.Items.Insert(0, card);
            while (TaskList.Items.Count > 6) { var last = (Border)TaskList.Items[^1]!; TaskList.Items.Remove(last); }
        }
        UpdateTaskCard(card, task);
        if (task.Finished && card.Tag is not "done")
        {
            card.Tag = "done";
            if (task.OpenResults) ShowTaskResults(task);
            if (task.Op == "screenshot" && task.OkCount > 0) Ui.Toast(T("Đã lưu {0} ảnh chụp", task.OkCount), "ok");
            if (task.Fail == 0)
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                t.Tick += (_, _) => { t.Stop(); TaskList.Items.Remove(card); taskCards.Remove(task.Id); };
                t.Start();
            }
        }
    }

    Border BuildTaskCard(BatchTask task)
    {
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Name = "tt" };
        var close = new Button { Content = Icons.Get("x"), FontSize = 10, Width = 22, Height = 20, MinHeight = 18, Padding = new Thickness(0) };
        close.SetResourceReference(StyleProperty, "IconGhost");
        var head = new DockPanel(); DockPanel.SetDock(close, Dock.Right); head.Children.Add(close); head.Children.Add(title);
        var bar = new ProgressBar { Margin = new Thickness(0, 6, 0, 6), Maximum = 100 };
        var info = new TextBlock { FontSize = 11.5 };
        var sp = new StackPanel(); sp.Children.Add(head); sp.Children.Add(bar); sp.Children.Add(info);
        var card = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 9, 10, 9), Margin = new Thickness(0, 6, 0, 0), Child = sp, BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
        card.SetResourceReference(Border.BackgroundProperty, "Bg2");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.3 };
        close.Click += (_, e) => { e.Handled = true; TaskList.Items.Remove(card); taskCards.Remove(task.Id); };
        card.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject o && FindParent<Button>(o) == null) ShowTaskResults(task); };
        return card;
    }

    static T2? FindParent<T2>(DependencyObject o) where T2 : DependencyObject
    {
        for (var x = o; x != null; x = x is Visual ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x)) if (x is T2 t) return t;
        return null;
    }

    void UpdateTaskCard(Border card, BatchTask task)
    {
        var sp = (StackPanel)card.Child;
        ((TextBlock)((DockPanel)sp.Children[0]).Children[1]).Text = task.Title;
        ((ProgressBar)sp.Children[1]).Value = task.Progress;
        var info = (TextBlock)sp.Children[2];
        info.Inlines.Clear();
        info.Inlines.Add(new System.Windows.Documents.Run($"{task.Done}/{task.Total}   ") { Foreground = Theme.Brush("Muted") });
        info.Inlines.Add(new System.Windows.Documents.Run($"✓ {task.OkCount}   ") { Foreground = Theme.Brush("Ok") });
        if (task.Fail > 0) info.Inlines.Add(new System.Windows.Documents.Run($"✗ {task.Fail}   ") { Foreground = Theme.Brush("Bad") });
        info.Inlines.Add(new System.Windows.Documents.Run(task.StatusText) { Foreground = Theme.Brush("Muted") });
        card.SetResourceReference(Border.BorderBrushProperty, task.Finished ? (task.Fail > 0 ? "Bad" : "Ok") : "Line");
    }

    void ShowTaskResults(BatchTask task)
    {
        var res = task.Results.OrderBy(r => r.Ok).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var d = new Dlg(T("{0} — {1}/{2} thành công", task.Title, task.OkCount, task.Total), 800);
        if (res.Count == 0) d.AddText("Không có máy nào.");
        foreach (var r in res) d.Body.Children.Add(ResultRow(r.Ok, r.Name, r.Msg != "" ? I18n.T(r.Msg) : I18n.T(r.Ok ? "OK" : "Lỗi")));
        if (task.Op == "screenshot")
            d.AddAction("Mở thư mục ảnh", "Btn", () =>
            {
                Directory.CreateDirectory(Store.Settings.ScreenshotDir);
                Process.Start(new ProcessStartInfo("explorer.exe", Store.Settings.ScreenshotDir) { UseShellExecute = true });
                return false;
            }, "folder");
        if (task.Fail > 0)
            d.AddAction("Chọn các máy lỗi", "Btn", () =>
            {
                selected.Clear();
                foreach (var r in task.Results.Where(x => !x.Ok)) selected.Add(r.Id);
                RefreshSelection();
                return true;
            });
        d.AddAction("Đóng", "Primary");
        d.Show();
    }
}
