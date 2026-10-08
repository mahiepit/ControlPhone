using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ControlPhone;

/// <summary>
/// Chế độ tự kiểm tra (chỉ khi đặt biến môi trường CP_SELFTEST=&lt;thư mục&gt;): app tự mở từng hộp thoại / màn hình,
/// chụp ảnh nội dung của chính nó ra tệp PNG và ghi lỗi (nếu có) — không cần thao tác chuột/bàn phím.
/// </summary>
public static class SelfTest
{
    public static readonly string? Dir = Environment.GetEnvironmentVariable("CP_SELFTEST");
    public static bool On => !string.IsNullOrEmpty(Dir);
    static readonly List<string> log = [];

    public static void Log(string s) { lock (log) log.Add($"{DateTime.Now:HH:mm:ss.fff} {s}"); }

    public static void Capture(Window w, string name)
    {
        if (!On) return;
        try
        {
            w.UpdateLayout();
            var root = (FrameworkElement)w.Content;
            int pw = Math.Max(1, (int)root.ActualWidth), ph = Math.Max(1, (int)root.ActualHeight);
            var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(w.Background, null, new Rect(0, 0, pw, ph));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, pw, ph));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            Directory.CreateDirectory(Dir!);
            using var fs = File.Create(Path.Combine(Dir!, Safe(name) + ".png"));
            enc.Save(fs);
            Log($"OK  {name} ({pw}x{ph})");
        }
        catch (Exception e) { Log($"ERR capture {name}: {e.Message}"); }
    }

    static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));

    public static void Flush()
    {
        if (!On) return;
        lock (log) File.WriteAllLines(Path.Combine(Dir!, "selftest.log"), log);
    }
}
