using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace ControlPhone.Adb;

/// <summary>Một dòng trong danh sách thiết bị của adb (host:track-devices-l).</summary>
public sealed record AdbDevice(string Serial, string State, string TransportId)
{
    /// <summary>Địa chỉ dùng để mở kết nối: serial, hoặc "serial@t&lt;id&gt;" khi nhiều máy trùng serial.</summary>
    public string Addr { get; init; } = Serial;
}

/// <summary>
/// Client ADB tối giản: nói chuyện trực tiếp với adb server (127.0.0.1:5037) qua socket —
/// không sinh tiến trình adb.exe cho từng lệnh (chỉ dùng adb.exe cho push / start-server).
/// </summary>
public static class AdbClient
{
    public static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_PORT"), out var p) ? p : 5037;
    public static readonly string AdbPath = ResolveAdbPath();

    static string ResolveAdbPath()
    {
        var env = Environment.GetEnvironmentVariable("ADB");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        // ưu tiên adb trong PATH (tránh 2 phiên bản adb server đánh nhau với công cụ khác)
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var f = Path.Combine(dir.Trim(), "adb.exe"); if (File.Exists(f)) return f; } catch { /* bỏ qua */ }
        }
        var bundled = Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe");
        return File.Exists(bundled) ? bundled : "adb.exe";
    }

    static (string serial, string? tid) ParseAddr(string addr)
    {
        var i = addr.LastIndexOf("@t", StringComparison.Ordinal);
        if (i > 0 && int.TryParse(addr.AsSpan(i + 2), out _)) return (addr[..i], addr[(i + 2)..]);
        return (addr, null);
    }

    /// <summary>Tham số chọn máy cho adb.exe: -s serial hoặc -t transportId.</summary>
    public static string[] Sel(string addr)
    {
        var (serial, tid) = ParseAddr(addr);
        return tid != null ? ["-t", tid] : ["-s", serial];
    }

    static byte[] Encode(string cmd)
    {
        var body = Encoding.UTF8.GetBytes(cmd);
        return [.. Encoding.ASCII.GetBytes(body.Length.ToString("x4")), .. body];
    }

    static async Task ReadExactAsync(Stream s, byte[] buf, int len, CancellationToken ct)
    {
        int off = 0;
        while (off < len)
        {
            int n = await s.ReadAsync(buf.AsMemory(off, len - off), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("adb đóng kết nối");
            off += n;
        }
    }

    static async Task ExpectOkayAsync(Stream s, CancellationToken ct)
    {
        var st = new byte[4];
        await ReadExactAsync(s, st, 4, ct).ConfigureAwait(false);
        var status = Encoding.ASCII.GetString(st);
        if (status == "OKAY") return;
        if (status == "FAIL")
        {
            await ReadExactAsync(s, st, 4, ct).ConfigureAwait(false);
            int len = Convert.ToInt32(Encoding.ASCII.GetString(st), 16);
            var msg = new byte[len];
            await ReadExactAsync(s, msg, len, ct).ConfigureAwait(false);
            throw new IOException(Encoding.UTF8.GetString(msg));
        }
        throw new IOException("Phản hồi adb không hợp lệ: " + status);
    }

    /// <summary>
    /// Mở một service ADB. serial = null cho lệnh host:*. Trả về socket đã được adb chấp nhận
    /// (sẵn sàng đọc/ghi dữ liệu của service).
    /// </summary>
    public static async Task<Socket> OpenServiceAsync(string? addr, string service, int timeoutMs = 10000, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await sock.ConnectAsync("127.0.0.1", Port, cts.Token).ConfigureAwait(false);
            var s = new NetworkStream(sock, ownsSocket: false);
            if (addr != null)
            {
                var (serial, tid) = ParseAddr(addr);
                await s.WriteAsync(Encode(tid != null ? $"host:transport-id:{tid}" : $"host:transport:{serial}"), cts.Token).ConfigureAwait(false);
                await ExpectOkayAsync(s, cts.Token).ConfigureAwait(false);
            }
            await s.WriteAsync(Encode(service), cts.Token).ConfigureAwait(false);
            await ExpectOkayAsync(s, cts.Token).ConfigureAwait(false);
            return sock;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sock.Dispose();
            throw new TimeoutException("adb timeout: " + service);
        }
        catch
        {
            sock.Dispose();
            throw;
        }
    }

    /// <summary>Chạy lệnh shell, trả về toàn bộ đầu ra (stdout).</summary>
    public static async Task<string> ShellAsync(string addr, string cmd, int timeoutMs = 15000, CancellationToken ct = default)
    {
        using var sock = await OpenServiceAsync(addr, "shell:" + cmd, timeoutMs, ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        using var s = new NetworkStream(sock, ownsSocket: false);
        using var ms = new MemoryStream();
        try { await s.CopyToAsync(ms, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Hết thời gian chờ lệnh"); }
        catch (IOException) { /* đóng phía máy: lấy phần đã nhận */ }
        return Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n");
    }

    /// <summary>exec: luồng nhị phân sạch (screencap, cat tệp). Trả về toàn bộ byte.</summary>
    public static async Task<byte[]> ExecOutAsync(string addr, string cmd, int timeoutMs = 30000, CancellationToken ct = default)
    {
        using var sock = await OpenServiceAsync(addr, "exec:" + cmd, timeoutMs, ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        using var s = new NetworkStream(sock, ownsSocket: false);
        using var ms = new MemoryStream();
        try { await s.CopyToAsync(ms, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Hết thời gian chờ lệnh"); }
        catch (IOException) { }
        return ms.ToArray();
    }

    /// <summary>exec: chép thẳng ra tệp (tải tệp lớn từ máy về).</summary>
    public static async Task ExecToFileAsync(string addr, string cmd, string file, CancellationToken ct = default)
    {
        using var sock = await OpenServiceAsync(addr, "exec:" + cmd, 15000, ct).ConfigureAwait(false);
        using var s = new NetworkStream(sock, ownsSocket: false);
        await using var fs = File.Create(file);
        try { await s.CopyToAsync(fs, ct).ConfigureAwait(false); } catch (IOException) { }
    }

    public static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // Đang thoát: không chạy thêm adb.exe nào (mọi lệnh adb.exe đều tự BẬT LẠI adb server nếu nó đang tắt)
    static volatile bool exiting;

    /// <summary>Chạy adb.exe (push / start-server / kill-server ...). Không bao giờ ném lỗi.</summary>
    public static async Task<(int Code, string Out)> RunAsync(IEnumerable<string> args, int timeoutMs = 60000)
    {
        var argList = args.ToList();
        if (exiting && argList.FirstOrDefault() != "kill-server") return (1, "ControlPhone is exiting");
        args = argList;
        var psi = new ProcessStartInfo(AdbPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { /* bỏ qua */ } return (-1, "timeout"); }
            return (p.ExitCode, ((await outTask) + (await errTask)).Trim());
        }
        catch (Exception e) { return (-1, e.Message); }
    }

    public static Task<(int Code, string Out)> StartServerAsync() => RunAsync(["start-server"], 20000);

    // ---------- khoá uỷ quyền ----------
    /// <summary>Khoá công khai mà adb server này dùng để uỷ quyền (RSA).</summary>
    public static string PubKeyPath()
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var p in new[] { Environment.GetEnvironmentVariable("ANDROID_VENDOR_KEYS"), Path.Combine(home, ".android", "adbkey.pub") })
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
        return "";
    }

    /// <summary>
    /// Cấy khoá công khai của PC vào /data/misc/adb/adb_keys (CẦN ROOT) để máy luôn tin máy tính này —
    /// như tick "Luôn cho phép", nhưng không cần màn hình và còn sau khi adbd/khởi động lại. Không khởi động lại adbd.
    /// Gộp thêm (không xoá khoá PC khác đã có).
    /// </summary>
    public static async Task<(bool Ok, string Msg)> PersistAdbKeyAsync(string addr)
    {
        var pub = PubKeyPath();
        if (pub == "") return (false, "Không thấy khoá adbkey.pub của máy tính");
        var key = File.ReadAllText(pub).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        var frag = key.Substring(40, 40); // đoạn giữa để nhận diện
        const string tmp = "/data/local/tmp/.cp_adbkey.pub";
        var push = await RunAsync([.. Sel(addr), "push", pub, tmp], 15000).ConfigureAwait(false);
        if (push.Code != 0) return (false, push.Out);
        var sh = "su -c 'mkdir -p /data/misc/adb; touch /data/misc/adb/adb_keys; "
               + $"grep -qF {ShellQuote(frag)} /data/misc/adb/adb_keys || cat {tmp} >> /data/misc/adb/adb_keys; "
               + "chmod 640 /data/misc/adb/adb_keys; chown system:shell /data/misc/adb/adb_keys; restorecon /data/misc/adb/adb_keys 2>/dev/null; echo CP_OK'";
        string o;
        try { o = await ShellAsync(addr, sh, 15000).ConfigureAwait(false); } catch (Exception e) { o = e.Message; }
        if (!o.Contains("CP_OK")) return (false, "Cần root để ghi khoá: " + o.Trim()[..Math.Min(200, o.Trim().Length)]);
        string chk = "";
        try { chk = await ShellAsync(addr, "su -c 'cat /data/misc/adb/adb_keys'", 10000).ConfigureAwait(false); } catch { }
        return chk.Contains(frag)
            ? (true, "Đã cấy khoá — máy này sẽ luôn tin máy tính, không hỏi uỷ quyền lại")
            : (false, "Ghi khoá không thành công");
    }

    // ---------- thoát ----------
    static List<string[]> NetstatTcp()
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p tcp") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return o.Split('\n').Select(l => l.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    .Where(f => f.Length >= 5 && f[0] == "TCP").ToList();
        }
        catch { return []; }
    }

    static string ProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName + ".exe"; } catch { return "PID " + pid; }
    }

    /// <summary>
    /// Thoát chương trình: tắt adb server, TRỪ KHI chương trình khác đang kết nối vào nó (vd. Xiaowei).
    /// Không dựa vào "ai đã khởi động adb" vì mọi lệnh adb đều tự bật lại server khi nó đang tắt.
    /// </summary>
    public static async Task<string> StopServerIfUnusedAsync()
    {
        exiting = true;
        if (Environment.GetEnvironmentVariable("CP_KEEP_ADB") != null) return "CP_KEEP_ADB set - keeping adb running";
        var port = ":" + Port;
        int myPid = Environment.ProcessId;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var rows = NetstatTcp();
            var listen = rows.FirstOrDefault(f => f[1].EndsWith(port) && (f[1].StartsWith("127.0.0.1") || f[1].StartsWith("0.0.0.0")) && f[3] == "LISTENING");
            if (listen == null) return attempt > 0 ? $"adb stopped (restarted {attempt}x, stopped again)" : "adb already stopped";
            int adbPid = int.Parse(listen[4]);
            var others = rows.Where(f => f[2] == "127.0.0.1" + port && f[3] == "ESTABLISHED")
                             .Select(f => int.TryParse(f[4], out var x) ? x : 0)
                             .Where(x => x != 0 && x != myPid && x != adbPid).Distinct().ToList();
            if (others.Count > 0) return $"adb is in use by {string.Join(", ", others.Select(ProcessName))} - keeping it running";
            await RunAsync(["kill-server"], 8000).ConfigureAwait(false);
            await Task.Delay(1200).ConfigureAwait(false);
        }
        return "adb keeps restarting - left running";
    }

    /// <summary>
    /// Theo dõi danh sách thiết bị theo thời gian thực (host:track-devices-l). Tự kết nối lại / bật adb server.
    /// Nhiều máy trùng serial (ROM nhân bản) → địa chỉ "serial@t&lt;id&gt;".
    /// </summary>
    public static async Task TrackDevicesAsync(Action<IReadOnlyList<AdbDevice>> onChange, Action<string>? onError, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var sock = await OpenServiceAsync(null, "host:track-devices-l", 8000, ct).ConfigureAwait(false);
                using var s = new NetworkStream(sock, ownsSocket: false);
                var head = new byte[4];
                while (!ct.IsCancellationRequested)
                {
                    await ReadExactAsync(s, head, 4, ct).ConfigureAwait(false);
                    int len = Convert.ToInt32(Encoding.ASCII.GetString(head), 16);
                    var body = new byte[len];
                    if (len > 0) await ReadExactAsync(s, body, len, ct).ConfigureAwait(false);
                    onChange(ParseDeviceList(Encoding.UTF8.GetString(body)));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                if (ct.IsCancellationRequested || exiting) return; // đang thoát: KHÔNG bật lại adb server
                onError?.Invoke(e.Message);
                await StartServerAsync().ConfigureAwait(false); // adb server có thể chưa chạy
            }
            try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    static List<AdbDevice> ParseDeviceList(string payload)
    {
        var raw = new List<AdbDevice>();
        foreach (var line in payload.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var tid = parts.FirstOrDefault(x => x.StartsWith("transport_id:"))?["transport_id:".Length..] ?? "";
            raw.Add(new AdbDevice(parts[0], parts.Length > 1 ? parts[1] : "", tid));
        }
        var dup = raw.GroupBy(x => x.Serial).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return raw.Select(x => dup.Contains(x.Serial) && x.TransportId != "" ? x with { Addr = $"{x.Serial}@t{x.TransportId}" } : x).ToList();
    }

    public static bool IsWifiSerial(string addr)
    {
        var (serial, _) = ParseAddr(addr);
        return System.Text.RegularExpressions.Regex.IsMatch(serial, @":\d+$") || serial.Contains("._adb-tls-connect._tcp") || serial.StartsWith("adb-");
    }
}
