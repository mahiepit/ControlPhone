using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using ControlPhone.Core;

namespace ControlPhone;

/// <summary>
/// Phát hiện cửa sổ "đứng hình": luồng nền cứ 100 ms gửi 1 việc nhỏ vào luồng giao diện; quá 200 ms chưa chạy được thì
/// ghi vào data/logs/ui-stalls.log việc gì đang chạy trên luồng giao diện lúc đó (mỗi việc lớn tự đánh dấu bằng <see cref="Op"/>).
/// Rất nhẹ (luôn bật) — để tìm đúng nguyên nhân "lag" trên máy người dùng thay vì đoán.
/// </summary>
public static class StallWatch
{
    static volatile string current = "";
    static long currentSince;
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>Đánh dấu việc đang chạy trên luồng giao diện: using (StallWatch.Op("tên")) { … }</summary>
    public static Scope Op(string name) => new(name);

    public readonly struct Scope : IDisposable
    {
        readonly string prev; readonly long prevSince;
        public Scope(string name) { prev = current; prevSince = currentSince; current = name; currentSince = Clock.ElapsedMilliseconds; }
        public void Dispose() { current = prev; currentSince = prevSince; }
    }

    public static void Start(Dispatcher ui)
    {
        var file = Path.Combine(Store.DataDir, "logs", "ui-stalls.log");
        new Thread(() =>
        {
            while (true)
            {
                long sent = Clock.ElapsedMilliseconds;
                int done = 0;
                ui.BeginInvoke(DispatcherPriority.Input, () => Interlocked.Exchange(ref done, 1));
                Thread.Sleep(200);
                if (Volatile.Read(ref done) == 0)
                {
                    // đang đứng: ghi việc đang chạy, rồi chờ tới khi hết đứng để biết đứng bao lâu
                    var what = current; var opAge = current != "" ? Clock.ElapsedMilliseconds - currentSince : 0;
                    while (Volatile.Read(ref done) == 0 && Clock.ElapsedMilliseconds - sent < 30_000) Thread.Sleep(20);
                    var ms = Clock.ElapsedMilliseconds - sent;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Delete(file);
                        File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} · đứng ≥ {ms} ms · đang chạy: {(what != "" ? $"{what} (đã {opAge} ms)" : "(không rõ — WPF vẽ/bố cục hoặc GC)")} · GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} · v{typeof(StallWatch).Assembly.GetName().Version?.ToString(3)}\n");
                    }
                    catch { }
                }
                else Thread.Sleep(100);
            }
        }) { IsBackground = true, Name = "stall-watch", Priority = ThreadPriority.AboveNormal }.Start();
    }
}
