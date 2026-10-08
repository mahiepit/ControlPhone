using System.IO;
using System.Runtime.InteropServices;

namespace ControlPhone.Video;

/// <summary>
/// Gọi trực tiếp FFmpeg (avcodec/avutil đi kèm scrcpy). Chỉ dùng hàm có ABI ổn định;
/// không đọc/ghi trường của AVCodecContext / AVPacket (dùng av_opt_set, av_packet_from_data),
/// chỉ đọc các trường đầu của AVFrame (data, linesize, width, height, format — vị trí không đổi qua nhiều đời FFmpeg).
/// </summary>
internal static unsafe class Ffmpeg
{
    const string AvCodec = "avcodec";
    const string AvUtil = "avutil";
    public const int CodecIdH264 = 27;
    public const int EAGAIN = -11;
    public const int OptSearchChildren = 1;

    static Ffmpeg()
    {
        // tên DLL có số phiên bản (avcodec-63.dll …) → tự tìm trong thư mục chạy
        NativeLibrary.SetDllImportResolver(typeof(Ffmpeg).Assembly, (name, asm, path) =>
        {
            if (name != AvCodec && name != AvUtil) return IntPtr.Zero;
            var f = Directory.GetFiles(AppContext.BaseDirectory, name + "-*.dll").OrderByDescending(x => x).FirstOrDefault();
            return f != null ? NativeLibrary.Load(f) : IntPtr.Zero;
        });
    }

    [DllImport(AvCodec)] public static extern uint avcodec_version();
    [DllImport(AvUtil)] public static extern uint avutil_version();
    [DllImport(AvCodec)] public static extern void* avcodec_find_decoder(int id);
    [DllImport(AvCodec)] public static extern void* avcodec_alloc_context3(void* codec);
    [DllImport(AvCodec)] public static extern int avcodec_open2(void* ctx, void* codec, void** options);
    [DllImport(AvCodec)] public static extern void avcodec_free_context(void** ctx);
    [DllImport(AvCodec)] public static extern int avcodec_send_packet(void* ctx, void* pkt);
    [DllImport(AvCodec)] public static extern int avcodec_receive_frame(void* ctx, void* frame);
    [DllImport(AvCodec)] public static extern void avcodec_flush_buffers(void* ctx);
    [DllImport(AvCodec)] public static extern void* av_packet_alloc();
    [DllImport(AvCodec)] public static extern void av_packet_free(void** pkt);
    [DllImport(AvCodec)] public static extern int av_packet_from_data(void* pkt, byte* data, int size);
    [DllImport(AvCodec)] public static extern void av_packet_unref(void* pkt);
    [DllImport(AvUtil)] public static extern void* av_frame_alloc();
    [DllImport(AvUtil)] public static extern void av_frame_free(void** frame);
    [DllImport(AvUtil)] public static extern void av_frame_unref(void* frame);
    [DllImport(AvUtil)] public static extern void* av_malloc(nuint size);
    [DllImport(AvUtil)] public static extern void av_free(void* ptr);
    [DllImport(AvUtil, CharSet = CharSet.Ansi)] public static extern int av_opt_set(void* obj, string name, string val, int flags);

    public const int InputPadding = 64; // AV_INPUT_BUFFER_PADDING_SIZE

    // ---- AVFrame (chỉ các trường đầu) ----
    public static byte* FrameData(void* f, int plane) => ((byte**)f)[plane];
    public static int FrameLinesize(void* f, int plane) => ((int*)((byte*)f + 64))[plane];
    public static int FrameWidth(void* f) => *(int*)((byte*)f + 104);
    public static int FrameHeight(void* f) => *(int*)((byte*)f + 108);
    public static int FrameFormat(void* f) => *(int*)((byte*)f + 116);

    public static string VersionString(uint v) => $"{v >> 16}.{(v >> 8) & 0xff}.{v & 0xff}";
}
