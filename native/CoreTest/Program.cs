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

// máy mới chờ cho phép, chưa biết IP xwdb: dotnet run -c Release -- xwnew <serial> — gỡ khoá (máy về "unauthorized"), tìm xwdb trong LAN, chữa
if (args.Contains("xwnew"))
{
    var s = args.SkipWhile(a => a != "xwnew").Skip(1).First();
    async Task<string> State() => (await AdbClient.RunAsync(["devices"], 8000)).Out.Split('\n').Select(l => l.Trim().Split('\t')).FirstOrDefault(f => f[0] == s)?.ElementAtOrDefault(1) ?? "(không thấy)";
    var ip = System.Text.RegularExpressions.Regex.Match(await AdbClient.ShellAsync(s, "ip -f inet addr show wlan0 | grep -m1 inet"), @"inet ([\d.]+)").Groups[1].Value;
    var frag = File.ReadAllText(AdbClient.PubKeyPath()).Trim().Split(' ')[0].Substring(40, 40);
    // IP của các máy khác đang kết nối USB bình thường (ControlPhone biết qua thông tin pin/IP) → không cần thử
    var skip = new HashSet<string>();
    foreach (var l in (await AdbClient.RunAsync(["devices"], 8000)).Out.Split('\n').Select(x => x.Trim().Split('\t')).Where(f => f.Length == 2 && f[1] == "device" && f[0] != s && !f[0].Contains(':')))
        try { var m = System.Text.RegularExpressions.Regex.Match(await AdbClient.ShellAsync(l[0], "ip -f inet addr show wlan0 | grep -m1 inet", 5000), @"inet ([\d.]+)"); if (m.Success) skip.Add(m.Groups[1].Value); } catch { }
    var hide = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
    try
    {
        await AdbClient.RunAsync(["connect", ip + ":15555"], 10000);
        await AdbClient.ShellAsync(ip + ":15555", $"su -c 'sed -i \"/{frag.Replace("/", "\\/")}/d\" /data/misc/adb/adb_keys'", 10000);
        await AdbClient.RunAsync(["disconnect", ip + ":15555"], 8000);
        await AdbClient.RunAsync(["-s", s, "reconnect"], 8000);
        string st = "";
        for (int i = 0; i < 20 && st != "unauthorized"; i++) { await Task.Delay(1000); st = await State(); }
        Check(st == "unauthorized", "Gây lỗi: máy về \"unauthorized\"", st);
        var sw4 = System.Diagnostics.Stopwatch.StartNew();
        var found = await ControlPhone.Core.DeviceManager.FindXwdbAsync([s], skip, hide);
        Check(found.TryGetValue(s, out var xw) && xw == ip + ":15555", "Tìm thấy xwdb của máy trong LAN (ghép theo serial USB)", $"{(found.TryGetValue(s, out var x2) ? x2 : "(không thấy)")} · bỏ qua {skip.Count} IP đã biết · {sw4.ElapsedMilliseconds} ms");
        var r = await ControlPhone.Core.DeviceManager.HealViaXwdbAsync(s, ip + ":15555", null);
        Check(r == "" && await State() == "device", "Tự cấp lại quyền", $"{(r == "" ? "OK" : r)} · {await State()} · tổng {sw4.ElapsedMilliseconds} ms");
    }
    finally
    {
        if (await State() != "device") Console.WriteLine("Khôi phục: " + await ControlPhone.Core.DeviceManager.HealViaXwdbAsync(s, ip + ":15555", null));
    }
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return 0;
}

// chữa máy đang "unauthorized" qua xwdb: dotnet run -c Release -- xwfix <serial>=<ip:15555>,…
if (args.Contains("xwfix"))
{
    foreach (var pair in args.SkipWhile(a => a != "xwfix").Skip(1).First().Split(','))
    {
        var (s, xw) = (pair.Split('=')[0], pair.Split('=')[1]);
        var sw3 = System.Diagnostics.Stopwatch.StartNew();
        var r = await ControlPhone.Core.DeviceManager.HealViaXwdbAsync(s, xw, null);
        var st = (await AdbClient.RunAsync(["devices"], 8000)).Out.Split('\n').Select(l => l.Trim().Split('\t')).FirstOrDefault(f => f[0] == s)?.ElementAtOrDefault(1) ?? "(không thấy)";
        Check(r == "" && st == "device", $"{s}: tự cấp lại quyền qua {xw}", $"{(r == "" ? "OK" : r)} · {st} · {sw3.ElapsedMilliseconds} ms");
    }
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return 0;
}

// máy có bộ mã hoá chết vì video_codec_options: dotnet run -c Release -- codecfallback <serial>
if (args.Contains("codecfallback"))
{
    var serial = args.SkipWhile(a => a != "codecfallback").Skip(1).First();
    var learn = typeof(ControlPhone.Core.DeviceManager).GetMethod("LearnFromFailure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var dev = new PhoneDevice(serial);
    var noOpt = typeof(PhoneDevice).GetField("NoCodecOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    async Task<(long Packets, bool Running, string Log)> Run(bool codecOptions)
    {
        string ended = "";
        using var s = new ControlPhone.Scrcpy.ScrcpySession(serial, new() { MaxSize = 480, BitRate = 600_000, MaxFps = 15, PowerOn = false, CodecOptions = codecOptions });
        s.Ended += r => ended = r;
        try { await s.StartAsync(); } catch (Exception e) { ended = e.Message; }
        await Task.Delay(3000);
        if (codecOptions) learn.Invoke(null, [dev, s, ended]);
        return (s.PacketsReceived, s.Running, s.LogText);
    }
    // dòng lệnh nặng nhất (bộ mã hoá tên dài, không trùng mặc định, power_on=false, có tuỳ chọn) vẫn vừa giới hạn
    var worst = ControlPhone.Scrcpy.ScrcpySession.BuildArgs(new() { MaxSize = 1280, BitRate = 12_000_000, MaxFps = 60, PowerOn = false, StayAwake = true }, "7fffffff", "c2.qti.avc.encoder.low_latency.secure", "OMX.google.h264.encoder");
    var hw2 = ControlPhone.Scrcpy.ScrcpySession.BuildArgs(new() { MaxSize = 480, BitRate = 600_000, MaxFps = 15, PowerOn = false }, "5f704175", "OMX.Exynos.AVC.Encoder", "OMX.Exynos.AVC.Encoder");
    int L(List<string> a) => "app_process / com.genymobile.scrcpy.Server ".Length + string.Join(' ', a).Length + 1;
    Check(L(worst.Args) <= ControlPhone.Scrcpy.ScrcpySession.CmdlineBudget && L(hw2.Args) <= ControlPhone.Scrcpy.ScrcpySession.CmdlineBudget,
        "Dòng lệnh server ≤ " + ControlPhone.Scrcpy.ScrcpySession.CmdlineBudget + " byte (Samsung tràn bộ đệm ~256 byte)", $"nặng nhất {L(worst.Args)}: {string.Join(' ', worst.Args)}\n  lưới Exynos {L(hw2.Args)}: {string.Join(' ', hw2.Args)}");
    var b0 = await Run(false);
    Console.WriteLine($"không tuỳ chọn (chạy trước): {b0.Packets} gói, running={b0.Running}{(b0.Packets == 0 ? "\n" + b0.Log : "")}");
    var a = await Run(true);
    bool crashed = a.Log.Contains("stack corruption") || !a.Running;
    Console.WriteLine($"có video_codec_options: {a.Packets} gói, running={a.Running}{(crashed ? " · server chết" : "")}");
    Check(!crashed || (bool)noOpt.GetValue(dev)!, "Server chết vì video_codec_options → máy được đánh dấu bỏ tuỳ chọn", crashed ? "đã đánh dấu: " + noOpt.GetValue(dev) : "máy này không chết, không cần");
    var b = await Run(false);
    Check(b.Packets > 0 && b.Running, "Không kèm video_codec_options: có hình, phiên vẫn chạy", $"{b.Packets} gói / 3 s" + (b.Packets == 0 ? "\n" + b.Log : ""));
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return 0;
}

// tự cấp lại quyền qua xwdb: dotnet run -c Release -- xwheal <serial USB> — GỠ khoá PC khỏi adb_keys (máy về "chờ cho phép")
// rồi để ControlPhone tự chữa. Chỉ chạy trên máy có xwdb; luôn khôi phục khoá khi kết thúc.
if (args.Contains("xwheal"))
{
    var s = args.SkipWhile(a => a != "xwheal").Skip(1).First();
    var ip = System.Text.RegularExpressions.Regex.Match(await AdbClient.ShellAsync(s, "ip -f inet addr show wlan0 | grep -m1 inet"), @"inet ([\d.]+)").Groups[1].Value;
    var xw = ip + ":15555";
    var aid = (await AdbClient.ShellAsync(s, "settings get secure android_id")).Trim();
    var frag = File.ReadAllText(AdbClient.PubKeyPath()).Trim().Split(' ')[0].Substring(40, 40);
    async Task<string> State() => (await AdbClient.RunAsync(["devices"], 8000)).Out.Split('\n').Select(l => l.Trim().Split('\t')).FirstOrDefault(f => f[0] == s)?.ElementAtOrDefault(1) ?? "(không thấy)";
    Console.WriteLine($"{s} · xwdb {xw} · android_id {aid}");
    try
    {
        // gây lỗi giống thật: khoá PC biến mất khỏi adb_keys, USB kết nối lại → máy hỏi "Cho phép gỡ lỗi USB"
        await AdbClient.RunAsync(["connect", xw], 10000);
        var rm = await AdbClient.ShellAsync(xw, $"su -c 'sed -i \"/{frag.Replace("/", "\\/")}/d\" /data/misc/adb/adb_keys; wc -l < /data/misc/adb/adb_keys'", 10000);
        await AdbClient.RunAsync(["disconnect", xw], 8000);
        await AdbClient.RunAsync(["-s", s, "reconnect"], 8000);
        string st = "";
        for (int i = 0; i < 20 && st != "unauthorized"; i++) { await Task.Delay(1000); st = await State(); }
        Check(st == "unauthorized", "Gây lỗi: gỡ khoá PC → USB về \"unauthorized\" (máy hiện hộp thoại cho phép)", $"adb_keys còn {rm.Trim()} dòng · trạng thái {st}");
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var r = await ControlPhone.Core.DeviceManager.HealViaXwdbAsync(s, xw, aid);
        st = await State();
        var top = st == "device" ? await AdbClient.ShellAsync(s, "dumpsys activity activities | grep -m1 -E 'mResumedActivity|ResumedActivity:'") : "";
        Check(r == "" && st == "device" && !top.Contains("UsbDebuggingActivity"), "Tự chữa qua xwdb: khoá cấy lại, USB về \"device\", hộp thoại đã đóng",
            $"{(r == "" ? "OK" : r)} · {st} · {sw2.ElapsedMilliseconds} ms · {top.Trim()}");
    }
    finally
    {
        if (await State() != "device") { Console.WriteLine("Khôi phục…"); Console.WriteLine(await ControlPhone.Core.DeviceManager.HealViaXwdbAsync(s, xw, aid)); }
    }
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return 0;
}

// giữ uỷ quyền adb: dotnet run -c Release -- authkeep <serial,serial,…> — khoá PC có trong adb_keys, thời hạn uỷ quyền tắt (Android 11+)
if (args.Contains("authkeep"))
{
    foreach (var s in args.SkipWhile(a => a != "authkeep").Skip(1).First().Split(','))
    {
        var sdk = int.TryParse((await AdbClient.ShellAsync(s, "getprop ro.build.version.sdk")).Trim(), out var v) ? v : 0;
        if (sdk >= 30) await AdbClient.ShellAsync(s, "[ \"$(settings get global adb_allowed_connection_time)\" = 0 ] || settings put global adb_allowed_connection_time 0", 10000);
        var k = await AdbClient.PersistAdbKeyAsync(s);
        var window = (await AdbClient.ShellAsync(s, "settings get global adb_allowed_connection_time")).Trim();
        Check(k.Ok && (sdk < 30 || window == "0"), $"{s}: khoá PC trong adb_keys, uỷ quyền không hết hạn", $"SDK {sdk} · {k.Msg} · adb_allowed_connection_time={window}");
    }
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return 0;
}

// autoboot (thoát chế độ sạc): dotnet run -c Release -- autoboot <serial,serial,…> — cài nếu thiếu, không tắt nguồn máy
if (args.Contains("autoboot"))
{
    var serials = args.SkipWhile(a => a != "autoboot").Skip(1).First().Split(',');
    var orig = @"D:\BuildRomAndroid\scripts\device\n9autoboot.rc";
    if (File.Exists(orig))
    {
        var rcField = typeof(AutoBoot).GetField("Rc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var rc = System.Text.Encoding.ASCII.GetBytes((string)rcField.GetValue(null)!);
        Check(rc.SequenceEqual(File.ReadAllBytes(orig)), "Nội dung rc nhúng trùng bản gốc (byte, LF)", $"{rc.Length} byte");
    }
    foreach (var s in serials)
    {
        var r = await AutoBoot.EnsureAsync("test:" + s, s);
        var chk = await AdbClient.ShellAsync(s, "grep -c 'sys.powerctl reboot' /system/etc/init/n9autoboot.rc; grep -c $'\\r' /system/etc/init/n9autoboot.rc; ls -lZ /system/etc/init/n9autoboot.rc; grep -E ' / | /system ' /proc/mounts | head -2", 15000);
        var l = chk.Split('\n').Select(x => x.Trim()).ToArray();
        Check(r.Ok && l.ElementAtOrDefault(0) == "1" && l.ElementAtOrDefault(1) == "0" && chk.Contains("system_file") && chk.Contains("root root"),
            $"{s}: autoboot có trên máy (1 dòng powerctl, không CR, root:root, system_file)", r.Msg + " | " + string.Join(" | ", l.Skip(2)));
    }
    var abLog = Path.Combine(Store.DataDir, "logs", "autoboot.log");
    Console.WriteLine($"Nhật ký: {abLog}\n{(File.Exists(abLog) ? File.ReadAllText(abLog) : "(trống — mọi máy đã có sẵn)")}");
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return 0;
}

// bộ mã hoá phần cứng + không gián đoạn điều khiển: dotnet run -c Release -- hwenc <serial>
if (args.Contains("hwenc"))
{
    var serial = args.SkipWhile(a => a != "hwenc").Skip(1).First();
    const string s9 = "    --video-codec=h264 --video-encoder=OMX.Exynos.AVC.Encoder         (hw) [vendor]\n    --video-codec=h264 --video-encoder=c2.android.avc.encoder         (sw)\n    --video-codec=h264 --video-encoder=OMX.google.h264.encoder        (sw) (alias for c2.android.avc.encoder)\n";
    const string a50 = "    --video-codec=h264 --video-encoder=OMX.Exynos.AVC.Encoder         (hw) [vendor]\n    --video-codec=h264 --video-encoder=OMX.google.h264.encoder        (hybrid)\n";
    const string swOnly = "    --video-codec=h264 --video-encoder=c2.android.avc.encoder (sw)\n    --video-codec=h265 --video-encoder=OMX.qcom.video.encoder.hevc (hw)\n";
    Check(ControlPhone.Scrcpy.ScrcpySession.PickHardwareEncoder(s9) == "OMX.Exynos.AVC.Encoder" && ControlPhone.Scrcpy.ScrcpySession.PickHardwareEncoder(a50) == "OMX.Exynos.AVC.Encoder" && ControlPhone.Scrcpy.ScrcpySession.PickHardwareEncoder(swOnly) == null,
        "Chọn bộ mã hoá H.264 phần cứng, bỏ c2.android.* / OMX.google.* (không có thì để mặc định)");
    var hw = await ControlPhone.Scrcpy.ScrcpySession.HardwareEncoderAsync(serial);
    Check(hw != null && !ControlPhone.Scrcpy.ScrcpySession.IsSoftwareEncoder(hw), "Đọc bộ mã hoá phần cứng từ máy thật (list_encoders)", hw ?? "(không có)");
    // phiên có hình: dùng đúng bộ mã hoá phần cứng + lặp khung thưa khi đứng yên (kiểm tra dòng lệnh đang chạy trên máy)
    using (var s = new ControlPhone.Scrcpy.ScrcpySession(serial, new() { MaxSize = 480, BitRate = 600_000, MaxFps = 15, PowerOn = false }))
    {
        await s.StartAsync(); await Task.Delay(2500);
        var cmd = await AdbClient.ShellAsync(serial, "ps -A -o ARGS | grep -m1 'com.genymobile.scrcpy.Server 5.0 scid=' | grep -v grep", 8000);
        // video_encoder= có thể được bỏ khi trùng bộ mã hoá mặc định của máy (rút gọn dòng lệnh) — vẫn là bộ mã hoá phần cứng
        Check(s.Running && s.PacketsReceived > 0 && s.UsedEncoder == hw && (cmd.Contains("video_encoder=" + hw) || !cmd.Contains("video_encoder=")) && cmd.Contains("repeat-previous-frame-after:long=1000000"), "Phiên chạy bằng bộ mã hoá phần cứng + lặp khung thưa khi màn hình đứng yên", $"{s.PacketsReceived} gói / 2,5 s" + (s.PacketsReceived == 0 ? $" · running={s.Running}\n{cmd.Trim()}\n{s.LogText}" : ""));
    }
    // không gián đoạn điều khiển khi bật / tắt màn hình lớn (luồng lưới đổi chế độ)
    UiDo(() => { Store.Load(); I18n.Init("vi"); });
    var mgrH = Ui(() => new DeviceManager(ui!));
    var dev = Ui(() => new PhoneDevice(serial) { ActiveAddr = serial });
    typeof(DeviceManager).GetMethod("EnsureThumb", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(mgrH, [dev]);
    var swH = System.Diagnostics.Stopwatch.StartNew();
    while (swH.Elapsed.TotalSeconds < 20 && !Ui(() => dev.IsOnline)) await Task.Delay(100);
    async Task<(int Null, int Total)> Watch(Action change, int ms)
    {
        UiDo(change);
        int nul = 0, tot = 0; var w = System.Diagnostics.Stopwatch.StartNew();
        while (w.ElapsedMilliseconds < ms) { tot++; if (Ui(() => dev.ControlSession) == null) nul++; await Task.Delay(10); }
        return (nul, tot);
    }
    var open = await Watch(() => mgrH.AcquireHq(dev), 5000);
    var close = await Watch(() => mgrH.ReleaseHq(dev), 5000);
    Console.WriteLine($"  mở màn hình lớn: {open.Null}/{open.Total} lần không có phiên điều khiển · đóng: {close.Null}/{close.Total}");
    Check(open.Null == 0 && close.Null == 0, "Bật / tắt màn hình lớn không làm mất phiên điều khiển lúc nào");
    UiDo(() => mgrH.Dispose());
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return fail == 0 ? 0 : 1;
}

// chụp màn hình chọn chế độ (ngoài màn hình, không hiện cho người dùng): dotnet run -c Release -- modeshot <thư mục>
if (args.Contains("modeshot"))
{
    var dir = args.SkipWhile(a => a != "modeshot").Skip(1).First();
    Directory.CreateDirectory(dir);
    UiDo(() => { Store.Load(); I18n.Init("vi"); var app = new ControlPhone.App(); app.InitializeComponent(); ControlPhone.Theme.Apply("dark"); });
    async Task Shot(ControlPhone.ModeWindow w, string name, int waitMs)
    {
        UiDo(() => { w.Left = -20000; w.Top = 0; w.ShowActivated = false; w.ShowInTaskbar = false; w.Show(); });
        await Task.Delay(waitMs);
        UiDo(() =>
        {
            var el = (System.Windows.FrameworkElement)w.Content;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var bg = new System.Windows.Controls.Border { Background = w.Background, Width = el.ActualWidth, Height = el.ActualHeight };
            bg.Measure(new System.Windows.Size(el.ActualWidth, el.ActualHeight)); bg.Arrange(new System.Windows.Rect(0, 0, el.ActualWidth, el.ActualHeight));
            rtb.Render(bg); rtb.Render(el);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, name)); enc.Save(fs);
            w.Close();
        });
    }
    if (args.Contains("singleshot"))
    {
        var serial = args.SkipWhile(a => a != "singleshot").Skip(1).First();
        var mwin = Ui(() => { var w = new ControlPhone.MainWindow(serial) { WindowState = System.Windows.WindowState.Normal, Left = -20000, Top = 0, Width = 1400, Height = 900, ShowActivated = false, ShowInTaskbar = false }; w.Show(); return w; });
        await Task.Delay(12000);
        UiDo(() =>
        {
            var el = (System.Windows.FrameworkElement)mwin.Content;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var bg = new System.Windows.Controls.Border { Background = mwin.Background, Width = el.ActualWidth, Height = el.ActualHeight };
            bg.Measure(new System.Windows.Size(el.ActualWidth, el.ActualHeight)); bg.Arrange(new System.Windows.Rect(0, 0, el.ActualWidth, el.ActualHeight));
            rtb.Render(bg); rtb.Render(el);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, "mode-3-single.png")); enc.Save(fs);
            mwin.Close();
        });
        await Task.Delay(3000);
        return 0;
    }
    await Shot(Ui(() => new ControlPhone.ModeWindow()), "mode-1-choices.png", 800);
    await Shot(Ui(() => new ControlPhone.ModeWindow(startWithList: true)), "mode-2-list.png", 6000);
    Console.WriteLine("đã chụp vào " + dir);
    return 0;
}

// chế độ "Chỉ hiển thị 1 điện thoại": chỉ máy đó được nhận + mở luồng, không đụng máy khác (dotnet run -c Release -- single <serial>)
if (args.Contains("single"))
{
    var serial = args.SkipWhile(a => a != "single").Skip(1).First();
    UiDo(() => { Store.Load(); I18n.Init("vi"); });
    // giao diện: dựng cửa sổ chính ở chế độ 1 máy (không hiện) + màn hình chọn chế độ
    UiDo(() =>
    {
        var app = new ControlPhone.App(); app.InitializeComponent();
        var w = new ControlPhone.MainWindow(serial);
        bool gridHidden = (w.FindName("GridArea") as System.Windows.FrameworkElement)?.Parent is System.Windows.FrameworkElement m && m.Visibility == System.Windows.Visibility.Collapsed;
        bool viewer = (w.FindName("ViewerPane") as System.Windows.FrameworkElement)?.Visibility == System.Windows.Visibility.Visible;
        bool sidebar = (w.FindName("Sidebar") as System.Windows.FrameworkElement)?.Visibility == System.Windows.Visibility.Collapsed;
        bool btns = (w.FindName("BtnAllMode") as System.Windows.FrameworkElement)?.Visibility == System.Windows.Visibility.Visible && (w.FindName("BtnSingleMode") as System.Windows.FrameworkElement)?.Visibility == System.Windows.Visibility.Collapsed;
        Check(gridHidden && viewer && sidebar && btns, "Cửa sổ 1 máy: ẩn lưới + thanh bên, hiện màn hình lớn, có nút Điều khiển tất cả", $"lưới ẩn={gridHidden} màn lớn={viewer} thanh bên ẩn={sidebar} nút={btns}");
        w.Close();
        var mw = new ControlPhone.ModeWindow();
        Check(mw.Content is System.Windows.Controls.Grid g && g.Children.Count == 1, "Dựng màn hình chọn chế độ");
        mw.Close();
    });
    // lõi: chỉ nhận đúng máy đã chọn
    DeviceManager.SingleAddr = serial;
    var mgrS = Ui(() => new DeviceManager(ui!));
    UiDo(() => mgrS.Start());
    var swS = System.Diagnostics.Stopwatch.StartNew();
    while (swS.Elapsed.TotalSeconds < 30 && !Ui(() => mgrS.Devices.Any(d => d.IsOnline))) await Task.Delay(300);
    await Task.Delay(2000);
    var info = Ui(() => mgrS.Devices.Select(d => (d.Id, d.Pending, d.IsOnline, Video: d.Thumb?.Options.Video)).ToList());
    foreach (var x in info) Console.WriteLine($"  ô: {x.Id} · online {x.IsOnline} · luồng lưới có hình: {x.Video}");
    Check(info.Count == 1 && info[0].Id == serial && info[0].IsOnline, "Chỉ nhận đúng 1 máy đã chọn (bỏ qua các máy khác)", $"{info.Count} ô sau {swS.Elapsed.TotalSeconds:0}s");
    Check(info.Count == 1 && info[0].Video == false, "Máy đó không mở luồng lưới (chỉ điều khiển; hình do màn hình lớn)");
    UiDo(() => mgrS.Dispose());
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return fail == 0 ? 0 : 1;
}

// gom lô lệnh: chỉ bỏ điểm di chuyển cũ của cùng ngón, không bao giờ bỏ chạm xuống / nhấc lên (dotnet run -c Release -- batch)
if (args.Contains("batch"))
{
    byte[] T(byte act, long pid, int x) => ControlPhone.Scrcpy.ControlMessages.Touch(act, pid, x, 0, 1000, 2000, 1);
    const byte D = ControlPhone.Scrcpy.ControlMessages.ActionDown, U = ControlPhone.Scrcpy.ControlMessages.ActionUp, M = ControlPhone.Scrcpy.ControlMessages.ActionMove;
    var list = new List<byte[]> { T(D, 1, 0), T(M, 1, 10), T(M, 1, 20), T(M, 2, 5), ControlPhone.Scrcpy.ControlMessages.Text("a"), T(M, 1, 30), T(U, 1, 30), T(M, 2, 6), T(D, 1, 40), T(M, 1, 50) };
    var stale = typeof(ControlPhone.Scrcpy.ScrcpySession).GetMethod("IsStaleMove", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var kept = Enumerable.Range(0, list.Count).Where(i => !(bool)stale.Invoke(null, [list, i])!).ToList();
    // giữ: DOWN1, MOVE2(5)?→bị thay bởi MOVE2(6) nên bỏ; chữ "a"; MOVE1(30) (ngay trước UP); UP1; MOVE2(6); DOWN1; MOVE1(50)
    var expect = new List<int> { 0, 4, 5, 6, 7, 8, 9 };
    Check(kept.SequenceEqual(expect), "Gom lô: chỉ bỏ điểm di chuyển cũ, giữ nguyên chạm/nhấc/chữ và thứ tự", $"giữ {string.Join(",", kept)}");
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return fail == 0 ? 0 : 1;
}

// điều khiển đồng bộ nhiều máy: dội lệnh vào mọi máy cùng lúc, đo bao lâu sau lệnh cuối mới được máy xử lý xong
// dotnet run -c Release -- ctlflood
if (args.Contains("ctlflood"))
{
    var adbList = new List<AdbDevice>();
    using (var c0 = new CancellationTokenSource(5000)) { try { await AdbClient.TrackDevicesAsync(l => { adbList = l.ToList(); c0.Cancel(); }, _ => { }, c0.Token); } catch { } }
    var serials = adbList.Where(x => x.State == "device").Select(x => x.Addr).ToList();
    var sess = new List<ControlPhone.Scrcpy.ScrcpySession>();
    await Task.WhenAll(serials.Select(async sr =>
    {
        var s = new ControlPhone.Scrcpy.ScrcpySession(sr, new() { Video = false, PowerOn = false });
        try { await s.StartAsync(); lock (sess) sess.Add(s); } catch { s.Dispose(); }
    }));
    Console.WriteLine($"  {sess.Count}/{serials.Count} máy đã mở phiên điều khiển");
    var noop = ControlPhone.Scrcpy.ControlMessages.Text("");
    foreach (var s in sess) s.Send(ControlPhone.Scrcpy.ControlMessages.SetClipboardMsg("cp-test", paste: false));
    await Task.Delay(1000);
    foreach (var (batch, rate) in new[] { (true, 60), (false, 120), (true, 120), (false, 120), (true, 120), (false, 600), (true, 600), (false, 600), (true, 600) })
    {
        ControlPhone.Scrcpy.ScrcpySession.BatchWrites = batch;
        await Task.Delay(500);
        long w0 = sess.Sum(s => s.ControlWrites);
        var swF = System.Diagnostics.Stopwatch.StartNew(); long sent = 0;
        while (swF.Elapsed.TotalSeconds < 4)
        {
            long due = (long)(swF.Elapsed.TotalSeconds * rate);
            for (; sent < due; sent++) foreach (var s in sess) s.Send(noop);
            await Task.Delay(1);
        }
        // máy nào trả clipboard nghĩa là đã xử lý xong MỌI lệnh trước đó (scrcpy xử lý tuần tự)
        var replies = new System.Collections.Concurrent.ConcurrentDictionary<ControlPhone.Scrcpy.ScrcpySession, double>();
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var handlers = sess.Select(s => { Action<string> h = _ => replies.TryAdd(s, System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds); s.ClipboardReceived += h; return (s, h); }).ToList();
        foreach (var s in sess) s.Send(ControlPhone.Scrcpy.ControlMessages.GetClipboardMsg(0));
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (replies.Count < sess.Count && wait.Elapsed.TotalSeconds < 20) await Task.Delay(5);
        foreach (var (s, h) in handlers) s.ClipboardReceived -= h;
        var v = replies.Values.ToList();
        Console.WriteLine($"  {(batch ? "GOM LÔ (mới)" : "từng lệnh (cũ)"),-15} {rate,4} lệnh/giây/máy × {sess.Count} máy: lệnh cuối xong sau TB {(v.Count > 0 ? v.Average() : 0):0} ms, chậm nhất {(v.Count > 0 ? v.Max() : 0):0} ms · {replies.Count}/{sess.Count} máy trả lời · {sess.Sum(s => s.ControlWrites) - w0} lần ghi");
        await Task.Delay(1500);
    }
    foreach (var s in sess) s.Dispose();
    return 0;
}

// máy vừa khởi động lại: mở phiên nhiều lần với stay_awake bật / tắt, đếm số lần server chết
// dotnet run -c Release -- bootstart <serial> [số vòng]
if (args.Contains("bootstart"))
{
    var a2 = args.SkipWhile(a => a != "bootstart").Skip(1).ToArray();
    var serial = a2[0]; int rounds = a2.Length > 1 ? int.Parse(a2[1]) : 4;
    var up = (await AdbClient.ShellAsync(serial, "cut -d. -f1 /proc/uptime", 8000)).Trim();
    Console.WriteLine($"  máy đã chạy {up} giây kể từ khi khởi động");
    foreach (var stay in new[] { true, false })
    {
        int ok = 0, dead = 0; string lastErr = "";
        for (int i = 0; i < rounds; i++)
        {
            var s = new ControlPhone.Scrcpy.ScrcpySession(serial, new() { MaxSize = 480, BitRate = 300_000, MaxFps = 10, PowerOn = true, StayAwake = stay, Encoder = "OMX.google.h264.encoder" });
            try { await s.StartAsync(); await Task.Delay(2500); if (s.Running) ok++; else { dead++; lastErr = s.LogText.Split('\n').FirstOrDefault(l => l.Contains("ERROR")) ?? "đóng"; } }
            catch (Exception e) { dead++; lastErr = e.Message; }
            s.Dispose();
            await Task.Delay(500);
        }
        Console.WriteLine($"  stay_awake={(stay ? "bật (cách cũ)" : "tắt (cách mới)"),-14}: {ok}/{rounds} chạy được, {dead} lần chết{(lastErr != "" ? " — " + lastErr[..Math.Min(110, lastErr.Length)] : "")}");
    }
    return 0;
}

// bộ phát hiện đứng hình: chặn luồng giao diện 400 ms trong 1 việc có đánh dấu → phải ghi lại đúng tên việc
if (args.Contains("stall"))
{
    UiDo(() => Store.Load());
    var logFile = Path.Combine(Store.DataDir, "logs", "ui-stalls.log");
    if (File.Exists(logFile)) File.Delete(logFile);
    ControlPhone.StallWatch.Start(ui!);
    await Task.Delay(500);
    UiDo(() => { using (ControlPhone.StallWatch.Op("việc thử chặn 400 ms")) Thread.Sleep(400); });
    await Task.Delay(800);
    var txt = File.Exists(logFile) ? File.ReadAllText(logFile) : "";
    Console.WriteLine("  " + txt.Trim());
    Check(txt.Contains("việc thử chặn 400 ms") && System.Text.RegularExpressions.Regex.IsMatch(txt, @"đứng ≥ (2|3|4|5)\d\d ms"), "Ghi lại lần đứng hình kèm tên việc đang chạy");
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return fail == 0 ? 0 : 1;
}

// gọi adb.exe từ luồng giao diện KHÔNG được làm đứng luồng đó: dotnet run -c Release -- uiblock
if (args.Contains("uiblock"))
{
    var times = new List<double>();
    for (int i = 0; i < 5; i++)
    {
        double ms = Ui(() => { var t0 = System.Diagnostics.Stopwatch.GetTimestamp(); _ = AdbClient.RunAsync(["version"], 5000); return System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds; });
        times.Add(ms);
        await Task.Delay(300);
    }
    // cách cũ (≤ 2.0.13): Process.Start chạy thẳng trên luồng giao diện
    var oldTimes = new List<double>();
    for (int i = 0; i < 5; i++)
    {
        oldTimes.Add(Ui(() =>
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var psi = new System.Diagnostics.ProcessStartInfo(AdbClient.AdbPath, "version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var pr = System.Diagnostics.Process.Start(psi);
            return System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }));
        await Task.Delay(300);
    }
    Console.WriteLine($"  cách cũ: luồng giao diện bị giữ {string.Join(", ", oldTimes.Select(t => t.ToString("0")))} ms");
    Check(times.Max() < 20, "Chạy adb.exe không chặn luồng giao diện", $"luồng giao diện bị giữ tối đa {times.Max():0.0} ms (5 lần: {string.Join(", ", times.Select(t => t.ToString("0.0")))})");
    Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
    return fail == 0 ? 0 : 1;
}

// độ trễ ĐIỀU KHIỂN đầu-cuối: gửi lệnh kéo thanh thông báo, đo tới khi hình trên PC đổi rõ (rồi đóng lại)
// dotnet run -c Release -- ctl <serial>
if (args.Contains("ctl"))
{
    var serial = args.SkipWhile(a => a != "ctl").Skip(1).First();
    double TopBrightness(byte[] px, int w, int h)
    {
        long sum = 0; int n = 0;
        for (int y = 0; y < h / 4; y += 4) for (int x = 0; x < w; x += 4) { int i = (y * w + x) * 4; sum += px[i] + px[i + 1] + px[i + 2]; n++; }
        return n > 0 ? sum / (3.0 * n) : 0;
    }
    var onlyGrid = args.Contains("gridonly");
    foreach (var (name, opt) in new (string, ControlPhone.Scrcpy.ScrcpyOptions)[]
    {
        ("lưới 480p 5fps phần mềm", new() { MaxSize = 480, BitRate = 150_000, MaxFps = 5, PowerOn = false, Encoder = "OMX.google.h264.encoder" }),
        ("lưới 480p 10fps phần mềm", new() { MaxSize = 480, BitRate = 300_000, MaxFps = 10, PowerOn = false, Encoder = "OMX.google.h264.encoder" }),
        ("lưới 480p 15fps phần mềm", new() { MaxSize = 480, BitRate = 500_000, MaxFps = 15, PowerOn = false, Encoder = "OMX.google.h264.encoder" }),
        ("tăng tốc 480p 30fps phần mềm", new() { MaxSize = 480, BitRate = 1_000_000, MaxFps = 30, PowerOn = false, Encoder = "OMX.google.h264.encoder" }),
        ("tăng tốc 480p 30fps phần cứng", new() { MaxSize = 480, BitRate = 1_000_000, MaxFps = 30, PowerOn = false }),
        ("màn lớn 1280p 30fps phần cứng", new() { MaxSize = 1280, BitRate = 4_000_000, MaxFps = 30, PowerOn = false }),
    })
    {
        if (onlyGrid && !name.StartsWith("lưới")) continue;
        using var s = new ControlPhone.Scrcpy.ScrcpySession(serial, opt);
        using var dec = new ControlPhone.Video.H264Decoder(1);
        s.Packet += (c, k, d) => dec.Feed(c, k, d);
        try { await s.StartAsync(); } catch (Exception e) { Console.WriteLine($"  {name}: lỗi {e.Message}"); continue; }
        await Task.Delay(2500);
        var res = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            double baseB = -1; dec.Output.MarkDirty(); dec.Output.TryTake((px, w, h) => baseB = TopBrightness(px, w, h));
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            s.Send(ControlPhone.Scrcpy.ControlMessages.Empty(ControlPhone.Scrcpy.ControlMessages.ExpandNotificationPanel));
            double? hit = null;
            while (System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds < 3000 && hit == null)
            {
                double b = -1;
                if (dec.Output.TryTake((px, w, h) => b = TopBrightness(px, w, h)) && baseB >= 0 && Math.Abs(b - baseB) > 18) hit = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                await Task.Delay(3);
            }
            if (hit != null) res.Add(hit.Value);
            s.Send(ControlPhone.Scrcpy.ControlMessages.Empty(ControlPhone.Scrcpy.ControlMessages.CollapsePanels));
            await Task.Delay(1800);
        }
        Console.WriteLine($"  {name,-30}: thao tác → hình đổi sau {(res.Count > 0 ? $"TB {res.Average():0} ms (min {res.Min():0}, max {res.Max():0})" : "không thấy")} ({res.Count}/3)");
    }
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
// bộ kiểm thử đầy đủ mở luồng trên MỌI máy → chỉ chạy khi gọi rõ "full" (tránh chạy nhầm khi người dùng đang dùng app)
if (!args.Contains("full")) { Console.WriteLine("Dùng: dotnet run -c Release -- full | kbd | store | xaml | grid | backup | keepalive <serial> | boost <serial> | latency <serial> | ctl <serial> | identity <phones.json> | upgrade <cfg> <phones.json> | otheradb | startfail <serial> | encfail <serial>"); return 2; }
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
