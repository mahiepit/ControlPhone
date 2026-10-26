using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;

namespace ControlPhone;

/// <summary>
/// Đo độ "lag" (đặt biến môi trường CP_PERFLOG=&lt;tệp&gt;): mỗi giây ghi 1 dòng — luồng giao diện trễ bao lâu mới xử lý
/// được thao tác (đây chính là cảm giác giật / chuột chậm), thời gian chép khung hình, dựng lại lưới, GC.
/// </summary>
public static class PerfLog
{
    public static readonly string? File = Environment.GetEnvironmentVariable("CP_PERFLOG");
    public static bool On => File != null;
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly object gate = new();
    static double lagMax, lagSum; static int lagN;
    static double renderMs; static int renderTicks, framesCopied;
    static double renderAllMs; static int renderAlls;

    public static void AddRender(double ms, int frames) { lock (gate) { renderMs += ms; renderTicks++; framesCopied += frames; } }
    public static void AddRenderAll(double ms) { lock (gate) { renderAllMs += ms; renderAlls++; } }

    public static void Start(Dispatcher ui, Func<string> extra)
    {
        if (!On) return;
        var proc = Process.GetCurrentProcess();
        System.IO.File.AppendAllText(File!, $"# {DateTime.Now:yyyy-MM-dd HH:mm:ss} ControlPhone {typeof(PerfLog).Assembly.GetName().Version} · RenderTier {RenderCapability.Tier >> 16} · {Environment.ProcessorCount} CPU\n" +
            "# t | trễ thao tác TB/max ms | chép khung: lần/s, khung/s, ms/s | dựng lưới: lần/s, ms/s | GC0/1/2 | CPU lõi | thêm\n");
        // đo trễ: cứ 50 ms gửi 1 việc ưu tiên "Input" vào luồng giao diện, xem bao lâu sau mới được chạy
        new Thread(() =>
        {
            while (true)
            {
                var t0 = Clock.Elapsed.TotalMilliseconds;
                ui.BeginInvoke(DispatcherPriority.Input, () => { var lag = Clock.Elapsed.TotalMilliseconds - t0; lock (gate) { lagSum += lag; lagN++; if (lag > lagMax) lagMax = lag; } });
                Thread.Sleep(50);
            }
        }) { IsBackground = true, Name = "perf-probe" }.Start();
        new Thread(() =>
        {
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            var cpu0 = proc.TotalProcessorTime; var w0 = Clock.Elapsed;
            while (true)
            {
                Thread.Sleep(1000);
                string line;
                proc.Refresh();
                var cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds / (Clock.Elapsed - w0).TotalSeconds; cpu0 = proc.TotalProcessorTime; w0 = Clock.Elapsed;
                int n0 = GC.CollectionCount(0), n1 = GC.CollectionCount(1), n2 = GC.CollectionCount(2);
                string ex = ""; try { ex = ui.Invoke(extra, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromMilliseconds(500)); } catch { }
                lock (gate)
                {
                    line = $"{DateTime.Now:HH:mm:ss} | {(lagN > 0 ? lagSum / lagN : 0):0.0}/{lagMax:0} | {renderTicks} {framesCopied} {renderMs:0} | {renderAlls} {renderAllMs:0} | {n0 - g0}/{n1 - g1}/{n2 - g2} | {cpu:0.00} | {ex}";
                    lagMax = lagSum = renderMs = renderAllMs = 0; lagN = renderTicks = framesCopied = renderAlls = 0;
                }
                g0 = n0; g1 = n1; g2 = n2;
                try { System.IO.File.AppendAllText(File!, line + "\n"); } catch { }
            }
        }) { IsBackground = true, Name = "perf-log" }.Start();
    }
}
