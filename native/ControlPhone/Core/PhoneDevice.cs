using System.ComponentModel;
using ControlPhone.Scrcpy;
using ControlPhone.Video;

namespace ControlPhone.Core;

/// <summary>
/// Một điện thoại (1 ô trên lưới). Có thể có nhiều đường kết nối (USB + WiFi) — chỉ 1 đường đang dùng.
/// Mọi thuộc tính hiển thị chỉ được sửa trên luồng giao diện; gọi <see cref="Changed"/> để cập nhật ô.
/// </summary>
public sealed class PhoneDevice : INotifyPropertyChanged
{
    static int vidCounter;

    public string Id { get; internal set; }
    public int Vid { get; } = Interlocked.Increment(ref vidCounter);
    public bool Pending { get; internal set; }   // ô thông báo: máy chưa cho phép / offline / lỗi đọc thông tin

    /// <summary>addr → "usb" | "wifi".</summary>
    public Dictionary<string, string> Transports { get; } = [];
    public string? ActiveAddr { get; internal set; }
    public string? Transport => ActiveAddr != null && Transports.TryGetValue(ActiveAddr, out var t) ? t : null;
    public bool HasUsb => Transports.ContainsValue("usb");
    public bool HasWifi => Transports.ContainsValue("wifi");

    // ---- thông tin máy ----
    public string Model { get; internal set; } = "";
    public string Manufacturer { get; internal set; } = "";
    public string Android { get; internal set; } = "";
    public int Sdk { get; internal set; }
    public bool Root { get; internal set; }
    public string AndroidId { get; internal set; } = "";
    public int? Battery { get; internal set; }
    public bool Charging { get; internal set; }
    public double? Temp { get; internal set; }
    public string Ip { get; internal set; } = "";
    public bool? ScreenOn { get; internal set; }

    // ---- trạng thái ----
    public string Status { get; internal set; } = "offline"; // online | connecting | offline | unauthorized | error | authorizing
    public string Error { get; internal set; } = "";
    public bool Zoomed => HqRefs > 0;
    public bool Paused => !Pending && Store.State.Devices.TryGetValue(Id, out var m) && m.Paused;
    public bool SoloHidden => !Pending && Store.State.Solo is { Count: > 0 } s && !s.Contains(Id);
    public bool IsOnline => Status == "online";

    // ---- giao diện ----
    bool selected, active, hasFrame;
    public bool IsSelected { get => selected; set { if (selected != value) { selected = value; Raise(nameof(IsSelected)); } } }
    public bool IsActive { get => active; set { if (active != value) { active = value; Raise(nameof(IsActive)); } } }
    public bool HasFrame { get => hasFrame; set { if (hasFrame != value) { hasFrame = value; Changed(); } } }
    public static bool Live = true; // nút "Đang xem / Đã tạm dừng" (toàn bộ)

    public DeviceMeta Meta => Pending ? new DeviceMeta() : Store.Meta(Id);
    public int Num => Pending ? 0 : Meta.Num;
    public string Label => Pending ? "" : Meta.Label;
    public IReadOnlyList<string> Groups => Pending ? [] : Meta.Groups;
    public string Serial => ActiveAddr ?? Transports.Keys.FirstOrDefault() ?? Id;
    public string DisplayName => !string.IsNullOrEmpty(Label) ? Label : !string.IsNullOrEmpty(Model) ? Model : Serial;
    public string NumText => Pending ? "?" : Num.ToString("00");
    public int VideoW { get; internal set; }
    public int VideoH { get; internal set; }

    public string BatteryText => Battery is int b ? b + "%" : "";
    public string BatteryKind => Battery is null ? "" : Charging ? "chg" : Battery <= 20 ? "low" : "";
    public string BatteryTip => Battery is null ? "" : I18n.T("Pin") + (Charging ? " " + I18n.T("(đang sạc)") : "") + (Temp is double t ? $" · {t}°C" : "");
    public bool PcKeyboardOn => !Pending && Meta.PcKeyboard;
    public bool UsbActive => Transport == "usb";
    public bool WifiActive => Transport == "wifi";

    // ---- lớp phủ trạng thái (giống bản web) ----
    public string OverlayKind
    {
        get
        {
            if (Pending || Status == "offline") return "err";
            if ((Zoomed || Paused) && IsOnline) return "soft";
            if (Status == "connecting" || !HasFrame) return "spin";
            return Live ? "" : "pause";
        }
    }
    public string OverlayTitle
    {
        get
        {
            if (Pending) return Status;
            if (Status == "offline") return I18n.T("Mất kết nối");
            if (Zoomed && IsOnline) return I18n.T("Đang xem ở màn hình lớn");
            if (Paused && IsOnline) return I18n.T("Tạm dừng xem");
            if (Status == "connecting" || !HasFrame) return IsOnline && Error == "" ? "" : (Error != "" ? I18n.T(Error) : I18n.T("Đang kết nối…"));
            return "";
        }
    }
    public string OverlaySub => Pending ? I18n.T(Error) : (Paused && IsOnline && !Zoomed ? I18n.T("Vẫn điều khiển được") : "");
    public bool ShowOverlay => OverlayKind != "";
    public bool ShowSpinner => OverlayKind == "spin";

    public string Tooltip =>
        $"{DisplayName}\n{Model} · Android {Android}\n{string.Join("\n", Transports.Select(t => t.Value.ToUpper() + ": " + t.Key))}{(Ip != "" ? "\nIP: " + Ip : "")}\n\n" +
        I18n.T("Click: chọn máy này · Ctrl+click: thêm/bớt vào nhóm · Nhấp đúp: mở lớn · Chuột phải: menu");

    // ---- phiên & giải mã (DeviceManager quản lý) ----
    internal ScrcpySession? Thumb, Hq;
    internal H264Decoder? ThumbDecoder, HqDecoder;
    internal int HqRefs, HqFails;
    internal int RestartDelayMs = 1000;
    internal CancellationTokenSource? RestartCts;
    internal int InfoBusy, ProbeFails;
    internal bool Visible = true;      // ô đang nằm trong vùng nhìn thấy (ngoài vùng: không giải mã)
    internal long WatchSince, WatchFrames; internal bool WatchKf; internal int WatchResets; internal bool Watching;

    public ScrcpySession? ControlSession => Thumb is { Running: true } t ? t : Hq is { Running: true } h ? h : null;
    public FrameBuffer? Frames => ThumbDecoder?.Output;
    public FrameBuffer? HqFrames => HqDecoder?.Output;

    public PhoneDevice(string id) { Id = id; }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    /// <summary>Cập nhật mọi ràng buộc hiển thị của ô.</summary>
    public void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
