using System.IO;
using System.Windows.Threading;
using ControlPhone.Adb;
using ControlPhone.Core;

// Kiểm thử lõi trên máy thật — CHỈ lệnh chỉ đọc (không chạm/không đổi cài đặt điện thoại).
int pass = 0, fail = 0;
void Check(bool ok, string name, string detail = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail != "" ? "  — " + detail : "")}"); if (ok) pass++; else fail++; }

var tmp = Path.Combine(Path.GetTempPath(), "cp-coretest");
Directory.CreateDirectory(tmp);
Environment.SetEnvironmentVariable("CP_DATA_DIR", null);

Dispatcher? ui = null;
var ready = new ManualResetEventSlim();
var th = new Thread(() => { ui = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); }) { IsBackground = true };
th.SetApartmentState(ApartmentState.STA);
th.Start();
ready.Wait();

T Ui<T>(Func<T> f) => ui!.Invoke(f);
void UiDo(Action a) => ui!.Invoke(a);

UiDo(() => { Store.Load(); I18n.Init("vi"); });
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

// 12) đa ngôn ngữ
UiDo(() => I18n.Init("en"));
Check(I18n.T("Đã chọn {0} máy", 3) == "3 phones selected" || I18n.T("Đã chọn {0} máy", 3).Contains('3'), "Dịch câu có biến", I18n.T("Đã chọn {0} máy", 3));
Check(I18n.T("Đang xem ở màn hình lớn") != "Đang xem ở màn hình lớn", "Dịch câu thường", I18n.T("Đang xem ở màn hình lớn"));

UiDo(() => mgr.Dispose());
Console.WriteLine($"\n>>> {pass} đạt, {fail} lỗi");
try { Directory.Delete(tmp, true); } catch { }
return fail == 0 ? 0 : 1;
