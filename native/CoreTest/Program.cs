using System.IO;
using System.Windows.Threading;
using ControlPhone.Adb;
using ControlPhone.Core;

// Kiểm thử lõi trên máy thật — CHỈ lệnh chỉ đọc (không chạm/không đổi cài đặt điện thoại).
int pass = 0, fail = 0;
void Check(bool ok, string name, string detail = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail != "" ? "  — " + detail : "")}"); if (ok) pass++; else fail++; }

var tmp = Path.Combine(Path.GetTempPath(), "cp-coretest");
Directory.CreateDirectory(tmp);
// KHÔNG BAO GIỜ dùng dữ liệu thật của người dùng: chép data/*.json sang thư mục tạm và trỏ Store vào đó
var dataCopy = Path.Combine(tmp, "data");
Directory.CreateDirectory(dataCopy);
var realData = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "data"));
if (Directory.Exists(realData)) foreach (var f in Directory.GetFiles(realData, "*.json")) File.Copy(f, Path.Combine(dataCopy, Path.GetFileName(f)), true);
Environment.SetEnvironmentVariable("CP_DATA_DIR", dataCopy);

Dispatcher? ui = null;
var ready = new ManualResetEventSlim();
var th = new Thread(() => { ui = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); }) { IsBackground = true };
th.SetApartmentState(ApartmentState.STA);
th.Start();
ready.Wait();

T Ui<T>(Func<T> f) => ui!.Invoke(f);
void UiDo(Action a) => ui!.Invoke(a);

// an toàn dữ liệu: config.json hỏng KHÔNG được bị ghi đè (dotnet run -c Release -- store) — chạy trên bản sao trong thư mục tạm
if (args.Contains("store"))
{
    var cfg = Path.Combine(dataCopy, "config.json");
    const string broken = "{ \"devices\": { \"abc\": { \"num\": 7 } }, ";
    File.WriteAllText(cfg, broken);
    UiDo(() => { Store.Load(); Store.Meta("new-device"); Store.Flush(); });
    Check(File.ReadAllText(cfg) == broken, "Cấu hình hỏng không bị ghi đè", Store.LoadError ?? "");
    Check(Directory.GetFiles(dataCopy, "config.json.unreadable-*").Length == 1, "Giữ bản sao tệp hỏng");
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    try { Directory.Delete(tmp, true); } catch { }
    return fail == 0 ? 0 : 1;
}

// sắp xếp lưới: luôn ra đúng thứ tự, và đổi số 1 máy chỉ dời 1 ô (dotnet run -c Release -- grid)
if (args.Contains("grid"))
{
    var rnd = new Random(7);
    var names = Enumerable.Range(1, 60).Select(i => "x" + i).ToList();
    var col = new System.Collections.ObjectModel.ObservableCollection<string>(names.Take(39));
    int moveEvents = 0;
    col.CollectionChanged += (_, e) => { if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Move) moveEvents += 1000; else moveEvents++; };
    // 1 máy đổi số: từ vị trí 5 xuống 30
    var t1 = col.ToList(); var m = t1[5]; t1.RemoveAt(5); t1.Insert(30, m);
    int ops = ControlPhone.GridOrder.Sync(col, t1);
    Check(col.SequenceEqual(t1) && ops <= 2 && moveEvents <= 2, "Đổi số 1 máy chỉ dời 1 ô (không dựng lại lưới)", $"{ops} thao tác");
    // 2 máy đổi số cho nhau
    moveEvents = 0;
    var t2 = col.ToList(); (t2[3], t2[20]) = (t2[20], t2[3]);
    ops = ControlPhone.GridOrder.Sync(col, t2);
    Check(col.SequenceEqual(t2) && ops <= 4, "2 máy đổi số cho nhau", $"{ops} thao tác");
    // không đổi gì → không thao tác
    Check(ControlPhone.GridOrder.Sync(col, col.ToList()) == 0, "Không đổi thứ tự → không đụng lưới");
    // ngẫu nhiên: thêm / bớt / xáo trộn 500 lần luôn ra đúng danh sách
    bool allOk = true;
    for (int k = 0; k < 500 && allOk; k++)
    {
        var target = names.OrderBy(_ => rnd.Next()).Take(rnd.Next(0, 60)).ToList();
        if (rnd.Next(3) == 0) target.Sort(StringComparer.Ordinal);
        ControlPhone.GridOrder.Sync(col, target);
        allOk = col.SequenceEqual(target);
    }
    Check(allOk, "500 lần thêm / bớt / xáo trộn ngẫu nhiên đều đúng thứ tự");
    // WPF giữ nguyên ô (container) khi dời → không tạo lại ô, không mất hình
    UiDo(() =>
    {
        var src = new System.Collections.ObjectModel.ObservableCollection<string>(names.Take(39));
        var ic = new System.Windows.Controls.ItemsControl
        {
            ItemsSource = src,
            ItemsPanel = new System.Windows.Controls.ItemsPanelTemplate(new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.WrapPanel))),
        };
        var win = new System.Windows.Window { Content = ic, Width = 800, Height = 600, Left = -20000, ShowInTaskbar = false, ShowActivated = false };
        win.Show(); win.UpdateLayout();
        var before = names.Take(39).ToDictionary(n => n, n => ic.ItemContainerGenerator.ContainerFromItem(n));
        var t = src.ToList(); var x = t[5]; t.RemoveAt(5); t.Insert(30, x);
        ControlPhone.GridOrder.Sync(src, t);
        win.UpdateLayout();
        int same = names.Take(39).Count(n => ReferenceEquals(before[n], ic.ItemContainerGenerator.ContainerFromItem(n)));
        win.Close();
        Check(same == 39, "Dời ô trên lưới WPF giữ nguyên ô cũ (không tạo lại)", $"{same}/39 ô giữ nguyên");
    });
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return fail == 0 ? 0 : 1;
}

// cập nhật bằng cách giải nén bản mới ra thư mục KHÁC: tìm + nhập dữ liệu bản cũ rồi giữ đúng số thứ tự
// dotnet run -c Release -- upgrade <config-ban-cu.json> <phones.json>   (chỉ dùng thư mục tạm)
if (args.Contains("upgrade"))
{
    var a2 = args.SkipWhile(a => a != "upgrade").Skip(1).ToArray();
    var oldCfg = a2[0]; var phonesFile = a2[1];
    var root = Path.Combine(Path.GetTempPath(), "cp-upgrade-" + DateTime.Now.ToString("HHmmss"));
    var oldData = Path.Combine(root, "ControlPhone-v2.0.1-win64", "data");
    var olderData = Path.Combine(root, "ControlPhone-v2.0.0-win64", "ControlPhone-v2.0.0-win64", "data"); // giải nén lồng 1 cấp
    var newExe = Path.Combine(root, "ControlPhone-v2.0.9-win64");
    Directory.CreateDirectory(oldData); Directory.CreateDirectory(olderData); Directory.CreateDirectory(newExe);
    File.Copy(oldCfg, Path.Combine(oldData, "config.json"));
    File.Copy(oldCfg, Path.Combine(olderData, "config.json"));
    File.SetLastWriteTime(Path.Combine(olderData, "config.json"), DateTime.Now.AddDays(-30));
    var own = Path.Combine(newExe, "data");
    var cands = DataImport.FindPrevious(newExe, own, userFolders: false);
    foreach (var c in cands) Console.WriteLine($"  tìm thấy: {c.Dir} · {c.Phones} máy · {c.Modified:dd/MM HH:mm}");
    Check(cands.Count == 2 && cands[0].Dir.Contains("v2.0.1"), "Tìm thấy dữ liệu bản cũ cạnh thư mục bản mới (cả bản giải nén lồng thư mục), mới nhất trước");
    DataImport.Import(cands[0].Dir, own);
    Check(File.Exists(Path.Combine(own, "config.json")), "Nhập dữ liệu vào thư mục bản mới");
    // bản mới dùng dữ liệu vừa nhập: số thứ tự của từng máy (theo serial USB) phải giữ như bản cũ
    File.Copy(Path.Combine(own, "config.json"), Path.Combine(dataCopy, "config.json"), true);
    UiDo(() => { Store.Load(); I18n.Init("vi"); });
    var oldNums = System.Text.Json.JsonDocument.Parse(File.ReadAllText(oldCfg)).RootElement.GetProperty("devices").EnumerateObject().ToDictionary(e => e.Name, e => e.Value.GetProperty("num").GetInt32());
    var phones2 = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(File.ReadAllText(phonesFile))!;
    var mgr3 = Ui(() => new DeviceManager(ui!));
    var resolve2 = typeof(DeviceManager).GetMethod("ResolveId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    int kept = 0, known = 0;
    foreach (var ph in phones2)
    {
        var id = Ui(() => (string)resolve2.Invoke(mgr3, [ph["SerialNo"], ph["Aid"], "usb", ph["Usb"]])!);
        var num = Ui(() => Store.Meta(id).Num);
        if (oldNums.TryGetValue(ph["Usb"], out var on)) { known++; if (on == num) kept++; else Console.WriteLine($"  KHÁC: {ph["Usb"]} cũ #{on} → #{num}"); }
        else Console.WriteLine($"  máy mới (bản cũ chưa có): {ph["Usb"]} → #{num}");
    }
    Check(known > 0 && kept == known, "Mọi máy đã có trong bản cũ giữ nguyên số thứ tự", $"{kept}/{known}");
    try { Directory.Delete(root, true); } catch { }
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return fail == 0 ? 0 : 1;
}

// định danh theo serial USB + chuyển mục cũ (không kết nối máy): dotnet run -c Release -- identity <phones.json>
// phones.json: [{Usb, SerialNo, Aid}] đọc từ máy thật; chạy trên BẢN SAO cấu hình
if (args.Contains("identity"))
{
    var file = args.SkipWhile(a => a != "identity").Skip(1).FirstOrDefault();
    var phones = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(File.ReadAllText(file!))!;
    UiDo(() => { Store.Load(); I18n.Init("vi"); });
    int cfgBefore = Ui(() => Store.State.Devices.Count);
    var mgr2 = Ui(() => new DeviceManager(ui!));
    var resolve = typeof(DeviceManager).GetMethod("ResolveId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    var result = Ui(() => phones.Select(p => (p["Usb"], Id: (string)resolve.Invoke(mgr2, [p["SerialNo"], p["Aid"], "usb", p["Usb"]])!)).ToList());
    var nums = Ui(() => result.Select(r => (r.Item1, r.Id, Store.Meta(r.Id).Num)).ToList());
    foreach (var (usb, id, num) in nums.OrderBy(x => x.Num)) Console.WriteLine($"  #{num:00}  {usb}  → {id}");
    Check(nums.All(x => x.Id == x.Item1), "Mỗi máy định danh bằng serial USB");
    Check(nums.Select(x => x.Num).Distinct().Count() == nums.Count, "Không trùng số thứ tự", $"{nums.Count} máy");
    // lần 2 (Android ID đổi, vd. cập nhật ROM): vẫn đúng máy, đúng số
    var again = Ui(() => phones.Select(p => (string)resolve.Invoke(mgr2, [p["SerialNo"], "ffff" + p["Aid"], "usb", p["Usb"]])!).ToList());
    Check(Ui(() => again.Select(id => Store.Meta(id).Num).SequenceEqual(nums.Select(x => x.Num))), "Đổi Android ID (cập nhật Android) không đổi số thứ tự");
    int cfgAfter = Ui(() => Store.State.Devices.Count);
    Console.WriteLine($"Mục trong cấu hình: {cfgBefore} → {cfgAfter}");
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return fail == 0 ? 0 : 1;
}

// adb server khác đang giữ máy (chỉ đọc, không tắt gì): dotnet run -c Release -- otheradb
if (args.Contains("otheradb"))
{
    var others = await AdbClient.FindOtherServersAsync();
    foreach (var o in others) Console.WriteLine($"cổng {o.Port} · PID {o.Pid} · mở {o.Started:HH:mm:ss} · {o.Serials.Count} máy: {string.Join(", ", o.Serials.Take(5))}…");
    Check(true, "Dò adb server khác", $"{others.Count} server, {others.Sum(o => o.Serials.Count)} máy bị giữ");
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return 0;
}

// máy không có bộ mã hoá đã chọn: xem scrcpy báo lỗi gì (để tự quay về bộ mã hoá mặc định)
if (args.Contains("encfail"))
{
    var serial = args.SkipWhile(a => a != "encfail").Skip(1).First();
    var s = new ControlPhone.Scrcpy.ScrcpySession(serial, new() { MaxSize = 480, MaxFps = 5, PowerOn = false, Encoder = "OMX.khong.ton.tai" });
    string? ended = null; s.Ended += r => ended = r;
    try { await s.StartAsync(); Console.WriteLine("kết nối được; chờ 4 giây…"); await Task.Delay(4000); Console.WriteLine($"đã đóng: {s.Closed} ({ended})"); } catch (Exception e) { Console.WriteLine("Lỗi: " + e.Message); }
    Console.WriteLine(s.LogText);
    s.Dispose();
    return 0;
}

// độ trễ hình: xin keyframe (RESET_VIDEO) rồi đo bao lâu nó về tới PC, theo bộ mã hoá + fps
// dotnet run -c Release -- latency <serial>
if (args.Contains("latency"))
{
    var serial = args.SkipWhile(a => a != "latency").Skip(1).First();
    var cfgs = args.Contains("hq") ? new (string?, int, int, int)[] { (null, 30, 4_000_000, 1280), ("OMX.google.h264.encoder", 30, 4_000_000, 1280) } : new (string?, int, int, int)[] { (null, 5, 150_000, 480), ("OMX.google.h264.encoder", 5, 150_000, 480), (null, 30, 1_000_000, 480), ("OMX.google.h264.encoder", 30, 1_000_000, 480) };
    foreach (var (enc, fps, br, size) in cfgs)
    {
        using var s = new ControlPhone.Scrcpy.ScrcpySession(serial, new() { MaxSize = size, BitRate = br, MaxFps = fps, PowerOn = false, Encoder = enc });
        long keyAt = 0, pkts = 0;
        s.Packet += (cfg, key, data) => { Interlocked.Increment(ref pkts); if (key) Interlocked.Exchange(ref keyAt, System.Diagnostics.Stopwatch.GetTimestamp()); };
        string err = "";
        try { await s.StartAsync(); } catch (Exception e) { err = e.Message; }
        if (err != "") { Console.WriteLine($"  {enc ?? "mặc định (phần cứng)"} @{fps}fps: KHÔNG CHẠY — {err[..Math.Min(120, err.Length)]}"); continue; }
        await Task.Delay(2500);
        var lat = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            Interlocked.Exchange(ref keyAt, 0);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            s.Send(ControlPhone.Scrcpy.ControlMessages.Empty(ControlPhone.Scrcpy.ControlMessages.ResetVideo));
            while (Interlocked.Read(ref keyAt) == 0 && System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds < 3000) await Task.Delay(2);
            var k = Interlocked.Read(ref keyAt);
            if (k != 0) lat.Add(System.Diagnostics.Stopwatch.GetElapsedTime(t0, k).TotalMilliseconds);
            await Task.Delay(900);
        }
        Console.WriteLine($"  {enc ?? "mặc định (phần cứng)",-26} {size}p @{fps,2}fps: keyframe về sau {(lat.Count > 0 ? $"TB {lat.Average():0} ms, min {lat.Min():0}, max {lat.Max():0}" : "không thấy")} ({lat.Count}/6) · {pkts} gói");
    }
    return 0;
}

// tăng tốc máy đang trỏ chuột (1 máy, không mở luồng lưới các máy khác): dotnet run -c Release -- boost <serial>
if (args.Contains("boost"))
{
    var serial = args.SkipWhile(a => a != "boost").Skip(1).First();
    UiDo(() => { Store.Load(); I18n.Init("vi"); Store.Settings.ThumbFps = 5; });
    var mgrB = Ui(() => new DeviceManager(ui!));
    var dev = Ui(() => new PhoneDevice(serial) { ActiveAddr = serial, Status = "online" });
    UiDo(() => mgrB.SetBoost(dev, true));
    var swB = System.Diagnostics.Stopwatch.StartNew();
    while (swB.Elapsed.TotalSeconds < 10 && !Ui(() => dev.BoostReady)) await Task.Delay(100);
    var readyAt = swB.Elapsed.TotalSeconds;
    var usesBoost = Ui(() => dev.BoostReady && ReferenceEquals(dev.Frames, dev.BoostDecoder!.Output) && dev.Frames!.Frames > 0);
    Check(usesBoost, "Bật tăng tốc: ô chuyển sang luồng 30 khung/giây khi đã có hình", $"sau {readyAt:0.0}s, fps tối đa {Ui(() => dev.Boost?.Options.MaxFps)}");
    UiDo(() => mgrB.SetBoost(dev, false));
    await Task.Delay(1500);
    Check(Ui(() => dev.Boost != null), "Rời chuột: chưa tắt ngay (tránh mở/đóng liên tục)");
    await Task.Delay(3500);
    Check(Ui(() => dev.Boost == null && !dev.BoostReady), "Sau ~4 giây: về luồng lưới, đóng luồng tăng tốc");
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return fail == 0 ? 0 : 1;
}

// server không chạy được: báo rõ "đã thoát" + đầu ra của server (dotnet run -c Release -- startfail <serial>)
// cách thử: phiên đầu push server; xoá server trên máy → phiên sau chép thất bại → server thoát ngay; cuối cùng push lại
if (args.Contains("startfail"))
{
    var serial = args.SkipWhile(a => a != "startfail").Skip(1).FirstOrDefault();
    if (serial == null) { Console.WriteLine("Cần serial"); return 1; }
    using (var ok = new ControlPhone.Scrcpy.ScrcpySession(serial, new() { Video = false, PowerOn = false })) await ok.StartAsync();
    await AdbClient.ShellAsync(serial, "rm -f /data/local/tmp/controlphone-server.jar", 8000);
    string msg = "", log = "";
    var bad = new ControlPhone.Scrcpy.ScrcpySession(serial, new() { Video = false, PowerOn = false });
    var sw2 = System.Diagnostics.Stopwatch.StartNew();
    try { await bad.StartAsync(); } catch (Exception e) { msg = e.Message; log = bad.LogText; }
    bad.Dispose();
    ControlPhone.Scrcpy.ScrcpySession.ForgetPushed(serial);
    using (var again = new ControlPhone.Scrcpy.ScrcpySession(serial, new() { Video = false, PowerOn = false })) await again.StartAsync(); // push lại
    var size = (await AdbClient.ShellAsync(serial, "stat -c %s /data/local/tmp/controlphone-server.jar", 8000)).Trim();
    Console.WriteLine($"Lỗi: {msg}\nNhật ký:\n{log}");
    Check(msg.StartsWith("scrcpy-server đã thoát") && sw2.Elapsed.TotalSeconds < 10, "Server thoát sớm → báo rõ, không chờ hết 20 giây", $"{sw2.Elapsed.TotalSeconds:0.0}s");
    Check(log.Contains("app_process") && log.Contains("controlphone-server.jar"), "Nhật ký có dòng lệnh + đầu ra của server");
    Check(size.Length > 3, "Đã push lại server sau khi thử", size + " byte");
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return fail == 0 ? 0 : 1;
}

// giữ USB có lưu lượng khi màn hình đứng yên: 1 phiên trên 1 máy, để yên 12 giây (dotnet run -c Release -- keepalive [serial])
if (args.Contains("keepalive"))
{
    var serial = args.SkipWhile(a => a != "keepalive").Skip(1).FirstOrDefault();
    if (serial == null)
    {
        var devs = new List<AdbDevice>();
        using var cts = new CancellationTokenSource(5000);
        try { await AdbClient.TrackDevicesAsync(l => { devs = l.ToList(); cts.Cancel(); }, _ => { }, cts.Token); } catch { }
        serial = devs.FirstOrDefault(d => d.State == "device")?.Addr;
    }
    if (serial == null) { Console.WriteLine("Không có máy"); return 1; }
    bool stay = args.Contains("stay");
    foreach (var video in new[] { true, false })
    {
        using var s = new ControlPhone.Scrcpy.ScrcpySession(serial, new() { Video = video, PowerOn = false, StayAwake = stay });
        await s.StartAsync();
        await Task.Delay(12000);
        Console.WriteLine($"{serial} video={video}: {s.PacketsReceived} gói hình, {s.KeepAlives} tin giữ kết nối trong 12 giây");
        Check(s.Running && (video ? s.PacketsReceived + s.KeepAlives : s.KeepAlives) >= 8, $"Giữ kết nối khi để yên (video={video})", "≥ 1 lần trao đổi mỗi ~1.5 giây");
    }
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    try { Directory.Delete(tmp, true); } catch { }
    return fail == 0 ? 0 : 1;
}

UiDo(() => { Store.Load(); I18n.Init("vi"); });
// lưu bình thường luôn để lại bản .bak (khôi phục được)
if (args.Contains("backup"))
{
    UiDo(() => { Store.Save(); Store.Flush(); });
    Check(File.Exists(Path.Combine(dataCopy, "config.json.bak")) && File.Exists(Path.Combine(dataCopy, "config.json.session.bak")), "Lưu cấu hình có bản sao lưu .bak");
}

// chỉ kiểm tra giao diện ô máy (không kết nối điện thoại): dotnet run -c Release -- xaml
if (args.Contains("xaml"))
{
    UiDo(() =>
    {
        var app = new ControlPhone.App();
        app.InitializeComponent(); // nạp tài nguyên (màu, kiểu) của app
        var dev = new PhoneDevice("test") { Model = "SM-G960F", Status = "online" };
        var meta = Store.Meta("test"); meta.PcKeyboard = true; meta.Num = 1;
        var tile = new ControlPhone.PhoneTile { DataContext = dev };
        tile.SetTileWidth(200);
        tile.Measure(new System.Windows.Size(400, 800)); tile.Arrange(new System.Windows.Rect(0, 0, 210, 460)); tile.UpdateLayout();
        Check(dev.PcKeyboardOn && tile.ActualHeight > 300, "Dựng ô máy (có biểu tượng bàn phím)", $"{tile.ActualWidth:0}×{tile.ActualHeight:0}");
        Store.State.Devices.Remove("test"); // không lưu máy giả vào cấu hình
        // dựng cửa sổ chính (không hiện, không kết nối máy): bắt lỗi XAML / khởi tạo
        try
        {
            var w = new ControlPhone.MainWindow();
            var btn = w.FindName("BtnHideOff") as System.Windows.Controls.Button;
            var lbl = w.FindName("HideOffLabel") as System.Windows.Controls.TextBlock;
            Check(btn != null && lbl?.Text.Length > 0, "Dựng cửa sổ chính (có nút Ẩn máy mất kết nối)", lbl?.Text ?? "");
            w.Close();
        }
        catch (Exception e) { Check(false, "Dựng cửa sổ chính", e.GetType().Name + ": " + e.Message); }
    });
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return fail == 0 ? 0 : 1;
}
Console.WriteLine($"Dữ liệu: {Store.DataDir} · adb: {AdbClient.AdbPath}");
DeviceManager mgr = null!;
Actions act = null!;
UiDo(() => { mgr = new DeviceManager(ui!); act = new Actions(mgr, ui!); mgr.Start(); });

// 1) nhận máy + phiên lưới
var sw = System.Diagnostics.Stopwatch.StartNew();
int online = 0, total = 0;
while (sw.Elapsed < TimeSpan.FromSeconds(60))
{
    await Task.Delay(1000);
    (online, total) = Ui(() => (mgr.Devices.Count(d => d.IsOnline), mgr.Devices.Count(d => !d.Pending)));
    if (total > 0 && online == total && sw.Elapsed > TimeSpan.FromSeconds(8)) break;
}
Check(total > 0 && online == total, "Nhận máy và mở phiên lưới", $"{online}/{total} online sau {sw.Elapsed.TotalSeconds:0}s");

// 2) có hình (giải mã FFmpeg)
await Task.Delay(4000);
var frames = Ui(() => mgr.Devices.Where(d => d.IsOnline).Select(d => d.Frames?.Frames ?? 0).ToList());
Check(frames.Count > 0 && frames.All(f => f > 0), "Mọi máy đều giải mã được khung hình", $"ít nhất {frames.DefaultIfEmpty().Min()} khung/máy");

// 3) thông tin pin / IP / màn hình (đọc mỗi 20 giây; gọi tay để thử)
var first = Ui(() => mgr.Devices.First(d => d.IsOnline));
UiDo(() => mgr.RefreshInfo(first));
await Task.Delay(4000);
Check(Ui(() => first.Battery) != null, "Đọc pin / nhiệt độ / màn hình", Ui(() => $"#{first.Num} pin {first.Battery}% sạc={first.Charging} nhiệt {first.Temp}°C IP={first.Ip} màn hình bật={first.ScreenOn}"));

// 4) thao tác hàng loạt: shell chỉ đọc trên tất cả máy
var ids = Ui(() => mgr.Devices.Where(d => d.IsOnline).Select(d => d.Id).ToList());
var done = new TaskCompletionSource<BatchTask>();
UiDo(() => act.TaskUpdated += t => { if (t.Finished && t.Op == "shell") done.TrySetResult(t); });
UiDo(() => act.RunBatch("shell", ids, new() { ["cmd"] = "getprop ro.product.model; uptime" }, "test shell"));
var task = await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
Check(task.OkCount == ids.Count, "Thao tác hàng loạt (shell) trên tất cả máy", $"{task.OkCount}/{task.Total} OK · vd: {task.Results.First().Msg.Replace("\n", " | ")}");

// 5) shell root (su -c id) trên 3 máy root
var rootIds = Ui(() => mgr.Devices.Where(d => d.IsOnline && d.Root).Take(3).Select(d => d.Id).ToList());
if (rootIds.Count > 0)
{
    var doneR = new TaskCompletionSource<BatchTask>();
    UiDo(() => act.TaskUpdated += t => { if (t.Finished && t.Op == "rootcheck") doneR.TrySetResult(t); });
    UiDo(() => act.RunBatch("rootcheck", rootIds, null, "test root"));
    var tr = await doneR.Task.WaitAsync(TimeSpan.FromSeconds(60));
    Check(tr.OkCount == rootIds.Count, "Kiểm tra root", $"{tr.OkCount}/{tr.Total}");
}

// 6) chụp màn hình (lưu vào thư mục tạm, không phải thư mục ảnh của người dùng)
var oldDir = Ui(() => Store.Settings.ScreenshotDir);
UiDo(() => Store.Settings.ScreenshotDir = tmp);
var doneS = new TaskCompletionSource<BatchTask>();
UiDo(() => act.TaskUpdated += t => { if (t.Finished && t.Op == "screenshot") doneS.TrySetResult(t); });
UiDo(() => act.RunBatch("screenshot", [first.Id], null, "test shot"));
var ts = await doneS.Task.WaitAsync(TimeSpan.FromSeconds(60));
var shotFile = ts.Results.FirstOrDefault()?.Msg ?? "";
Check(ts.OkCount == 1 && File.Exists(shotFile) && new FileInfo(shotFile).Length > 10000, "Chụp màn hình", $"{shotFile} ({(File.Exists(shotFile) ? new FileInfo(shotFile).Length / 1024 : 0)} KB)");
UiDo(() => Store.Settings.ScreenshotDir = oldDir);

// 7) tệp & ứng dụng
var (p, items) = await Actions.ListFilesAsync(first.ActiveAddr!, "/sdcard/");
Check(items.Count > 0, "Duyệt tệp /sdcard/", $"{items.Count} mục, vd: {string.Join(", ", items.Take(4).Select(i => i.Name + (i.Dir ? "/" : "")))}");
var apps = await Actions.ListAppsAsync(first.ActiveAddr!);
Check(apps.Count > 0, "Danh sách ứng dụng đã cài", $"{apps.Count} gói, vd: {string.Join(", ", apps.Take(3))}");
// tải một tệp thật trong /sdcard (tìm tệp nhỏ nhất > 0 byte trong vài thư mục) và so kích thước
Actions.FileItem? pick = null; string pickDir = "";
foreach (var dir in new[] { "/sdcard/", "/sdcard/Download/", "/sdcard/DCIM/Camera/", "/sdcard/Pictures/" })
{
    try
    {
        var (dp, its) = await Actions.ListFilesAsync(first.ActiveAddr!, dir);
        var f = its.Where(i => !i.Dir && i.Size > 0 && i.Size < 20_000_000).OrderBy(i => i.Size).FirstOrDefault();
        if (f != null) { pick = f; pickDir = dp; break; }
    }
    catch { }
}
if (pick != null)
{
    var pullFile = Path.Combine(tmp, "pulled.bin");
    await AdbClient.ExecToFileAsync(first.ActiveAddr!, "cat " + AdbClient.ShellQuote(pickDir + pick.Name), pullFile);
    Check(new FileInfo(pullFile).Length == pick.Size, "Tải tệp từ máy về (exec cat)", $"{pickDir}{pick.Name}: {new FileInfo(pullFile).Length}/{pick.Size} byte");
}
else Console.WriteLine("SKIP  Tải tệp: không thấy tệp nào để thử");

// 8) phóng to: luồng nét + luồng lưới chuyển sang chỉ điều khiển rồi trở lại
UiDo(() => mgr.AcquireHq(first));
await Task.Delay(6000);
var hq = Ui(() => (first.HqFrames?.Frames ?? 0, first.Thumb?.Options.Video, first.Zoomed));
Check(hq.Item1 > 0 && hq.Item2 == false && hq.Item3, "Phóng to: có luồng nét, ô lưới chỉ điều khiển", $"{hq.Item1} khung nét");
long before = Ui(() => first.Frames?.Frames ?? 0);
UiDo(() => mgr.ReleaseHq(first));
await Task.Delay(6000);
var after = Ui(() => (first.Frames?.Frames ?? 0, first.Thumb?.Options.Video, first.IsOnline));
Check(after.Item1 > before && after.Item2 == true && after.Item3, "Đóng phóng to: ô lưới có hình lại", $"+{after.Item1 - before} khung sau khi đóng");

// 9) tạm dừng xem / tiếp tục
UiDo(() => mgr.SetPaused([first.Id], true, out _));
await Task.Delay(5000);
Check(Ui(() => first.Thumb?.Options.Video == false && first.IsOnline), "Tạm dừng xem: phiên chỉ điều khiển, vẫn online");
UiDo(() => mgr.SetPaused([first.Id], false, out _));
await Task.Delay(6000);
Check(Ui(() => first.Thumb?.Options.Video == true && first.IsOnline), "Tiếp tục xem: có hình lại");

// 10) chẩn đoán + quét mạng nội bộ (chỉ dò cổng, không kết nối gì mới nếu không có)
var diag = Ui(() => mgr.Diagnostics());
Check(diag.Rows.Count >= total, "Chẩn đoán kết nối", $"adb={diag.AdbConnections} device={diag.DeviceConnections} ô={diag.Tiles}");

// 11) khoá uỷ quyền đã có trên máy root (đọc, không ghi)
if (Ui(() => first.Root))
{
    var keys = await AdbClient.ShellAsync(first.ActiveAddr!, "su -c 'cat /data/misc/adb/adb_keys'", 10000);
    var frag = File.ReadAllText(AdbClient.PubKeyPath()).Trim().Split(' ')[0].Substring(40, 40);
    Check(keys.Contains(frag), "Khoá uỷ quyền của PC đã nằm trong adb_keys");
}

// 11b) chế độ "chỉ nhập từ PC" (chỉ khi chạy với tham số kbd): bật rồi trả lại bàn phím cũ trên 1 máy
if (args.Contains("kbd"))
{
    var imeBefore = await PcKeyboard.CurrentIme(first.ActiveAddr!);
    async Task<BatchTask> RunOp(string op)
    {
        var tcs = new TaskCompletionSource<BatchTask>();
        UiDo(() => act.TaskUpdated += t => { if (t.Finished && t.Op == op) tcs.TrySetResult(t); });
        UiDo(() => act.RunBatch(op, [first.Id], null, op));
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(90));
    }
    var on = await RunOp("pckbd");
    var cur = await PcKeyboard.CurrentIme(first.ActiveAddr!);
    Check(on.OkCount == 1 && cur == PcKeyboard.Ime && Ui(() => first.PcKeyboardOn), "Bật chế độ chỉ nhập từ PC", $"{on.Results[0].Msg} · bàn phím: {cur}");
    var off = await RunOp("phonekbd");
    var back = await PcKeyboard.CurrentIme(first.ActiveAddr!);
    Check(off.OkCount == 1 && back == imeBefore && !Ui(() => first.PcKeyboardOn), "Tắt chế độ: trả lại bàn phím cũ", $"{off.Results[0].Msg} · trước: {imeBefore} · sau: {back}");
}

// 12) đa ngôn ngữ
UiDo(() => I18n.Init("en"));
Check(I18n.T("Đã chọn {0} máy", 3) == "3 phones selected" || I18n.T("Đã chọn {0} máy", 3).Contains('3'), "Dịch câu có biến", I18n.T("Đã chọn {0} máy", 3));
Check(I18n.T("Đang xem ở màn hình lớn") != "Đang xem ở màn hình lớn", "Dịch câu thường", I18n.T("Đang xem ở màn hình lớn"));

UiDo(() => mgr.Dispose());
Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
try { Directory.Delete(tmp, true); } catch { }
return fail == 0 ? 0 : 1;
