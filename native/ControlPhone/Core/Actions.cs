using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using ControlPhone.Adb;

namespace ControlPhone.Core;

public sealed record TaskResult(string Id, int Vid, string Name, bool Ok, string Msg);

/// <summary>Một thao tác hàng loạt (hiện ở bảng tiến trình).</summary>
public sealed class BatchTask : INotifyPropertyChanged
{
    static int seq;
    public int Id { get; } = Interlocked.Increment(ref seq);
    public string Op { get; init; } = "";
    public string Title { get; init; } = "";
    public int Total { get; init; }
    public int Done { get; internal set; }
    public int OkCount { get; internal set; }
    public int Fail { get; internal set; }
    public bool Finished { get; internal set; }
    public bool OpenResults { get; init; }
    public List<TaskResult> Results { get; } = [];
    public double Progress => Total == 0 ? 100 : Done * 100.0 / Total;
    public string StatusText => Finished ? I18n.T("Xong · bấm để xem") : I18n.T("Đang chạy…");
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>Thao tác quản lý hàng loạt (chuyển từ server/actions.js).</summary>
public sealed class Actions
{
    readonly DeviceManager manager;
    readonly Dispatcher ui;
    public event Action<BatchTask>? TaskUpdated;
    public int RunningTasks;
    public readonly HashSet<Process> ScrcpyChildren = [];

    public Actions(DeviceManager manager, Dispatcher ui) { this.manager = manager; this.ui = ui; }

    static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");
    public static string SafeName(string s) => Regex.Replace(s, @"[\\/:*?""<>|\s]+", "_")[..Math.Min(80, Regex.Replace(s, @"[\\/:*?""<>|\s]+", "_").Length)];

    delegate Task<(bool Ok, string Msg)> Op(PhoneDevice d, string addr, Dictionary<string, object?> p);

    static string S(Dictionary<string, object?> p, string k, string def = "") => p.TryGetValue(k, out var v) && v != null ? v.ToString()! : def;
    static bool B(Dictionary<string, object?> p, string k) => p.TryGetValue(k, out var v) && v is bool b && b;

    Dictionary<string, Op> Ops => new()
    {
        ["shell"] = async (d, a, p) =>
        {
            var cmd = B(p, "root") ? "su -c " + AdbClient.ShellQuote(S(p, "cmd")) : S(p, "cmd");
            var o = await AdbClient.ShellAsync(a, cmd + " 2>&1", 60000);
            return (true, o.Trim());
        },
        ["install"] = async (d, a, p) =>
        {
            var r = await AdbClient.RunAsync([.. AdbClient.Sel(a), "install", "-r", "-d", S(p, "file")], 10 * 60 * 1000);
            return (r.Out.Contains("Success"), string.Join(" ", r.Out.Split('\n').TakeLast(2)).Trim());
        },
        ["uninstall"] = async (d, a, p) => { var o = await AdbClient.ShellAsync(a, $"pm uninstall {AdbClient.ShellQuote(S(p, "pkg"))} 2>&1", 60000); return (o.Contains("Success"), o.Trim()); },
        ["push"] = async (d, a, p) =>
        {
            var dir = S(p, "dir", "/sdcard/Download/").TrimEnd('/') + "/";
            var remote = dir + (S(p, "name") is { Length: > 0 } n ? n : Path.GetFileName(S(p, "file")));
            var r = await AdbClient.RunAsync([.. AdbClient.Sel(a), "push", S(p, "file"), remote], 10 * 60 * 1000);
            if (r.Code == 0) _ = AdbClient.ShellAsync(a, $"am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d {AdbClient.ShellQuote("file://" + remote)} >/dev/null 2>&1", 10000);
            return (r.Code == 0, r.Code == 0 ? remote : r.Out);
        },
        ["url"] = async (d, a, p) => { var o = await AdbClient.ShellAsync(a, $"am start -a android.intent.action.VIEW -d {AdbClient.ShellQuote(S(p, "url"))} 2>&1", 15000); return (!o.Contains("Error"), o.Trim().Split('\n').Last()); },
        ["launch"] = async (d, a, p) =>
        {
            var s = d.ControlSession;
            if (s != null) { s.Send(Scrcpy.ControlMessages.StartAppMsg(S(p, "pkg"))); return (true, "đã gửi lệnh mở"); }
            var o = await AdbClient.ShellAsync(a, $"monkey -p {AdbClient.ShellQuote(S(p, "pkg"))} -c android.intent.category.LAUNCHER 1 2>&1", 15000);
            return (!Regex.IsMatch(o, "No activities|Error"), o.Trim().Split('\n').Last());
        },
        ["stopapp"] = async (d, a, p) => { await AdbClient.ShellAsync(a, $"am force-stop {AdbClient.ShellQuote(S(p, "pkg"))}", 15000); return (true, "đã dừng"); },
        ["clearapp"] = async (d, a, p) => { var o = await AdbClient.ShellAsync(a, $"pm clear {AdbClient.ShellQuote(S(p, "pkg"))} 2>&1", 30000); return (o.Contains("Success"), o.Trim()); },
        ["reboot"] = async (d, a, p) => { await AdbClient.RunAsync([.. AdbClient.Sel(a), "reboot"], 20000); return (true, "đang khởi động lại"); },
        ["screenshot"] = async (d, a, p) =>
        {
            var png = await AdbClient.ExecOutAsync(a, "screencap -p", 30000);
            if (png.Length < 100 || png[1] != 0x50) throw new IOException("ảnh không hợp lệ");
            var dir = Store.Settings.ScreenshotDir;
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{d.Num:000}_{SafeName(d.Label != "" ? d.Label : d.Model != "" ? d.Model : d.Id)}_{Stamp()}.png");
            await File.WriteAllBytesAsync(file, png);
            return (true, file);
        },
        // USB → WiFi: bật adb tcpip 5555 rồi kết nối qua IP wlan0
        ["tcpip"] = async (d, a, p) =>
        {
            var ip = d.Ip;
            if (ip == "")
            {
                var o = await AdbClient.ShellAsync(a, "ip -f inet addr show wlan0 | grep -m1 inet", 8000);
                var m = Regex.Match(o, @"inet\s+(\d+\.\d+\.\d+\.\d+)");
                ip = m.Success ? m.Groups[1].Value : "";
            }
            if (ip == "") return (false, "Máy chưa kết nối WiFi");
            var target = ip + ":5555";
            if (d.Transports.ContainsKey(target)) return (true, "đã có kết nối WiFi " + target);
            if (d.Transport == "usb")
            {
                // adb tcpip khởi động lại adbd: máy root phải có khoá trong adb_keys trước, nếu không sẽ bị hỏi uỷ quyền lại
                if (d.Root)
                {
                    var k = await AdbClient.PersistAdbKeyAsync(a);
                    if (!k.Ok) return (false, "Chưa chuyển WiFi (tránh mất uỷ quyền): " + k.Msg);
                }
                await AdbClient.RunAsync([.. AdbClient.Sel(a), "tcpip", "5555"], 15000);
                await Task.Delay(2500);
            }
            (int Code, string Out) r = (1, "");
            for (int i = 0; i < 4; i++)
            {
                r = await AdbClient.RunAsync(["connect", target], 10000);
                if (r.Out.Contains("connected to")) break;
                await Task.Delay(1500);
            }
            bool ok = r.Out.Contains("connected to");
            if (ok) ui.Invoke(() => { Store.Meta(d.Id).Wifi = target; if (!Store.State.WifiHistory.Contains(target)) Store.State.WifiHistory.Add(target); Store.Save(); });
            var msg = r.Out;
            if (!ok && Regex.IsMatch(msg, "refused|10061")) msg += "\n→ ROM của máy này chặn ADB qua TCP (hay gặp ở Samsung Android 11+). Hãy bật \"Gỡ lỗi không dây\" trong Tuỳ chọn nhà phát triển rồi dùng \"Ghép nối mã\".";
            return (ok, msg);
        },
        // WiFi → USB: ngắt kết nối WiFi, không tự nối lại nữa. KHÔNG khởi động lại adbd (máy chưa "Luôn cho phép" sẽ hỏi lại).
        ["tousb"] = async (d, a, p) =>
        {
            var wifi = d.Transports.Where(t => t.Value == "wifi").Select(t => t.Key).ToList();
            var usb = d.Transports.FirstOrDefault(t => t.Value == "usb").Key;
            var meta = Store.Meta(d.Id);
            if (wifi.Count == 0 && string.IsNullOrEmpty(meta.Wifi)) return (true, "Đang dùng USB");
            ui.Invoke(() => { meta.Wifi = null; Store.Save(); });
            var serial = usb ?? a;
            string wl = "";
            try { wl = await AdbClient.ShellAsync(serial, "settings get global adb_wifi_enabled 2>/dev/null", 8000); } catch { }
            if (wl.Trim() == "1") { try { await AdbClient.ShellAsync(serial, "settings put global adb_wifi_enabled 0", 8000); } catch { } }
            foreach (var s in wifi) await AdbClient.RunAsync(["disconnect", s.Split("@t")[0]], 8000);
            return (true, usb != null ? "Đã chuyển về USB" : "Đã ngắt WiFi ADB — cắm cáp USB để dùng tiếp");
        },
        // ROOT: bật ADB qua WiFi cố định cổng 5555 (giữ cả sau khi khởi động lại)
        ["rootadbwifi"] = async (d, a, p) =>
        {
            if (!d.Root) return (false, "Máy không có root");
            string o;
            try { o = await AdbClient.ShellAsync(a, "su -c 'setprop persist.adb.tcp.port 5555; setprop service.adb.tcp.port 5555; echo SET; setprop ctl.restart adbd' 2>&1", 20000); } catch (Exception e) { o = "SET " + e.Message; }
            if (!o.Contains("SET")) return (false, o.Trim());
            await Task.Delay(5000);
            if (d.Ip == "") return (true, "Đã đặt cổng 5555 (máy chưa có IP WiFi)");
            var r = await AdbClient.RunAsync(["connect", d.Ip + ":5555"], 10000);
            bool ok = r.Out.Contains("connected to");
            if (ok) ui.Invoke(() => { Store.Meta(d.Id).Wifi = d.Ip + ":5555"; Store.Save(); });
            return (ok, ok ? $"Đã bật ADB WiFi cố định: {d.Ip}:5555" : $"Đã đặt cổng 5555 nhưng không kết nối được ({r.Out}). ROM có thể chặn ADB TCP.");
        },
        ["rootcheck"] = async (d, a, p) =>
        {
            string o;
            try { o = await AdbClient.ShellAsync(a, "su -c 'id' 2>&1", 15000); } catch (Exception e) { o = e.Message; }
            bool ok = o.Contains("uid=0");
            ui.Invoke(() => { d.Root = ok; d.Changed(); });
            return (ok, ok ? "Có quyền root" : "Không có root: " + o.Trim());
        },
        ["pushkey"] = async (d, a, p) =>
        {
            if (!d.Root) return (false, "Máy không có root — hãy tick \"Luôn cho phép từ máy tính này\" một lần trên máy");
            return await AdbClient.PersistAdbKeyAsync(a);
        },
        ["stayawake"] = async (d, a, p) =>
        {
            await AdbClient.ShellAsync(a, $"settings put global stay_on_while_plugged_in {(B(p, "on") ? 7 : 0)}", 10000);
            return (true, B(p, "on") ? "Luôn sáng khi cắm sạc" : "Tắt luôn sáng");
        },
        ["brightness"] = async (d, a, p) =>
        {
            var v = Math.Clamp(int.TryParse(S(p, "value"), out var x) ? x : 40, 0, 255);
            await AdbClient.ShellAsync(a, $"settings put system screen_brightness_mode 0; settings put system screen_brightness {v}", 10000);
            return (true, "Độ sáng " + v);
        },
    };

    /// <summary>Chạy một thao tác trên nhiều máy (song song có giới hạn). Gọi trên luồng giao diện.</summary>
    public BatchTask? RunBatch(string op, IEnumerable<string> ids, Dictionary<string, object?>? p, string title, bool openResults = false)
    {
        if (!Ops.TryGetValue(op, out var fn)) throw new InvalidOperationException("Thao tác không hỗ trợ: " + op);
        var devices = ids.Select(manager.Get).Where(d => d is { Pending: false, ActiveAddr: not null }).Cast<PhoneDevice>().ToList();
        if (devices.Count == 0) return null;
        p ??= [];
        var task = new BatchTask { Op = op, Title = title, Total = devices.Count, OpenResults = openResults };
        TaskUpdated?.Invoke(task);
        Interlocked.Increment(ref RunningTasks);
        var lim = new SemaphoreSlim(Math.Max(1, Store.Settings.MaxParallelJobs));
        _ = Task.Run(async () =>
        {
            await Task.WhenAll(devices.Select(async d =>
            {
                await lim.WaitAsync();
                (bool Ok, string Msg) r;
                try { r = await fn(d, d.ActiveAddr!, p); }
                catch (Exception e) { r = (false, e.Message); }
                finally { lim.Release(); }
                _ = ui.BeginInvoke(() =>
                {
                    task.Done++;
                    if (r.Ok) task.OkCount++; else task.Fail++;
                    task.Results.Add(new TaskResult(d.Id, d.Vid, manager.LabelOf(d), r.Ok, r.Msg.Length > 4000 ? r.Msg[..4000] : r.Msg));
                    task.Changed();
                    TaskUpdated?.Invoke(task);
                });
            }));
            Interlocked.Decrement(ref RunningTasks);
            _ = ui.BeginInvoke(() => { task.Finished = true; task.Changed(); TaskUpdated?.Invoke(task); });
        });
        return task;
    }

    // ---------- WiFi / kết nối ----------
    public async Task<(bool Ok, string Msg)> ConnectAsync(string addr)
    {
        addr = addr.Trim();
        if (addr == "") return (false, "Thiếu địa chỉ");
        if (!Regex.IsMatch(addr, @":\d+$")) addr += ":5555";
        var r = await AdbClient.RunAsync(["connect", addr], 12000);
        bool ok = r.Out.Contains("connected to");
        if (ok && !Store.State.WifiHistory.Contains(addr)) { Store.State.WifiHistory.Add(addr); Store.Save(); }
        return (ok, r.Out);
    }

    public async Task<(bool Ok, string Msg)> PairAsync(string addr, string code)
    {
        var r = await AdbClient.RunAsync(["pair", addr.Trim(), code.Trim()], 20000);
        return (Regex.IsMatch(r.Out, "Successfully paired", RegexOptions.IgnoreCase), r.Out);
    }

    public static List<(string Name, string Address, string Subnet)> LocalSubnets()
    {
        var res = new List<(string, string, string)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || Regex.IsMatch(ni.Name + ni.Description, "vEthernet|VirtualBox|VMware|WSL|Hyper-V|Loopback")) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                var s = ua.Address.ToString();
                if (s.StartsWith("169.254.")) continue;
                res.Add((ni.Name, s, string.Join('.', s.Split('.').Take(3))));
            }
        }
        return res;
    }

    /// <summary>Quét LAN tìm cổng ADB rồi kết nối. subnet dạng "192.168.1".</summary>
    public async Task<(int Found, List<(string Addr, bool Ok, string Msg)> Results)> ScanAsync(string subnet, int port)
    {
        var hosts = Enumerable.Range(1, 254).Select(i => $"{subnet}.{i}").ToList();
        var open = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var lim = new SemaphoreSlim(128);
        await Task.WhenAll(hosts.Select(async h =>
        {
            await lim.WaitAsync();
            try
            {
                using var c = new TcpClient();
                using var cts = new CancellationTokenSource(500);
                await c.ConnectAsync(h, port, cts.Token);
                open.Add($"{h}:{port}");
            }
            catch { }
            finally { lim.Release(); }
        }));
        var results = new List<(string, bool, string)>();
        foreach (var addr in open.OrderBy(x => x))
        {
            if (manager.Devices.Any(d => d.Transports.ContainsKey(addr))) { results.Add((addr, true, "đã kết nối sẵn")); continue; }
            var r = await ConnectAsync(addr);
            results.Add((addr, r.Ok, r.Msg));
        }
        return (open.Count, results);
    }

    // ---------- scrcpy gốc ----------
    public (bool Ok, string Msg) LaunchScrcpy(string? addr, IEnumerable<string> extra, string? title = null)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "scrcpy", "scrcpy.exe");
        if (!File.Exists(exe)) return (false, "Không thấy scrcpy.exe");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        if (addr != null) { psi.ArgumentList.Add("-s"); psi.ArgumentList.Add(addr.Split("@t")[0]); } // scrcpy.exe chỉ nhận serial
        foreach (var a in extra) psi.ArgumentList.Add(a);
        if (title != null) { psi.ArgumentList.Add("--window-title"); psi.ArgumentList.Add(title); }
        psi.Environment["ADB"] = AdbClient.AdbPath;
        try
        {
            var p = Process.Start(psi)!;
            p.EnableRaisingEvents = true;
            lock (ScrcpyChildren) ScrcpyChildren.Add(p);
            p.Exited += (_, _) => { lock (ScrcpyChildren) ScrcpyChildren.Remove(p); };
            return (true, "Đã mở scrcpy " + string.Join(' ', psi.ArgumentList));
        }
        catch (Exception e) { return (false, e.Message); }
    }

    // ---------- tệp / ứng dụng ----------
    public sealed record FileItem(string Name, bool Dir, bool Link, long Size, DateTime? MTime);

    public static async Task<(string Path, List<FileItem> Items)> ListFilesAsync(string addr, string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) dir = "/sdcard/";
        var o = await AdbClient.ShellAsync(addr, $"cd {AdbClient.ShellQuote(dir)} 2>&1 && stat -c '%F|%s|%Y|%n' * .[!.]* 2>/dev/null", 15000);
        var first = o.Split('\n').FirstOrDefault() ?? "";
        if (Regex.IsMatch(first, "No such file|Permission denied|Not a directory")) throw new IOException(o.Trim());
        var items = new List<FileItem>();
        foreach (var line in o.Split('\n'))
        {
            var parts = line.Split('|');
            if (parts.Length < 4) continue;
            var name = string.Join('|', parts.Skip(3));
            if (name is "" or "*" or ".[!.]*") continue;
            items.Add(new FileItem(name, parts[0].Contains("directory") || parts[0].Contains("symbolic link"), parts[0].Contains("symbolic link"),
                long.TryParse(parts[1], out var sz) ? sz : 0, long.TryParse(parts[2], out var mt) && mt > 0 ? DateTimeOffset.FromUnixTimeSeconds(mt).LocalDateTime : null));
        }
        items = items.OrderByDescending(x => x.Dir).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return (dir.TrimEnd('/') + "/", items);
    }

    public static Task<string> DeletePathAsync(string addr, string p)
    {
        if (string.IsNullOrEmpty(p) || p == "/" || !(p.StartsWith("/sdcard/") || p.StartsWith("/storage/") || p.StartsWith("/data/local/tmp/")))
            throw new InvalidOperationException("Chỉ cho phép xoá trong /sdcard, /storage hoặc /data/local/tmp");
        return AdbClient.ShellAsync(addr, $"rm -rf {AdbClient.ShellQuote(p)} 2>&1", 30000);
    }

    public static async Task<List<string>> ListAppsAsync(string addr)
    {
        var o = await AdbClient.ShellAsync(addr, "pm list packages -3 2>/dev/null", 20000);
        return o.Split('\n').Select(l => l.Replace("package:", "").Trim()).Where(x => x != "").OrderBy(x => x).ToList();
    }
}
