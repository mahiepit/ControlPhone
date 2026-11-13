using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using ControlPhone.Adb;
using ControlPhone.Video;

namespace ControlPhone.Recovery;

/// <summary>
/// Máy đang ở recovery (TWRP...): không có Android runtime nên scrcpy-server không chạy được. Đẩy helper native
/// (native/recovery/cprec.c) lên /tmp, chạy qua adb exec: — nó đọc framebuffer bằng mmap rồi gửi khung hình về (chỉ khi
/// màn hình đổi), và bơm chạm / phím vào /dev/input. Board không màn hình không có thiết bị cảm ứng → helper tạo cảm ứng ảo
/// (uinput); TWRP 3.7 tự nạp lại thiết bị nhập khi có thiết bị mới.
/// </summary>
public sealed class RecoverySession : IDisposable
{
    public string Addr { get; }
    /// <summary>Khung cho ô lưới và cho màn hình lớn (mỗi nơi tiêu thụ cờ "khung mới" riêng).</summary>
    public FrameBuffer Output { get; } = new();
    public FrameBuffer HqOutput { get; } = new();
    public int ScreenW { get; private set; }
    public int ScreenH { get; private set; }
    /// <summary>0 chưa có cảm ứng, 1 cảm ứng thật của máy, 2 cảm ứng ảo (uinput).</summary>
    public int TouchMode { get; private set; }
    public bool HasKeys { get; private set; }
    /// <summary>"TWRP 3.7.0_9-0" hoặc "Recovery".</summary>
    public string Name { get; private set; } = "Recovery";
    public string Device { get; private set; } = "";
    public volatile bool Running;
    public bool Closed { get; private set; }
    public long Frames;
    public event Action<string>? Ended;
    public event Action? InfoChanged;

    // mã phím Linux (linux/input-event-codes.h)
    public const int KeyPower = 116, KeyVolumeUp = 115, KeyVolumeDown = 114, KeyHome = 102, KeyBack = 158;

    Socket? sock;
    readonly object sendGate = new();
    bool uinputAsked;

    public RecoverySession(string addr) { Addr = addr; }

    static string HelperPath(bool arm64) => Path.Combine(AppContext.BaseDirectory, "recovery", arm64 ? "cprec-arm64" : "cprec-arm");

    public async Task StartAsync(CancellationToken ct = default)
    {
        var info = await AdbClient.ShellAsync(Addr, "echo \"ARCH=$(uname -m)\"; echo \"TWRP=$(getprop ro.twrp.version)\"; echo \"DEV=$(getprop ro.product.device)\"", 10000, ct).ConfigureAwait(false);
        string V(string k) => System.Text.RegularExpressions.Regex.Match(info, k + @"=(\S*)").Groups[1].Value;
        if (V("TWRP") != "") Name = "TWRP " + V("TWRP");
        Device = V("DEV");
        var arch = V("ARCH");
        bool arm64 = arch.Contains("64");
        if (arch != "" && !arm64 && !arch.StartsWith("arm")) throw new IOException($"Recovery trên CPU {arch} chưa được hỗ trợ");
        var local = HelperPath(arm64);
        if (!File.Exists(local)) throw new IOException("Thiếu tệp " + local);
        if (Closed) throw new ObjectDisposedException("session");
        const string remote = "/tmp/cprec";
        var push = await AdbClient.RunAsync([.. AdbClient.Sel(Addr), "push", local, remote], 30000).ConfigureAwait(false);
        if (push.Code != 0) throw new IOException("Không đẩy được helper lên máy: " + push.Out);
        await AdbClient.ShellAsync(Addr, "chmod 755 " + remote, 8000, ct).ConfigureAwait(false);
        if (Closed) throw new ObjectDisposedException("session");
        // exec: = luồng nhị phân hai chiều (không qua pty); cạnh dài tối đa 1600 px, chụp mỗi 120 ms
        sock = await AdbClient.OpenServiceAsync(Addr, $"exec:{remote} stream 1600 120", 15000, ct).ConfigureAwait(false);
        Running = true;
        new Thread(ReadLoop) { IsBackground = true, Name = "recovery " + Addr }.Start();
    }

    void ReadLoop()
    {
        string reason = "recovery đóng kết nối";
        try
        {
            using var s = new BufferedStream(new NetworkStream(sock!, ownsSocket: false), 1 << 16);
            var head = new byte[12];
            byte[] rle = [];
            while (!Closed)
            {
                s.ReadExactly(head);
                var tag = Encoding.ASCII.GetString(head, 0, 4);
                if (tag == "CPI1")
                {
                    ScreenW = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(4));
                    ScreenH = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6));
                    TouchMode = head[8];
                    HasKeys = head[9] != 0;
                    // máy không có cảm ứng (board không màn hình): tạo cảm ứng ảo, 1 lần
                    if (TouchMode == 0 && !uinputAsked) { uinputAsked = true; SendCmd((byte)'U', 0, 0, 0); }
                    InfoChanged?.Invoke();
                    continue;
                }
                if (tag != "CPF1")
                {
                    // helper báo lỗi bằng chữ (stderr đi chung luồng): đọc nốt để ghi lại
                    var rest = new byte[400];
                    int n = 0;
                    try { sock!.ReceiveTimeout = 500; n = s.Read(rest, 0, rest.Length); } catch { }
                    reason = (Encoding.UTF8.GetString(head) + Encoding.UTF8.GetString(rest, 0, n)).Replace("CPREC_ERR", "").Trim();
                    if (reason.Contains("fb:")) reason = "Không đọc được màn hình của recovery này (" + reason + ")";
                    break;
                }
                int w = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(4)), h = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6));
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
                if (w <= 0 || h <= 0 || len <= 0 || len > w * h * 4 || (len & 3) != 0) { reason = "dữ liệu khung hình không hợp lệ"; break; }
                if (rle.Length < len) rle = new byte[len];
                s.ReadExactly(rle, 0, len);
                var px = Output.BeginWrite(w, h);
                if (!Decode(rle, len, px)) { reason = "dữ liệu khung hình không hợp lệ"; break; }
                Output.EndWrite();
                var hq = HqOutput.BeginWrite(w, h);
                Buffer.BlockCopy(px, 0, hq, 0, px.Length);
                HqOutput.EndWrite();
                Interlocked.Increment(ref Frames);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or EndOfStreamException) { }
        Close(reason);
    }

    /// <summary>RLE (n, b, g, r) → BGRA. false nếu dữ liệu không khớp kích thước khung.</summary>
    static bool Decode(byte[] rle, int len, byte[] px)
    {
        int o = 0;
        for (int i = 0; i < len; i += 4)
        {
            int n = rle[i];
            byte b = rle[i + 1], g = rle[i + 2], r = rle[i + 3];
            if (n == 0 || o + n * 4 > px.Length) return false;
            for (; n > 0; n--) { px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = 255; o += 4; }
        }
        return o == px.Length;
    }

    bool SendCmd(byte cmd, byte a, int x, int y)
    {
        var s = sock;
        if (s == null || Closed) return false;
        Span<byte> p = stackalloc byte[8];
        p[0] = cmd; p[1] = a;
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], (ushort)Math.Clamp(x, 0, 65535));
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], (ushort)Math.Clamp(y, 0, 65535));
        try { lock (sendGate) s.Send(p); return true; }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { return false; }
    }

    /// <summary>Chạm 1 ngón. action theo scrcpy (0 xuống, 1 nhả, 2 di chuyển); toạ độ chuẩn hoá 0..1.</summary>
    public bool Touch(byte action, double nx, double ny)
    {
        byte a = action switch { Scrcpy.ControlMessages.ActionDown => 0, Scrcpy.ControlMessages.ActionUp => 2, _ => 1 };
        return SendCmd((byte)'T', a, (int)Math.Round(Math.Clamp(nx, 0, 1) * 65535), (int)Math.Round(Math.Clamp(ny, 0, 1) * 65535));
    }

    public bool Key(int linuxCode, bool down) => SendCmd((byte)'K', down ? (byte)1 : (byte)0, linuxCode, 0);

    public void KeyPress(int linuxCode) { Key(linuxCode, true); Key(linuxCode, false); }

    /// <summary>Vuốt thẳng từ điểm này tới điểm kia (vd. thanh "Swipe to Unlock" của TWRP).</summary>
    public async Task SwipeAsync(double x1, double y1, double x2, double y2, int steps = 16, int stepMs = 20)
    {
        Touch(Scrcpy.ControlMessages.ActionDown, x1, y1);
        for (int i = 1; i <= steps; i++)
        {
            await Task.Delay(stepMs).ConfigureAwait(false);
            Touch(Scrcpy.ControlMessages.ActionMove, x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
        }
        await Task.Delay(40).ConfigureAwait(false);
        Touch(Scrcpy.ControlMessages.ActionUp, x2, y2);
    }

    void Close(string reason)
    {
        lock (sendGate)
        {
            if (Closed) return;
            Closed = true;
        }
        Running = false;
        try { sock?.Dispose(); } catch { }
        Ended?.Invoke(reason);
    }

    public void Dispose() => Close("dừng");
}
