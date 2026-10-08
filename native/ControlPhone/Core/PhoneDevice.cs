using System.ComponentModel;
using ControlPhone.Scrcpy;
using ControlPhone.Video;

namespace ControlPhone.Core;

public enum PhoneStatus { Connecting, Online, Offline, Unauthorized, Error }

/// <summary>Một điện thoại (1 ô trên lưới): kết nối adb, phiên scrcpy và bộ giải mã của nó.</summary>
public sealed class PhoneDevice : INotifyPropertyChanged
{
    public string Addr { get; }
    public string HwId { get; set; } = "";
    public bool IsWifi { get; }

    string model = "", label = "", error = "", android = "";
    int num;
    bool root;
    PhoneStatus status = PhoneStatus.Connecting;

    public string Model { get => model; set => Set(ref model, value, nameof(Model), nameof(DisplayName)); }
    public string Label { get => label; set => Set(ref label, value, nameof(Label), nameof(DisplayName)); }
    public string Android { get => android; set => Set(ref android, value, nameof(Android)); }
    public int Num { get => num; set => Set(ref num, value, nameof(Num), nameof(NumText)); }
    public bool Root { get => root; set => Set(ref root, value, nameof(Root)); }
    public PhoneStatus Status { get => status; set => Set(ref status, value, nameof(Status), nameof(StatusText), nameof(ShowOverlay)); }
    public string Error { get => error; set => Set(ref error, value, nameof(Error), nameof(StatusText)); }

    public string DisplayName => string.IsNullOrEmpty(Label) ? (string.IsNullOrEmpty(Model) ? Addr : Model) : Label;
    public string NumText => Num > 0 ? Num.ToString("00") : "?";
    public bool ShowOverlay => Status != PhoneStatus.Online || !HasFrame;
    public string StatusText => Status switch
    {
        PhoneStatus.Online => HasFrame ? "" : "Đang chờ hình…",
        PhoneStatus.Connecting => string.IsNullOrEmpty(Error) ? "Đang kết nối…" : Error,
        PhoneStatus.Unauthorized => "Hãy bấm \"Cho phép gỡ lỗi USB\" trên điện thoại",
        PhoneStatus.Offline => "Mất kết nối",
        _ => Error,
    };

    bool hasFrame;
    public bool HasFrame { get => hasFrame; set => Set(ref hasFrame, value, nameof(HasFrame), nameof(ShowOverlay), nameof(StatusText)); }

    // ---- phiên & giải mã (do DeviceManager quản lý) ----
    internal ScrcpySession? Session;
    internal H264Decoder? Decoder;
    internal int RestartDelayMs = 1000;
    internal CancellationTokenSource? RestartCts;

    public ScrcpySession? ControlSession => Session is { Running: true } s ? s : null;
    public FrameBuffer? Frames => Decoder?.Output;

    public PhoneDevice(string addr)
    {
        Addr = addr;
        IsWifi = Adb.AdbClient.IsWifiSerial(addr);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Set<T>(ref T field, T value, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
