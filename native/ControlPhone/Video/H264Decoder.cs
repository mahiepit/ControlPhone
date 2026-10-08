namespace ControlPhone.Video;

/// <summary>
/// Khung hình BGRA mới nhất của một luồng, bộ đệm kép: luồng giải mã ghi vào "back" rồi đổi chỗ;
/// giao diện chỉ chép khi có khung mới (<see cref="TryTake"/>). Không bao giờ chặn luồng giải mã lâu.
/// </summary>
public sealed class FrameBuffer
{
    readonly object gate = new();
    byte[] front = [], back = [];
    int frontW, frontH, backW, backH;
    bool dirty;
    public long Frames;

    internal byte[] BeginWrite(int w, int h)
    {
        int need = w * h * 4;
        if (back.Length != need) back = new byte[need];
        backW = w; backH = h;
        return back;
    }

    internal void EndWrite()
    {
        lock (gate)
        {
            (front, back) = (back, front);
            (frontW, frontH) = (backW, backH);
            dirty = true;
        }
        Interlocked.Increment(ref Frames);
    }

    /// <summary>Nếu có khung mới: gọi copy(pixels, w, h) (đang giữ khoá — chỉ chép, không làm việc nặng).</summary>
    public bool TryTake(Action<byte[], int, int> copy)
    {
        lock (gate)
        {
            if (!dirty) return false;
            dirty = false;
            copy(front, frontW, frontH);
            return true;
        }
    }

    public void MarkDirty() { lock (gate) dirty = front.Length > 0; }
}

/// <summary>Giải mã H.264 bằng FFmpeg (CPU, đa luồng tuỳ chọn), xuất BGRA vào <see cref="FrameBuffer"/>.</summary>
public sealed unsafe class H264Decoder : IDisposable
{
    void* codec, ctx, pkt, frame;
    byte[]? config;
    bool waitKey = true;
    public FrameBuffer Output { get; } = new();
    public string? Error { get; private set; }
    public event Action? NeedKeyFrame;

    public H264Decoder(int threads = 1)
    {
        codec = Ffmpeg.avcodec_find_decoder(Ffmpeg.CodecIdH264);
        if (codec == null) throw new InvalidOperationException("FFmpeg không có bộ giải mã H.264");
        Open(threads);
        pkt = Ffmpeg.av_packet_alloc();
        frame = Ffmpeg.av_frame_alloc();
    }

    void Open(int threads)
    {
        ctx = Ffmpeg.avcodec_alloc_context3(codec);
        Ffmpeg.av_opt_set(ctx, "threads", threads.ToString(), Ffmpeg.OptSearchChildren);
        Ffmpeg.av_opt_set(ctx, "flags", "+low_delay", Ffmpeg.OptSearchChildren);
        Ffmpeg.av_opt_set(ctx, "flags2", "+fast", Ffmpeg.OptSearchChildren);
        int r = Ffmpeg.avcodec_open2(ctx, codec, null);
        if (r < 0) throw new InvalidOperationException("avcodec_open2 lỗi " + r);
    }

    /// <summary>Gọi khi bắt đầu nhận lại luồng (bỏ khung tới keyframe kế tiếp).</summary>
    public void Resume() => waitKey = true;

    public void Feed(bool isConfig, bool isKey, byte[] data)
    {
        if (isConfig) { config = data; waitKey = true; return; }
        if (waitKey && !isKey) return;
        if (isKey) waitKey = false;
        int extra = isKey && config != null ? config.Length : 0;
        int size = extra + data.Length;
        var buf = (byte*)Ffmpeg.av_malloc((nuint)(size + Ffmpeg.InputPadding));
        if (buf == null) return;
        if (extra > 0) fixed (byte* c = config) Buffer.MemoryCopy(c, buf, size, extra);
        fixed (byte* d = data) Buffer.MemoryCopy(d, buf + extra, size - extra, data.Length);
        new Span<byte>(buf + size, Ffmpeg.InputPadding).Clear();
        if (Ffmpeg.av_packet_from_data(pkt, buf, size) < 0) { Ffmpeg.av_free(buf); return; }
        int r = Ffmpeg.avcodec_send_packet(ctx, pkt);
        Ffmpeg.av_packet_unref(pkt);
        if (r < 0 && r != Ffmpeg.EAGAIN) { Fail("send_packet " + r); return; }
        while (Ffmpeg.avcodec_receive_frame(ctx, frame) == 0)
        {
            Convert(frame);
            Ffmpeg.av_frame_unref(frame);
        }
    }

    void Fail(string why)
    {
        Error = why;
        Ffmpeg.avcodec_flush_buffers(ctx);
        waitKey = true;
        NeedKeyFrame?.Invoke();
    }

    // ---- YUV420 → BGRA (BT.601; dải hạn chế 16–235 hoặc dải đầy đủ cho yuvj420p) ----
    static readonly int[] YL = new int[256], RV = new int[256], GU = new int[256], GV = new int[256], BU = new int[256];
    static readonly int[] YF = new int[256], RVF = new int[256], GUF = new int[256], GVF = new int[256], BUF = new int[256];
    static readonly byte[] Clip = new byte[1024 * 3];
    static H264Decoder()
    {
        for (int i = 0; i < 256; i++)
        {
            YL[i] = 298 * (i - 16) + 128; RV[i] = 409 * (i - 128); GU[i] = -100 * (i - 128); GV[i] = -208 * (i - 128); BU[i] = 516 * (i - 128);
            YF[i] = 256 * i + 128; RVF[i] = 359 * (i - 128); GUF[i] = -88 * (i - 128); GVF[i] = -183 * (i - 128); BUF[i] = 454 * (i - 128);
        }
        for (int i = 0; i < Clip.Length; i++) Clip[i] = (byte)Math.Clamp(i - 1024, 0, 255);
    }

    void Convert(void* f)
    {
        int w = Ffmpeg.FrameWidth(f), h = Ffmpeg.FrameHeight(f), fmt = Ffmpeg.FrameFormat(f);
        int ls0 = Ffmpeg.FrameLinesize(f, 0), ls1 = Ffmpeg.FrameLinesize(f, 1), ls2 = Ffmpeg.FrameLinesize(f, 2);
        // kiểm tra bố cục AVFrame (phòng FFmpeg đổi cấu trúc): chỉ nhận yuv420p / yuvj420p hợp lệ
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192 || ls0 < w || (fmt != 0 && fmt != 12)) { Error = $"khung không hỗ trợ ({w}x{h} fmt {fmt})"; return; }
        bool full = fmt == 12;
        int[] ty = full ? YF : YL, trv = full ? RVF : RV, tgu = full ? GUF : GU, tgv = full ? GVF : GV, tbu = full ? BUF : BU;
        byte* py = Ffmpeg.FrameData(f, 0), pu = Ffmpeg.FrameData(f, 1), pv = Ffmpeg.FrameData(f, 2);
        var dst = Output.BeginWrite(w, h);
        fixed (byte* d0 = dst) fixed (byte* clip0 = Clip)
        fixed (int* cy = ty, crv = trv, cgu = tgu, cgv = tgv, cbu = tbu)
        {
            byte* clip = clip0 + 1024;
            for (int y = 0; y < h; y++)
            {
                byte* yr = py + y * ls0, ur = pu + (y >> 1) * ls1, vr = pv + (y >> 1) * ls2;
                uint* o = (uint*)(d0 + y * w * 4);
                for (int x = 0; x < w; x += 2)
                {
                    int u = ur[x >> 1], v = vr[x >> 1];
                    int r = crv[v], g = cgu[u] + cgv[v], b = cbu[u];
                    int l = cy[yr[x]];
                    o[x] = 0xff000000u | ((uint)clip[(l + r) >> 8] << 16) | ((uint)clip[(l + g) >> 8] << 8) | clip[(l + b) >> 8];
                    if (x + 1 < w)
                    {
                        l = cy[yr[x + 1]];
                        o[x + 1] = 0xff000000u | ((uint)clip[(l + r) >> 8] << 16) | ((uint)clip[(l + g) >> 8] << 8) | clip[(l + b) >> 8];
                    }
                }
            }
        }
        Output.EndWrite();
    }

    public void Dispose()
    {
        fixed (void** p = &frame) Ffmpeg.av_frame_free(p);
        fixed (void** p = &pkt) Ffmpeg.av_packet_free(p);
        fixed (void** p = &ctx) Ffmpeg.avcodec_free_context(p);
    }
}
