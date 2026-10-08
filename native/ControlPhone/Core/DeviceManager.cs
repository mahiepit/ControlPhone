using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using ControlPhone.Adb;
using ControlPhone.Scrcpy;
using ControlPhone.Video;

namespace ControlPhone.Core;

/// <summary>
/// Theo dõi máy cắm/rút qua adb, đọc thông tin, mở phiên scrcpy + bộ giải mã cho từng máy và tự kết nối lại.
/// <see cref="Devices"/> chỉ được sửa trên luồng giao diện.
/// </summary>
public sealed class DeviceManager : IDisposable
{
    readonly Dispatcher ui;
    readonly AppConfig config;
    readonly CancellationTokenSource cts = new();
    readonly ConcurrentDictionary<string, PhoneDevice> byAddr = new();
    readonly ConcurrentDictionary<string, byte> probing = new();
    readonly SemaphoreSlim startLimiter;
    readonly SemaphoreSlim probeLimiter = new(8);
    volatile IReadOnlyList<AdbDevice> lastList = [];

    public ObservableCollection<PhoneDevice> Devices { get; } = new();
    public event Action<string>? Log;

    public DeviceManager(Dispatcher ui, AppConfig config)
    {
        this.ui = ui;
        this.config = config;
        startLimiter = new SemaphoreSlim(Math.Max(1, config.MaxParallelStart));
    }

    public void Start()
    {
        _ = Task.Run(() => AdbClient.TrackDevicesAsync(OnTrack, e => Log?.Invoke("adb: " + e), cts.Token));
    }

    void OnTrack(IReadOnlyList<AdbDevice> list)
    {
        lastList = list;
        var present = list.ToDictionary(x => x.Addr, x => x.State);
        // máy đã nhận (có HwId) biến mất / không còn ở trạng thái device → bỏ ô
        foreach (var (addr, dev) in byAddr)
        {
            bool isDevice = present.TryGetValue(addr, out var st) && st == "device";
            if (dev.HwId != "" && !isDevice) Detach(dev);
        }
        foreach (var d in list)
        {
            if (d.State == "device")
            {
                if (byAddr.TryGetValue(d.Addr, out var ph) && ph.HwId == "") RemovePlaceholder(ph); // vừa được cho phép
                if (!byAddr.ContainsKey(d.Addr) && probing.TryAdd(d.Addr, 0)) _ = ProbeAsync(d.Addr);
            }
            else if (!byAddr.ContainsKey(d.Addr))
            {
                // unauthorized / offline: hiện ô thông báo
                var ph = new PhoneDevice(d.Addr) { Model = d.Serial, Status = d.State == "unauthorized" ? PhoneStatus.Unauthorized : PhoneStatus.Offline, Error = "Thiết bị đang " + d.State };
                byAddr[d.Addr] = ph;
                ui.BeginInvoke(() => Insert(ph));
            }
        }
        // ô thông báo của máy đã rút hẳn
        foreach (var (addr, dev) in byAddr)
            if (dev.HwId == "" && !present.ContainsKey(addr)) RemovePlaceholder(dev);
    }

    void RemovePlaceholder(PhoneDevice ph)
    {
        byAddr.TryRemove(ph.Addr, out _);
        ui.BeginInvoke(() => Devices.Remove(ph));
    }

    async Task ProbeAsync(string addr)
    {
        try
        {
            await probeLimiter.WaitAsync(cts.Token);
            string text;
            try
            {
                text = await AdbClient.ShellAsync(addr,
                    "getprop ro.serialno; getprop ro.product.model; getprop ro.build.version.release; echo \"SU=$(which su 2>/dev/null)\"", 20000, cts.Token);
            }
            finally { probeLimiter.Release(); }
            var lines = text.Split('\n').Select(x => x.Trim()).ToArray();
            string serialno = lines.ElementAtOrDefault(0) ?? "";
            if (serialno is "" or "unknown") serialno = addr;
            var dev = new PhoneDevice(addr)
            {
                HwId = serialno,
                Model = lines.ElementAtOrDefault(1) ?? addr,
                Android = lines.ElementAtOrDefault(2) ?? "",
                Root = text.Contains("SU=/"),
            };
            var (label, num) = config.DeviceMeta(serialno);
            dev.Label = label; dev.Num = num;

            // cùng một máy cắm cả USB lẫn WiFi → chỉ 1 ô, ưu tiên USB
            var twin = byAddr.Values.FirstOrDefault(x => x.HwId == serialno);
            if (twin != null)
            {
                if (!twin.IsWifi || dev.IsWifi) return; // đã có USB (hoặc cùng loại) → bỏ kết nối này
                Detach(twin);
            }
            if (!lastList.Any(x => x.Addr == addr && x.State == "device")) return; // đã rút trong lúc đọc
            byAddr[addr] = dev;
            await ui.InvokeAsync(() => Insert(dev));
            StartSession(dev);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log?.Invoke($"Không đọc được thông tin {addr}: {e.Message}"); }
        finally { probing.TryRemove(addr, out _); }
    }

    void Insert(PhoneDevice d)
    {
        int i = 0;
        while (i < Devices.Count && (Devices[i].Num == 0 ? int.MaxValue : Devices[i].Num) <= (d.Num == 0 ? int.MaxValue : d.Num)) i++;
        Devices.Insert(i, d);
    }

    void Detach(PhoneDevice d)
    {
        byAddr.TryRemove(d.Addr, out _);
        d.RestartCts?.Cancel();
        StopSession(d);
        ui.BeginInvoke(() => Devices.Remove(d));
    }

    // ---------- phiên scrcpy ----------
    void StartSession(PhoneDevice d)
    {
        if (cts.IsCancellationRequested || !byAddr.ContainsKey(d.Addr)) return;
        var session = new ScrcpySession(d.Addr, new ScrcpyOptions
        {
            MaxSize = config.ThumbMaxSize, BitRate = config.ThumbBitRate, MaxFps = config.ThumbFps,
            PowerOn = config.PowerOnConnect, StayAwake = config.ScreenOffOnConnect,
        });
        var decoder = d.Decoder ?? new H264Decoder(threads: 1);
        decoder.Resume();
        d.Decoder = decoder;
        d.Session = session;
        decoder.NeedKeyFrame += session.RequestKeyFrame;
        session.Packet += decoder.Feed;
        session.Ended += reason => OnSessionEnded(d, session, reason);
        ui.BeginInvoke(() => { d.Status = PhoneStatus.Connecting; });
        _ = Task.Run(async () =>
        {
            try
            {
                await startLimiter.WaitAsync(cts.Token);
                try { if (!session.Closed) await session.StartAsync(cts.Token); }
                finally { startLimiter.Release(); }
                if (d.Session != session) return;
                d.RestartDelayMs = 1000;
                await ui.InvokeAsync(() => { d.Status = PhoneStatus.Online; d.Error = ""; });
                // mặc định tắt màn hình thật của điện thoại (vẫn truyền hình về máy tính)
                if (config.ScreenOffOnConnect) { await Task.Delay(600); session.Send(ControlMessages.DisplayPower(false)); }
            }
            catch (OperationCanceledException) { session.Dispose(); }
            catch (Exception e)
            {
                ScrcpySession.ForgetPushed(d.Addr); // lần thử lại sẽ kiểm tra/push lại server
                await ui.InvokeAsync(() => d.Error = e.Message);
                session.Dispose();
            }
        });
    }

    void OnSessionEnded(PhoneDevice d, ScrcpySession s, string reason)
    {
        if (d.Session != s) return;
        d.Session = null;
        if (cts.IsCancellationRequested || !byAddr.ContainsKey(d.Addr)) return;
        ui.BeginInvoke(() => { d.Status = PhoneStatus.Connecting; if (string.IsNullOrEmpty(d.Error)) d.Error = $"Đang kết nối lại ({reason})"; });
        var delay = d.RestartDelayMs;
        d.RestartDelayMs = Math.Min(d.RestartDelayMs * 2, 15000);
        d.RestartCts = new CancellationTokenSource();
        var token = d.RestartCts.Token;
        _ = Task.Delay(delay, token).ContinueWith(t => { if (!t.IsCanceled) StartSession(d); }, TaskScheduler.Default);
    }

    static void StopSession(PhoneDevice d)
    {
        var s = d.Session;
        d.Session = null;
        s?.Dispose();
    }

    public void Dispose()
    {
        cts.Cancel();
        foreach (var d in byAddr.Values) { d.RestartCts?.Cancel(); StopSession(d); }
    }
}
