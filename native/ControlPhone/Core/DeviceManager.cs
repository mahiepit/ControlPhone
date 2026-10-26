using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using ControlPhone.Adb;
using ControlPhone.Scrcpy;
using ControlPhone.Video;

namespace ControlPhone.Core;

/// <summary>
/// Quản lý thiết bị (chuyển từ server/devices.js). Mọi trạng thái được xử lý trên luồng giao diện
/// (các lệnh adb chạy bất đồng bộ); gói video đi thẳng từ luồng đọc của từng máy vào bộ giải mã.
/// </summary>
public sealed class DeviceManager : IDisposable
{
    readonly Dispatcher ui;
    readonly CancellationTokenSource cts = new();
    readonly Dictionary<string, string> serialToId = [];      // addr → hwId
    readonly Dictionary<string, string> serialStates = [];    // addr → state
    readonly HashSet<string> probing = [];
    readonly Dictionary<string, string> probeErrors = [];
    readonly SemaphoreSlim probeLimiter = new(8), infoLimiter = new(8);
    SemaphoreSlim startLimiter;
    IReadOnlyList<AdbDevice> lastTrack = [];
    DispatcherTimer? infoTimer, wifiTimer;
    bool stopping;

    public ObservableCollection<PhoneDevice> Devices { get; } = new();
    readonly Dictionary<string, PhoneDevice> byId = [];
    public event Action<string, bool>? Log;               // (thông báo, là lỗi)
    public event Action<PhoneDevice, string>? Clipboard;   // điện thoại gửi clipboard
    public event Action? ListChanged;

    public DeviceManager(Dispatcher ui)
    {
        this.ui = ui;
        startLimiter = new SemaphoreSlim(Math.Max(1, Store.Settings.MaxParallelStart));
    }

    public PhoneDevice? Get(string id) => byId.GetValueOrDefault(id);
    public PhoneDevice? ByVid(int vid) => byId.Values.FirstOrDefault(d => d.Vid == vid);
    public string LabelOf(PhoneDevice d) => $"#{d.Num} {(d.Label != "" ? d.Label : d.Model != "" ? d.Model : d.Id)}";

    public void Start()
    {
        // danh sách "serial bị trùng" của bản ≤ 2.0.7 không còn dùng (định danh giờ là serial USB)
        if (Store.State.DupSerials.Count > 0) { Store.State.DupSerials.Clear(); Store.Save(); }
        _ = Task.Run(async () =>
        {
            await AdbClient.StartServerAsync();
            await AdbClient.TrackDevicesAsync(list => ui.BeginInvoke(() => OnTrack(list)), e => ui.BeginInvoke(() => Log?.Invoke("adb: " + e, true)), cts.Token);
        });
        infoTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => RefreshAllInfo(), ui);
        wifiTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => _ = AutoReconnectWifiAsync(), ui);
        infoTimer.Start(); wifiTimer.Start();
    }

    void Add(PhoneDevice d) { byId[d.Id] = d; Devices.Add(d); ListChanged?.Invoke(); }
    void Remove(PhoneDevice d) { if (byId.TryGetValue(d.Id, out var x) && x == d) byId.Remove(d.Id); Devices.Remove(d); ListChanged?.Invoke(); }
    void Changed(PhoneDevice d) { d.Changed(); ListChanged?.Invoke(); }

    // ---------- theo dõi adb ----------
    void OnTrack(IReadOnlyList<AdbDevice> list)
    {
        using var _op = StallWatch.Op("adb: danh sách máy thay đổi");
        if (stopping) return;
        lastTrack = list;
        var seen = list.ToDictionary(x => x.Addr, x => x.State);
        // serial biến mất hoặc không còn ở trạng thái device
        foreach (var (addr, id) in serialToId.ToList())
        {
            if (seen.GetValueOrDefault(addr) == "device") continue;
            serialToId.Remove(addr);
            ScrcpySession.ForgetPushed(addr);
            if (byId.TryGetValue(id, out var d)) Detach(d, addr);
        }
        // ô thông báo (pending) không còn đúng trạng thái
        foreach (var d in byId.Values.Where(x => x.Pending).ToList())
        {
            var st = seen.GetValueOrDefault(d.ActiveAddr ?? "");
            bool probeErrorValid = d.Status == "error" && st == "device";
            if (!probeErrorValid && st != d.Status) { d.RestartCts?.Cancel(); Remove(d); }
        }
        foreach (var x in list)
        {
            if (x.State == "device")
            {
                if (!serialToId.ContainsKey(x.Addr) && !probing.Contains(x.Addr)) _ = ProbeAsync(x.Addr);
            }
            else
            {
                var pid = "pending:" + x.Addr;
                if (!byId.TryGetValue(pid, out var d))
                {
                    d = new PhoneDevice(pid) { Pending = true, ActiveAddr = x.Addr, Model = x.Addr };
                    d.Transports[x.Addr] = AdbClient.IsWifiSerial(x.Addr) ? "wifi" : "usb";
                    Add(d);
                }
                d.Status = x.State;
                d.Error = x.State == "unauthorized" ? "Hãy bấm \"Cho phép gỡ lỗi USB\" trên điện thoại" : "Thiết bị đang " + x.State;
                Changed(d);
            }
        }
        foreach (var k in serialStates.Keys.ToList()) if (!seen.ContainsKey(k)) serialStates.Remove(k);
        foreach (var (k, v) in seen) serialStates[k] = v;
    }

    async Task ProbeAsync(string addr)
    {
        probing.Add(addr);
        try
        {
            string text = "";
            Exception? lastErr = null;
            await probeLimiter.WaitAsync(cts.Token);
            try
            {
                // hỏi theo lô (tránh 40–100 máy cùng lúc làm máy bận trả lời quá hạn)
                for (int i = 0; i < 3 && text == ""; i++)
                {
                    try
                    {
                        text = await AdbClient.ShellAsync(addr, "getprop ro.serialno; getprop ro.product.model; getprop ro.product.manufacturer; getprop ro.build.version.release; getprop ro.build.version.sdk; echo \"SU=$(which su 2>/dev/null)\"; echo \"UID=$(id -u)\"", 20000, cts.Token);
                    }
                    catch (Exception e) when (!cts.IsCancellationRequested) { lastErr = e; await Task.Delay(1500, cts.Token); }
                }
                if (text == "") throw lastErr ?? new Exception("không đọc được thông tin");
                // Android ID đọc riêng: nếu chậm/lỗi vẫn nhận máy bình thường
                try { text += await AdbClient.ShellAsync(addr, "echo \"AID=$(settings get secure android_id 2>/dev/null)\"", 15000, cts.Token); } catch { }
            }
            finally { probeLimiter.Release(); }
            if (serialStates.GetValueOrDefault(addr) != "device") return;
            using var _op = StallWatch.Op("nhận máy (sau khi đọc thông tin)");
            ClearProbeError(addr);
            var lines = text.Split('\n').Select(s => s.Trim()).ToArray();
            var aidM = Regex.Match(text, @"AID=([0-9a-f]{6,})", RegexOptions.IgnoreCase);
            var androidId = aidM.Success ? aidM.Groups[1].Value.ToLowerInvariant() : "";
            var type = AdbClient.IsWifiSerial(addr) ? "wifi" : "usb";
            var hwId = ResolveId(lines.ElementAtOrDefault(0) ?? "", androidId, type, addr);
            if (byId.TryGetValue("pending:" + addr, out var pend)) Remove(pend);
            if (!byId.TryGetValue(hwId, out var d)) { d = new PhoneDevice(hwId); Add(d); }
            d.Model = lines.ElementAtOrDefault(1) ?? ""; d.Manufacturer = lines.ElementAtOrDefault(2) ?? "";
            d.Android = lines.ElementAtOrDefault(3) ?? ""; d.Sdk = int.TryParse(lines.ElementAtOrDefault(4), out var sdk) ? sdk : 0;
            d.Root = Regex.IsMatch(text, @"SU=\S+") || text.Contains("UID=0");
            d.AndroidId = androidId;
            d.Transports[addr] = type;
            serialToId[addr] = hwId;
            var meta = Store.Meta(hwId);
            if (androidId != "" && meta.AndroidId != androidId) { meta.AndroidId = androidId; Store.Save(); }
            if (type == "wifi") RememberWifi(hwId, addr);
            // máy root: tự cấy khoá uỷ quyền của PC (1 lần/máy) → không bao giờ hỏi lại, kể cả box không màn hình
            if (d.Root && Store.Settings.AutoPersistKey && !meta.KeyPushed)
                _ = Task.Run(async () => { var r = await AdbClient.PersistAdbKeyAsync(addr); if (r.Ok) ui.Invoke(() => { meta.KeyPushed = true; Store.Save(); }); });
            // chế độ "chỉ nhập từ PC": bàn phím có thể bị đổi khi máy khởi động lại → chọn lại
            if (meta.PcKeyboard) _ = Task.Run(async () => { try { await PcKeyboard.EnsureAsync(addr); } catch { } });
            ChooseTransport(d);
            RefreshInfo(d);
            Changed(d);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { SetProbeError(addr, e.Message); }
        finally { probing.Remove(addr); }
    }

    // Máy chưa đọc được thông tin: hiện ô báo lỗi (không bao giờ bỏ máy im lặng) và tự thử lại
    void SetProbeError(string addr, string msg)
    {
        var pid = "pending:" + addr;
        if (!byId.TryGetValue(pid, out var d))
        {
            d = new PhoneDevice(pid) { Pending = true, ActiveAddr = addr, Model = addr };
            d.Transports[addr] = AdbClient.IsWifiSerial(addr) ? "wifi" : "usb";
            Add(d);
        }
        d.Status = "error";
        d.ProbeFails++;
        d.Error = $"Không đọc được thông tin máy ({msg}) — đang thử lại…";
        probeErrors[addr] = msg;
        Changed(d);
        var delay = Math.Min(5000 * d.ProbeFails, 30000);
        d.RestartCts?.Cancel();
        d.RestartCts = new CancellationTokenSource();
        var tok = d.RestartCts.Token;
        _ = Task.Delay(delay, tok).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            ui.BeginInvoke(() => { if (serialStates.GetValueOrDefault(addr) == "device" && !serialToId.ContainsKey(addr) && !probing.Contains(addr)) _ = ProbeAsync(addr); });
        }, TaskScheduler.Default);
    }

    void ClearProbeError(string addr)
    {
        probeErrors.Remove(addr);
        if (byId.TryGetValue("pending:" + addr, out var d)) { d.RestartCts?.Cancel(); Remove(d); }
    }

    /// <summary>
    /// Định danh ổn định của máy = SERIAL USB mà adb thấy (do bootloader / phần cứng cấp — giống Xiaowei). Nó KHÔNG đổi khi
    /// cập nhật Android, cài ROM mới hay khôi phục cài đặt gốc. Trước đây (≤ 2.0.7) dùng ro.serialno + Android ID: ROM nhân
    /// bản cho nhiều máy CÙNG ro.serialno, còn Android ID đổi sau mỗi lần cài ROM → máy bị coi là máy khác, mất số thứ tự.
    /// Máy qua WiFi (serial là ip:port) được gắn vào máy USB đã biết nhờ địa chỉ WiFi đã nhớ hoặc ro.serialno + Android ID.
    /// </summary>
    string ResolveId(string serialno, string aid, string type, string addr)
    {
        serialno = serialno.Trim();
        if (serialno == "unknown") serialno = "";
        var aid12 = aid[..Math.Min(12, aid.Length)];
        var all = Store.State.Devices;
        if (type == "usb")
        {
            var (usb, tid) = AdbClient.SplitAddr(addr);
            // 2 kết nối USB trùng cả serial USB (máy nhân bản ở mức phần cứng — hiếm) → phân biệt thêm bằng Android ID
            var id = tid == null ? usb : $"{usb}#{(aid12 != "" ? aid12 : "t" + tid)}";
            MigrateOldEntries(id, usb, serialno, aid, aid12);
            var m = Store.Meta(id);
            if (serialno != "" && m.SerialNo != serialno) { m.SerialNo = serialno; Store.Save(); }
            return id;
        }
        // WiFi: địa chỉ đã nhớ của đúng máy này (Android ID khớp) → máy đó
        foreach (var (k, m) in all)
            if (m.Wifi == addr && (aid == "" || string.IsNullOrEmpty(m.AndroidId) || m.AndroidId == aid)) return k;
        // cùng ro.serialno + Android ID với một máy đã biết (vd. vừa chuyển USB → WiFi)
        if (serialno != "" && aid != "")
            foreach (var (k, m) in all)
                if (m.SerialNo == serialno && m.AndroidId == aid) return k;
        return serialno != "" ? $"wifi:{serialno}#{aid12}" : "wifi:" + addr;
    }

    /// <summary>
    /// Chuyển mục cũ (bản ≤ 2.0.7 đặt theo ro.serialno / "serial#androidId" / "aid:…") của CHÍNH máy này sang định danh
    /// serial USB, giữ số thứ tự / tên / nhóm. Có nhiều mục cũ (máy từng bị tách nhầm) → giữ mục có số nhỏ nhất, bỏ mục thừa.
    /// </summary>
    void MigrateOldEntries(string id, string usb, string serialno, string aid, string aid12)
    {
        var all = Store.State.Devices;
        bool Mine(string k, DeviceMeta m) => k != id && !byId.ContainsKey(k) && (
            k.StartsWith(usb + "#", StringComparison.Ordinal) || k.StartsWith(usb + "~", StringComparison.Ordinal)
            || (serialno != "" && aid12 != "" && k == $"{serialno}#{aid12}")
            || (serialno != "" && k == serialno && serialno != usb && (string.IsNullOrEmpty(m.AndroidId) || m.AndroidId == aid))
            || (aid != "" && k == "aid:" + aid));
        var old = all.Where(kv => Mine(kv.Key, kv.Value)).ToList();
        if (old.Count == 0) return;
        if (!all.ContainsKey(id))
        {
            var keep = old.OrderBy(kv => kv.Value.Num > 0 ? kv.Value.Num : int.MaxValue).First().Value;
            if (keep.Label == "") keep.Label = old.Select(kv => kv.Value.Label).FirstOrDefault(l => l != "") ?? "";
            keep.KeyPushed |= old.Any(kv => kv.Value.KeyPushed);
            all[id] = keep;
        }
        foreach (var (k, _) in old)
        {
            all.Remove(k);
            if (Store.State.Solo != null) Store.State.Solo = Store.State.Solo.Select(x => x == k ? id : x).Distinct().ToList();
        }
        Store.Save();
    }
    void RememberWifi(string hwId, string addr)
    {
        Store.Meta(hwId).Wifi = addr;
        if (!Store.State.WifiHistory.Contains(addr)) Store.State.WifiHistory.Add(addr);
        Store.Save();
    }

    void Detach(PhoneDevice d, string addr)
    {
        d.Transports.Remove(addr);
        if (d.ActiveAddr == addr) { d.ActiveAddr = null; StopSessions(d); }
        ChooseTransport(d);
        Changed(d);
    }

    void ChooseTransport(PhoneDevice d)
    {
        var prefer = Store.Settings.PreferTransport == "wifi" ? "wifi" : "usb";
        string? best = null;
        foreach (var (addr, type) in d.Transports)
            if (best == null || (type == prefer && d.Transports[best] != prefer)) best = addr;
        if (best == d.ActiveAddr)
        {
            if (best == null) { d.Status = "offline"; d.Error = "Mất kết nối"; }
            return;
        }
        if (d.ActiveAddr != null) StopSessions(d);
        d.ActiveAddr = best;
        if (best == null) { d.Status = "offline"; d.Error = "Mất kết nối"; return; }
        d.RestartDelayMs = 1000;
        d.StartFails = 0; // kết nối mới (cắm lại / đổi USB↔WiFi): thử lại nhanh từ đầu
        EnsureThumb(d);
        if (d.HqRefs > 0) EnsureHq(d);
    }

    public void ReapplyTransportPreference() { foreach (var d in byId.Values.Where(x => !x.Pending).ToList()) { ChooseTransport(d); Changed(d); } }

    // ---------- phiên scrcpy ----------
    /// <summary>Luồng lưới có truyền hình không: không khi tạm dừng xem / bị ẩn, và không khi đang phóng to.</summary>
    static bool ThumbWantsVideo(PhoneDevice d) => !d.Paused && !d.SoloHidden && d.HqRefs == 0;

    const string SwEncoder = "OMX.google.h264.encoder";
    static string? GridEncoder(PhoneDevice d) => Store.Settings.SoftwareEncoder && !d.NoSwEncoder ? SwEncoder : null;

    /// <summary>Lỗi khởi động do máy không có bộ mã hoá đã chọn → ghi nhớ và dùng bộ mã hoá mặc định.</summary>
    static bool EncoderMissing(ScrcpySession s, string error) =>
        s.Options.Encoder != null && (error + "\n" + s.LogText).Contains("encoder", StringComparison.OrdinalIgnoreCase)
        && (error + "\n" + s.LogText).Contains("not found", StringComparison.OrdinalIgnoreCase);

    ScrcpyOptions ThumbOptions(PhoneDevice d)
    {
        var s = Store.Settings;
        return new ScrcpyOptions
        {
            MaxSize = s.ThumbMaxSize, BitRate = s.ThumbBitRate, MaxFps = s.ThumbFps, PowerOn = s.PowerOnConnect,
            StayAwake = s.ScreenOffOnConnect, Video = ThumbWantsVideo(d), Encoder = GridEncoder(d),
        };
    }

    ScrcpyOptions HqOptions()
    {
        var s = Store.Settings;
        return new ScrcpyOptions { MaxSize = s.HqMaxSize, BitRate = s.HqBitRate, MaxFps = s.HqFps, PowerOn = s.PowerOnConnect };
    }

    void EnsureThumb(PhoneDevice d)
    {
        using var _op = StallWatch.Op("mở luồng lưới");
        if (stopping || d.Thumb != null || d.ActiveAddr == null || d.Pending) return;
        d.RestartCts?.Cancel();
        var sess = new ScrcpySession(d.ActiveAddr, ThumbOptions(d));
        d.Thumb = sess;
        d.Status = "connecting";
        d.Error = "";
        Changed(d);
        if (d.ThumbDecoder == null)
        {
            d.ThumbDecoder = new H264Decoder(threads: 1);
            d.ThumbDecoder.NeedKeyFrame += () => d.Thumb?.RequestKeyFrame();
        }
        var dec = d.ThumbDecoder;
        dec.Resume();
        // ô ngoài vùng nhìn / đang tạm dừng toàn bộ: không giải mã (giữ cấu hình, chờ keyframe khi xem lại)
        sess.Packet += (isConfig, isKey, data) =>
        {
            if (isConfig || (d.Visible && PhoneDevice.Live)) dec.Feed(isConfig, isKey, data);
            else dec.Resume();
        };
        sess.SizeChanged += (w, h) => ui.BeginInvoke(() => { d.VideoW = w; d.VideoH = h; Changed(d); });
        sess.ClipboardReceived += t => ui.BeginInvoke(() => Clipboard?.Invoke(d, t));
        sess.Ended += reason => ui.BeginInvoke(() => OnThumbEnded(d, sess, reason));
        _ = Task.Run(async () =>
        {
            try
            {
                await startLimiter.WaitAsync(cts.Token);
                try { if (!sess.Closed) await sess.StartAsync(cts.Token); }
                finally { startLimiter.Release(); }
                await ui.InvokeAsync(() =>
                {
                    if (d.Thumb != sess) return;
                    d.Status = "online"; d.Error = ""; d.RestartDelayMs = 1000;
                    d.StartFails = 0; d.LastStartError = "";
                    if (!sess.Options.Video) { d.VideoW = d.VideoW == 0 ? sess.Width : d.VideoW; }
                    StartWatch(d);
                    Changed(d);
                });
                // mặc định tắt màn hình thật của điện thoại (vẫn truyền hình về máy tính); thoát app màn hình tự bật lại
                if (Store.Settings.ScreenOffOnConnect) { await Task.Delay(600); sess.Send(ControlMessages.DisplayPower(false)); }
            }
            catch (OperationCanceledException) { sess.Dispose(); }
            catch (Exception e)
            {
                ScrcpySession.ForgetPushed(sess.Addr); // lần thử lại sẽ kiểm tra/push lại server
                await ui.InvokeAsync(() =>
                {
                    if (d.Thumb != sess) return;
                    if (EncoderMissing(sess, e.Message)) { d.NoSwEncoder = true; d.RestartDelayMs = 300; return; } // thử lại ngay với bộ mã hoá mặc định
                    d.Error = e.Message;
                    d.StartFails++;
                    d.LastStartError = e.Message;
                    // lỗi lặp lại (vd. server không chạy được trên ROM này): giữ báo lỗi trên ô, thử lại thưa (1 phút/lần)
                    if (d.StartFails >= 3) d.RestartDelayMs = 60000;
                    Changed(d);
                });
                WriteStartLog(d, sess, e.Message);
                sess.Dispose();
            }
        });
    }

    /// <summary>
    /// Ghi nhật ký khi không khởi động được scrcpy-server: data/logs/scrcpy-&lt;serial&gt;.log (dòng lệnh + đầu ra của server).
    /// Người dùng gửi kèm tệp này khi báo lỗi.
    /// </summary>
    static void WriteStartLog(PhoneDevice d, ScrcpySession sess, string error)
    {
        try
        {
            var dir = Path.Combine(Store.DataDir, "logs");
            Directory.CreateDirectory(dir);
            var name = string.Concat(sess.Addr.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
            var file = Path.Combine(dir, $"scrcpy-{name}.log");
            if (File.Exists(file) && new FileInfo(file).Length > 256 * 1024) File.Delete(file); // giữ tệp nhỏ
            File.AppendAllText(file,
                $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · ControlPhone {typeof(DeviceManager).Assembly.GetName().Version?.ToString(3)} · " +
                $"{d.Manufacturer} {d.Model} · Android {d.Android} (SDK {d.Sdk}) · lần lỗi liên tiếp: {d.StartFails}\n" +
                $"Lỗi: {error}\n{sess.LogText}\n\n");
        }
        catch { /* nhật ký không bắt buộc */ }
    }

    // ---------- tăng tốc máy đang trỏ chuột ----------
    const int BoostFps = 30, MaxBoosts = 2;

    /// <summary>
    /// Máy đang được trỏ chuột / điều khiển: mở thêm 1 luồng 30 khung/giây (lưới vẫn chạy thưa cho nhẹ — giống Xiaowei:
    /// cả lưới chỉ 2 khung/giây, máy đang thao tác thì mượt). Bật: đổi sang luồng mới chỉ khi nó đã có hình (không chớp đen).
    /// Tắt: trễ vài giây (rê chuột qua lại không phải mở luồng liên tục).
    /// </summary>
    public void SetBoost(PhoneDevice d, bool on)
    {
        using var _op = StallWatch.Op("bật/tắt tăng tốc");
        if (!on)
        {
            if (d.Boost == null) return;
            d.BoostOffCts?.Cancel();
            d.BoostOffCts = new CancellationTokenSource();
            var tok = d.BoostOffCts.Token;
            _ = Task.Delay(4000, tok).ContinueWith(t => { if (!t.IsCanceled) ui.BeginInvoke(() => ClearBoost(d)); }, TaskScheduler.Default);
            return;
        }
        d.BoostOffCts?.Cancel();
        if (d.Boost != null || stopping || d.Pending || d.ActiveAddr == null || !d.IsOnline || !ThumbWantsVideo(d)) return;
        if (Store.Settings.ThumbFps >= 20) return; // lưới đã mượt sẵn
        foreach (var o in byId.Values.Where(x => x.Boost != null).OrderBy(x => x.BoostSince).Skip(MaxBoosts - 1).ToList()) ClearBoost(o);
        var s = Store.Settings;
        var sess = new ScrcpySession(d.ActiveAddr, new ScrcpyOptions
        {
            MaxSize = s.ThumbMaxSize, BitRate = Math.Max(s.ThumbBitRate, 1_000_000), MaxFps = BoostFps, PowerOn = false, StayAwake = false, Video = true,
            Encoder = GridEncoder(d),
        });
        d.Boost = sess;
        d.BoostSince = Environment.TickCount64;
        d.BoostDecoder ??= new H264Decoder(threads: 1);
        var dec = d.BoostDecoder;
        dec.Resume();
        long frames0 = dec.Output.Frames;
        bool ready = false;
        sess.Packet += (isConfig, isKey, data) =>
        {
            if (d.Boost != sess) return;
            dec.Feed(isConfig, isKey, data);
            if (!ready && dec.Output.Frames > frames0)
            {
                ready = true;
                ui.BeginInvoke(() => { if (d.Boost == sess) { d.BoostReady = true; dec.Output.MarkDirty(); } });
            }
        };
        sess.Ended += reason => ui.BeginInvoke(() => { if (EncoderMissing(sess, reason)) d.NoSwEncoder = true; if (d.Boost == sess) ClearBoost(d); });
        _ = Task.Run(async () =>
        {
            try { await sess.StartAsync(cts.Token); }
            catch (Exception e)
            {
                bool missing = EncoderMissing(sess, e.Message);
                _ = ui.BeginInvoke(() => { if (missing) d.NoSwEncoder = true; if (d.Boost == sess) ClearBoost(d); });
                sess.Dispose();
            }
        });
    }

    void ClearBoost(PhoneDevice d)
    {
        using var _op = StallWatch.Op("đóng luồng tăng tốc");
        var b = d.Boost;
        if (b == null) return;
        d.Boost = null;
        d.BoostReady = false;
        d.BoostOffCts?.Cancel();
        d.ThumbDecoder?.Output.MarkDirty(); // ô quay về luồng lưới: vẽ ngay khung gần nhất
        b.Dispose();
    }

    void OnThumbEnded(PhoneDevice d, ScrcpySession sess, string reason)
    {
        using var _op = StallWatch.Op("luồng lưới đóng");
        // server kết nối được rồi mới báo "Video encoder ... not found" và thoát → máy không có bộ mã hoá phần mềm: dùng mặc định
        if (d.Thumb == sess && EncoderMissing(sess, reason)) { d.NoSwEncoder = true; d.RestartDelayMs = 300; }
        if (d.Thumb == sess)
        {
            d.Thumb = null;
            if (!stopping && d.ActiveAddr != null && serialToId.ContainsKey(d.ActiveAddr))
            {
                d.Status = "connecting";
                if (d.Error == "") d.Error = "Đang kết nối lại (" + reason + ")";
                var delay = d.RestartDelayMs;
                d.RestartDelayMs = Math.Min(d.RestartDelayMs * 2, 15000);
                d.RestartCts = new CancellationTokenSource();
                var tok = d.RestartCts.Token;
                _ = Task.Delay(delay, tok).ContinueWith(t => { if (!t.IsCanceled) ui.BeginInvoke(() => EnsureThumb(d)); }, TaskScheduler.Default);
            }
            Changed(d);
        }
    }

    void EnsureHq(PhoneDevice d)
    {
        if (stopping || d.Hq != null || d.ActiveAddr == null || d.Pending) return;
        var sess = new ScrcpySession(d.ActiveAddr, HqOptions());
        d.Hq = sess;
        if (d.HqDecoder == null)
        {
            d.HqDecoder = new H264Decoder(threads: 2);
            d.HqDecoder.NeedKeyFrame += () => d.Hq?.RequestKeyFrame();
        }
        var dec = d.HqDecoder;
        dec.Resume();
        sess.Packet += dec.Feed;
        sess.ClipboardReceived += t => ui.BeginInvoke(() => Clipboard?.Invoke(d, t));
        sess.Ended += reason => ui.BeginInvoke(() =>
        {
            if (d.Hq != sess) return;
            d.Hq = null;
            if (d.HqRefs > 0 && d.HqFails < 3 && d.ActiveAddr != null && serialToId.ContainsKey(d.ActiveAddr))
                _ = Task.Delay(1500).ContinueWith(_ => ui.BeginInvoke(() => { if (d.HqRefs > 0) EnsureHq(d); }));
        });
        _ = Task.Run(async () =>
        {
            try { await sess.StartAsync(cts.Token); _ = ui.BeginInvoke(() => d.HqFails = 0); }
            catch (Exception e)
            {
                _ = ui.BeginInvoke(() => { d.HqFails++; Log?.Invoke($"Không mở được luồng nét ({LabelOf(d)}): {e.Message}", true); });
                ScrcpySession.ForgetPushed(sess.Addr);
                sess.Dispose();
            }
        });
    }

    /// <summary>Chạy lại riêng luồng lưới nếu chế độ hình (có/không) không còn đúng.</summary>
    void SyncThumbMode(PhoneDevice d)
    {
        if (d.Pending || d.ActiveAddr == null) return;
        if (!ThumbWantsVideo(d)) ClearBoost(d);
        var t = d.Thumb;
        if (t != null && t.Options.Video == ThumbWantsVideo(d)) return;
        d.RestartCts?.Cancel();
        d.Thumb = null; // gỡ trước khi dừng để trình xử lý "đóng phiên" không tự kết nối lại
        t?.Dispose();
        d.HasFrame = false;
        EnsureThumb(d);
        Changed(d);
    }

    void StopSessions(PhoneDevice d)
    {
        ClearBoost(d);
        d.RestartCts?.Cancel();
        var t = d.Thumb; var h = d.Hq;
        d.Thumb = null; d.Hq = null;
        t?.Dispose(); h?.Dispose();
    }

    public void RestartSessions(PhoneDevice d)
    {
        StopSessions(d);
        d.RestartDelayMs = 1000;
        d.StartFails = 0;
        d.HasFrame = false;
        EnsureThumb(d);
        if (d.HqRefs > 0) EnsureHq(d);
    }

    public void RestartAll() { foreach (var d in byId.Values.Where(x => !x.Pending && x.ActiveAddr != null).ToList()) RestartSessions(d); }

    public void AcquireHq(PhoneDevice d)
    {
        ClearBoost(d); // màn hình lớn có luồng nét riêng
        d.HqRefs++;
        d.HqFails = 0;
        EnsureHq(d);
        if (d.HqRefs == 1) SyncThumbMode(d); // luồng lưới chuyển sang chỉ điều khiển
        Changed(d);
    }

    public void ReleaseHq(PhoneDevice d)
    {
        d.HqRefs = Math.Max(0, d.HqRefs - 1);
        if (d.HqRefs == 0 && d.Hq != null) { var h = d.Hq; d.Hq = null; h.Dispose(); }
        if (d.HqRefs == 0) SyncThumbMode(d); // đóng màn hình lớn → ô lưới có hình lại
        Changed(d);
    }

    public void SetPaused(IEnumerable<string> ids, bool paused, out int changed)
    {
        changed = 0;
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var d) || d.Pending) continue;
            var m = Store.Meta(id);
            if (m.Paused == paused) continue;
            m.Paused = paused;
            changed++;
            if (d.ActiveAddr != null) SyncThumbMode(d);
            Changed(d);
        }
        Store.Save();
    }

    /// <summary>Chế độ chỉ hiển thị: ids = danh sách máy hiển thị; null = bình thường.</summary>
    public void SetSolo(IReadOnlyCollection<string>? ids)
    {
        Store.State.Solo = ids is { Count: > 0 } ? ids.Distinct().ToList() : null;
        Store.Save();
        foreach (var d in byId.Values.Where(x => !x.Pending && x.ActiveAddr != null).ToList()) SyncThumbMode(d);
        foreach (var d in byId.Values) d.Changed();
        ListChanged?.Invoke();
    }

    public void RemoveOffline()
    {
        foreach (var d in byId.Values.Where(x => x.ActiveAddr == null && !x.Pending).ToList()) Remove(d);
    }

    // ---------- theo dõi "lỡ keyframe" (đóng phóng to, cuộn tới, bỏ tạm dừng) ----------
    internal void StartWatch(PhoneDevice d)
    {
        d.Watching = true; d.WatchSince = Environment.TickCount64; d.WatchFrames = d.Frames?.Frames ?? 0; d.WatchKf = false; d.WatchResets = 0;
    }

    /// <summary>Gọi mỗi giây: ô đang xem mà không vẽ được khung nào → xin keyframe; vẫn không → dựng lại bộ giải mã.</summary>
    public void WatchTick()
    {
        long now = Environment.TickCount64;
        foreach (var d in byId.Values)
        {
            if (!d.Watching) continue;
            if (!d.Visible || !PhoneDevice.Live || d.Zoomed || d.Paused || d.Pending || d.Thumb is not { Options.Video: true }) { d.Watching = false; continue; }
            if (!d.IsOnline) { d.WatchSince = now; d.WatchKf = false; continue; }
            if ((d.Frames?.Frames ?? 0) > d.WatchFrames) { d.Watching = false; continue; }
            var age = now - d.WatchSince;
            if (age > 6000)
            {
                if (d.WatchResets >= 3) { d.Watching = false; continue; }
                d.ThumbDecoder?.Resume();
                d.Thumb?.RequestKeyFrame();
                d.WatchSince = now; d.WatchKf = true; d.WatchResets++;
            }
            else if (age > 2500 && !d.WatchKf) { d.Thumb?.RequestKeyFrame(); d.WatchKf = true; }
        }
    }

    /// <summary>Ô vào/ra vùng nhìn thấy (chỉ giải mã ô đang thấy).</summary>
    public void SetVisible(PhoneDevice d, bool visible)
    {
        if (d.Visible == visible) return;
        d.Visible = visible;
        if (visible) { d.ThumbDecoder?.Resume(); d.Thumb?.RequestKeyFrame(); StartWatch(d); }
    }

    public void SetLive(bool live)
    {
        PhoneDevice.Live = live;
        foreach (var d in byId.Values)
        {
            if (live && d.Visible) { d.ThumbDecoder?.Resume(); d.Thumb?.RequestKeyFrame(); StartWatch(d); }
            d.Changed();
        }
    }

    // ---------- thông tin pin / IP / màn hình ----------
    void RefreshAllInfo() { foreach (var d in byId.Values.Where(x => x.ActiveAddr != null && !x.Pending).ToList()) RefreshInfo(d); }

    public void RefreshInfo(PhoneDevice d)
    {
        if (Interlocked.Exchange(ref d.InfoBusy, 1) == 1) return;
        var addr = d.ActiveAddr;
        if (addr == null) { d.InfoBusy = 0; return; }
        _ = Task.Run(async () =>
        {
            try
            {
                await infoLimiter.WaitAsync(cts.Token);
                string o;
                try
                {
                    o = await AdbClient.ShellAsync(addr, "dumpsys battery | grep -E '^  (level|AC powered|USB powered|Wireless powered|temperature):'; ip -f inet addr show wlan0 2>/dev/null | grep -m1 inet; dumpsys power | grep -m1 -E 'mWakefulness='", 10000, cts.Token);
                }
                finally { infoLimiter.Release(); }
                string? M(string re) { var m = Regex.Match(o, re); return m.Success ? m.Groups[1].Value : null; }
                var level = M(@"level:\s*(\d+)"); var temp = M(@"temperature:\s*(-?\d+)"); var wake = M(@"mWakefulness=(\w+)");
                await ui.InvokeAsync(() =>
                {
                    using var _op = StallWatch.Op("cập nhật pin / IP");
                    d.Battery = level != null ? int.Parse(level) : null;
                    d.Temp = temp != null && int.Parse(temp) > 0 ? int.Parse(temp) / 10.0 : null;
                    d.Charging = Regex.IsMatch(o, @"(AC|USB|Wireless) powered:\s*true");
                    d.Ip = M(@"inet\s+(\d+\.\d+\.\d+\.\d+)") ?? "";
                    d.ScreenOn = wake != null ? wake == "Awake" : null;
                    Changed(d);
                });
            }
            catch { }
            finally { d.InfoBusy = 0; }
        });
    }

    // ---------- WiFi ----------
    async Task AutoReconnectWifiAsync()
    {
        if (!Store.Settings.AutoReconnectWifi || stopping) return;
        var targets = new List<string>();
        long now = Environment.TickCount64;
        foreach (var (id, meta) in Store.State.Devices)
        {
            if (string.IsNullOrEmpty(meta.Wifi) || serialStates.ContainsKey(meta.Wifi)) continue;
            // chỉ tự nối lại khi máy đang không có kết nối nào (tránh làm phiền khi đã có USB)
            if (byId.TryGetValue(id, out var d) && d.ActiveAddr != null) continue;
            // địa chỉ cứ thất bại (máy đã tắt / đổi IP): thử thưa dần tới 10 phút/lần thay vì mỗi 20 giây
            if (wifiBackoff.TryGetValue(meta.Wifi, out var b) && now < b.NextAt) continue;
            targets.Add(meta.Wifi);
        }
        using var lim = new SemaphoreSlim(8);
        await Task.WhenAll(targets.Select(async t =>
        {
            await lim.WaitAsync();
            try
            {
                var r = await AdbClient.RunAsync(["connect", t], 6000);
                bool ok = r.Out.Contains("connected to", StringComparison.OrdinalIgnoreCase) && !r.Out.Contains("unable", StringComparison.OrdinalIgnoreCase);
                await ui.InvokeAsync(() =>
                {
                    if (ok) { wifiBackoff.Remove(t); return; }
                    int fails = wifiBackoff.TryGetValue(t, out var old) ? old.Fails + 1 : 1;
                    wifiBackoff[t] = (fails, Environment.TickCount64 + Math.Min(20_000L << Math.Min(fails, 5), 600_000));
                });
            }
            finally { lim.Release(); }
        }));
    }

    readonly Dictionary<string, (int Fails, long NextAt)> wifiBackoff = [];

    // ---------- chẩn đoán ----------
    public sealed record DiagRow(string Addr, string State, string Tid, string Tile, bool Ok, string Note);
    public (int AdbConnections, int DeviceConnections, int Tiles, int DupSerials, List<DiagRow> Rows) Diagnostics()
    {
        var rows = new List<DiagRow>();
        foreach (var x in lastTrack)
        {
            serialToId.TryGetValue(x.Addr, out var id);
            var d = id != null ? byId.GetValueOrDefault(id) : byId.GetValueOrDefault("pending:" + x.Addr);
            var ok = x.State == "device" && d is { IsOnline: true };
            var note = probing.Contains(x.Addr) ? "Đang đọc thông tin…" : probeErrors.GetValueOrDefault(x.Addr) ?? (d != null ? (d.IsOnline ? d.Status : d.Error != "" ? d.Error : d.Status) : "Chưa có ô");
            rows.Add(new DiagRow(x.Addr, x.State, x.TransportId, d is { Pending: false } ? "#" + d.Num.ToString("00") : "—", ok, note));
        }
        int dup = lastTrack.Count - lastTrack.Select(x => x.Serial).Distinct().Count();
        return (rows.Count, rows.Count(r => r.State == "device"), byId.Count, dup, rows);
    }

    public void ResetStartLimiter() => startLimiter = new SemaphoreSlim(Math.Max(1, Store.Settings.MaxParallelStart));

    public void Dispose()
    {
        stopping = true;
        infoTimer?.Stop(); wifiTimer?.Stop();
        cts.Cancel();
        foreach (var d in byId.Values) StopSessions(d);
    }
}
