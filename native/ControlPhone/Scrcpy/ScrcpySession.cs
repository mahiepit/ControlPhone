using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using ControlPhone.Adb;

namespace ControlPhone.Scrcpy;

public sealed class ScrcpyOptions
{
    public int MaxSize { get; init; } = 480;
    public int BitRate { get; init; } = 600_000;
    public int MaxFps { get; init; } = 15;
    public bool PowerOn { get; init; } = true;
    public bool StayAwake { get; init; }
    /// <summary>false = phiên "chỉ điều khiển" (không mã hoá/gửi hình): máy tạm dừng xem / đang phóng to.</summary>
    public bool Video { get; init; } = true;
}

/// <summary>
/// Một phiên scrcpy-server trên thiết bị: 1 socket video (H.264) + 1 socket điều khiển.
/// Gói video được đọc trên một luồng riêng và đẩy qua <see cref="Packet"/> ngay trên luồng đó
/// (bộ giải mã chạy luôn trên luồng này → mỗi máy giải mã song song, không chặn giao diện).
/// </summary>
public sealed class ScrcpySession : IDisposable
{
    const string ServerVersion = "5.0";
    const string ServerRemote = "/data/local/tmp/controlphone-server.jar";
    static readonly string ServerLocal = Path.Combine(AppContext.BaseDirectory, "scrcpy-server");
    static readonly long ServerSize = File.Exists(ServerLocal) ? new FileInfo(ServerLocal).Length : 0;
    static readonly ConcurrentDictionary<string, Task> Pushes = new();

    public string Addr { get; }
    public ScrcpyOptions Options { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string DeviceName { get; private set; } = "";
    public bool Running { get; private set; }
    public bool Closed { get; private set; }
    public long BytesReceived;

    /// <summary>(isConfig, isKey, data) — gọi trên luồng đọc video.</summary>
    public event Action<bool, bool, byte[]>? Packet;
    public event Action<int, int>? SizeChanged;
    public event Action<string>? ClipboardReceived;
    public event Action<string>? Ended;

    Socket? shellSock, videoSock, controlSock;
    NetworkStream? control;
    readonly object controlLock = new();
    readonly Queue<string> logTail = new();
    long lastResetAt;

    public ScrcpySession(string addr, ScrcpyOptions opts)
    {
        Addr = addr;
        Options = opts;
    }

    /// <summary>Đẩy scrcpy-server lên máy (1 lần mỗi máy, bỏ qua nếu đã đúng kích thước).</summary>
    static Task EnsureServerPushedAsync(string addr) => Pushes.GetOrAdd(addr, a => Task.Run(async () =>
    {
        long size = -1;
        try
        {
            var o = await AdbClient.ShellAsync(a, $"rm -f /data/local/tmp/controlphone-*.jar; stat -c %s {ServerRemote} 2>/dev/null", 8000);
            long.TryParse(o.Trim(), out size);
        }
        catch { /* bỏ qua */ }
        if (size == ServerSize) return;
        var r = await AdbClient.RunAsync([.. AdbClient.Sel(a), "push", ServerLocal, ServerRemote], 120000);
        if (r.Code != 0) { Pushes.TryRemove(a, out _); throw new IOException("Không push được scrcpy-server: " + r.Out); }
    }));

    public static void ForgetPushed(string addr) => Pushes.TryRemove(addr, out _);

    public async Task StartAsync(CancellationToken ct = default)
    {
        await EnsureServerPushedAsync(Addr).ConfigureAwait(false);
        if (Closed) throw new ObjectDisposedException("session");
        var scid = Random.Shared.Next(0, int.MaxValue).ToString("x8");
        var o = Options;
        string[] videoArgs = o.Video
            ? ["video_codec=h264", $"max_size={o.MaxSize}", $"video_bit_rate={o.BitRate}", $"max_fps={o.MaxFps}"]
            : ["video=false", "send_device_meta=false"];
        string[] args =
        [
            ServerVersion, $"scid={scid}", "log_level=info", "audio=false", .. videoArgs,
            "tunnel_forward=true", "control=true", "clipboard_autosync=false",
            $"power_on={(o.PowerOn ? "true" : "false")}", $"stay_awake={(o.StayAwake ? "true" : "false")}",
            "cleanup=true", "downsize_on_error=true",
            // khung I thưa (20 giây): máy đứng yên gần như không tốn băng thông; cần hình ngay thì gửi RESET_VIDEO
            "video_codec_options=i-frame-interval:int=20",
        ];
        // scrcpy-server tự xoá file jar khi chạy (cleanup) → mỗi phiên dùng một bản sao riêng
        var jar = $"/data/local/tmp/controlphone-{scid}.jar";
        var cmd = $"cp {ServerRemote} {jar} && CLASSPATH={jar} app_process / com.genymobile.scrcpy.Server {string.Join(' ', args)}";
        shellSock = await AdbClient.OpenServiceAsync(Addr, "shell:" + cmd, 15000, ct).ConfigureAwait(false);
        _ = Task.Run(ReadShellLog);

        var socketName = "localabstract:scrcpy_" + scid;
        // socket đầu tiên: thử lại tới khi server sẵn sàng (byte "dummy" đầu tiên)
        Socket? first = null;
        for (int i = 0; i < 100 && !Closed && first == null; i++)
        {
            try
            {
                var s = await AdbClient.OpenServiceAsync(Addr, socketName, 4000, ct).ConfigureAwait(false);
                var one = new byte[1];
                s.ReceiveTimeout = 3000;
                int n = 0;
                try { n = s.Receive(one); } catch (SocketException) { }
                if (n == 1) { s.ReceiveTimeout = 0; first = s; break; }
                s.Dispose();
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* chưa sẵn sàng */ }
            await Task.Delay(i < 20 ? 100 : 250, ct).ConfigureAwait(false);
        }
        if (first == null)
        {
            string tail;
            lock (logTail) tail = string.Join(" | ", logTail.TakeLast(3));
            Close("không kết nối được video");
            throw new IOException("Không kết nối được scrcpy" + (tail.Length > 0 ? ": " + tail : ""));
        }
        if (!o.Video)
        {
            // không có hình: socket đầu tiên chính là socket điều khiển; toạ độ chạm = toạ độ thật của màn hình
            controlSock = first;
            control = new NetworkStream(controlSock, ownsSocket: false);
            await RefreshScreenSizeAsync().ConfigureAwait(false);
            Running = true;
            new Thread(ControlLoop) { IsBackground = true, Name = "control " + Addr }.Start();
            return;
        }
        videoSock = first;
        controlSock = await AdbClient.OpenServiceAsync(Addr, socketName, 8000, ct).ConfigureAwait(false);
        control = new NetworkStream(controlSock, ownsSocket: false);
        Running = true;
        new Thread(VideoLoop) { IsBackground = true, Name = "video " + Addr, Priority = ThreadPriority.AboveNormal }.Start();
        new Thread(ControlLoop) { IsBackground = true, Name = "control " + Addr }.Start();
    }

    /// <summary>Kích thước màn hình hiện tại (đã tính xoay) — dùng cho phiên chỉ điều khiển.</summary>
    public async Task RefreshScreenSizeAsync()
    {
        try
        {
            var o = await AdbClient.ShellAsync(Addr, "dumpsys window displays | grep -m1 -oE 'cur=[0-9]+x[0-9]+'; wm size", 8000).ConfigureAwait(false);
            var m = System.Text.RegularExpressions.Regex.Match(o, @"cur=(\d+)x(\d+)");
            if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(o, @"Override size:\s*(\d+)x(\d+)");
            if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(o, @"Physical size:\s*(\d+)x(\d+)");
            if (m.Success) { Width = int.Parse(m.Groups[1].Value); Height = int.Parse(m.Groups[2].Value); }
        }
        catch { /* giữ kích thước cũ */ }
        if (Width == 0) { Width = 1080; Height = 2220; }
    }

    void ReadShellLog()
    {
        try
        {
            using var s = new NetworkStream(shellSock!, ownsSocket: false);
            using var r = new StreamReader(s, Encoding.UTF8);
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                lock (logTail) { logTail.Enqueue(line); while (logTail.Count > 30) logTail.Dequeue(); }
            }
        }
        catch { /* đóng */ }
        Close("server đã thoát");
    }

    static void ReadExact(Stream s, Span<byte> buf)
    {
        while (buf.Length > 0)
        {
            int n = s.Read(buf);
            if (n == 0) throw new EndOfStreamException();
            buf = buf[n..];
        }
    }

    void VideoLoop()
    {
        try
        {
            using var s = new BufferedStream(new NetworkStream(videoSock!, ownsSocket: false), 256 * 1024);
            Span<byte> head = stackalloc byte[64];
            ReadExact(s, head);
            DeviceName = Encoding.UTF8.GetString(head).Split('\0')[0];
            ReadExact(s, head[..4]);
            uint codec = BinaryPrimitives.ReadUInt32BigEndian(head);
            if (codec is 0 or 1) { Close("thiết bị từ chối luồng video"); return; }
            Span<byte> h12 = stackalloc byte[12];
            while (!Closed)
            {
                ReadExact(s, h12);
                uint hi = BinaryPrimitives.ReadUInt32BigEndian(h12);
                if ((hi & 0x80000000) != 0)
                {
                    // session meta: kích thước khung hình mới (xoay màn hình, reset...)
                    Width = (int)BinaryPrimitives.ReadUInt32BigEndian(h12[4..]);
                    Height = (int)BinaryPrimitives.ReadUInt32BigEndian(h12[8..]);
                    SizeChanged?.Invoke(Width, Height);
                    continue;
                }
                int size = (int)BinaryPrimitives.ReadUInt32BigEndian(h12[8..]);
                var data = new byte[size];
                ReadExact(s, data);
                Interlocked.Add(ref BytesReceived, size + 12);
                Packet?.Invoke((hi & 0x40000000) != 0, (hi & 0x20000000) != 0, data);
            }
        }
        catch (Exception) { /* đóng */ }
        Close("video đóng");
    }

    void ControlLoop()
    {
        try
        {
            using var s = new BufferedStream(new NetworkStream(controlSock!, ownsSocket: false));
            Span<byte> h = stackalloc byte[8];
            while (!Closed)
            {
                int type = s.ReadByte();
                if (type < 0) break;
                if (type == 0) // clipboard
                {
                    ReadExact(s, h[..4]);
                    var buf = new byte[BinaryPrimitives.ReadUInt32BigEndian(h)];
                    ReadExact(s, buf);
                    ClipboardReceived?.Invoke(Encoding.UTF8.GetString(buf));
                }
                else if (type == 1) ReadExact(s, h); // ack clipboard (8 byte)
                else if (type == 2) // uhid output
                {
                    ReadExact(s, h[..4]);
                    var buf = new byte[BinaryPrimitives.ReadUInt16BigEndian(h[2..])];
                    ReadExact(s, buf);
                }
                else break;
            }
        }
        catch { /* đóng */ }
    }

    public bool Send(byte[] msg)
    {
        if (!Running || control == null) return false;
        try { lock (controlLock) control.Write(msg); return true; }
        catch { Close("control đóng"); return false; }
    }

    public void RequestKeyFrame()
    {
        if (!Options.Video) return;
        long now = Environment.TickCount64;
        if (now - lastResetAt < 800) return;
        if (Send(ControlMessages.Empty(ControlMessages.ResetVideo))) lastResetAt = now;
    }

    /// <summary>Chạm theo toạ độ chuẩn hoá 0..1.</summary>
    public bool Touch(byte action, double nx, double ny, long pointerId = ControlMessages.PointerIdGenericFinger)
    {
        if (Width == 0 || Height == 0) return false;
        int x = (int)Math.Round(Math.Clamp(nx, 0, 1) * (Width - 1));
        int y = (int)Math.Round(Math.Clamp(ny, 0, 1) * (Height - 1));
        return Send(ControlMessages.Touch(action, pointerId, x, y, Width, Height, action == ControlMessages.ActionUp ? 0 : 1));
    }

    public bool Scroll(double nx, double ny, float h, float v)
    {
        if (Width == 0 || Height == 0) return false;
        return Send(ControlMessages.Scroll((int)(nx * (Width - 1)), (int)(ny * (Height - 1)), Width, Height, h, v));
    }

    public void KeyPress(int code, int meta = 0)
    {
        Send(ControlMessages.Keycode(0, code, 0, meta));
        Send(ControlMessages.Keycode(1, code, 0, meta));
    }

    /// <summary>Gõ văn bản: ASCII ngắn gõ trực tiếp; còn lại (tiếng Việt, emoji, dài) dán qua clipboard.</summary>
    public void TypeText(string s, bool paste = false)
    {
        if (string.IsNullOrEmpty(s)) return;
        bool ascii = s.All(c => c >= 0x20 && c <= 0x7e);
        if (ascii && !paste && s.Length <= 300) Send(ControlMessages.Text(s));
        else Send(ControlMessages.SetClipboardMsg(s, paste: true));
    }

    int closedFlag;
    void Close(string reason)
    {
        if (Interlocked.Exchange(ref closedFlag, 1) != 0) return;
        Closed = true;
        Running = false;
        foreach (var s in new[] { videoSock, controlSock, shellSock }) { try { s?.Dispose(); } catch { } }
        Ended?.Invoke(reason);
    }

    public void Dispose() => Close("dừng");
}
