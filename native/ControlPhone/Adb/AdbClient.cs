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

    /// <summary>Chạy adb.exe (push / start-server / kill-server ...). Không bao giờ ném lỗi.</summary>
    public static async Task<(int Code, string Out)> RunAsync(IEnumerable<string> args, int timeoutMs = 60000)
    {
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
                onError?.Invoke(e.Message);
                if (ct.IsCancellationRequested) return;
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
