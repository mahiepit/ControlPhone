using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlPhone.Core;

/// <summary>Cài đặt (tương thích data/config.json của bản web).</summary>
public sealed class Settings
{
    public int ThumbMaxSize { get; set; } = 480;
    // lưới mặc định nhẹ (Xiaowei chỉ 480p · 2 fps · 50 kbps): 40 máy × 15 fps = 600 khung/giây làm cả app ì
    public int ThumbBitRate { get; set; } = 600_000;
    public int ThumbFps { get; set; } = 15;
    /// <summary>
    /// Dùng bộ mã hoá PHẦN MỀM (OMX.google.h264.encoder) cho lưới. Mặc định TẮT: khi đang xem, ưu tiên bộ mã hoá phần cứng
    /// của máy (chọn qua scrcpy list_encoders, tránh c2.android.* / OMX.google.*).
    /// </summary>
    public bool SoftwareEncoder { get; set; }
    /// <summary>Phiên bản cài đặt (để chuyển đổi một lần khi đổi mặc định).</summary>
    public int Rev { get; set; }
    public int HqMaxSize { get; set; } = 1280;
    public int HqBitRate { get; set; } = 4_000_000;
    public int HqFps { get; set; } = 30;
    public bool PowerOnConnect { get; set; } = true;
    public bool ScreenOffOnConnect { get; set; } = true;
    public string PreferTransport { get; set; } = "usb";
    public bool AutoReconnectWifi { get; set; } = true;
    /// <summary>
    /// Tắt adb server khi thoát. MẶC ĐỊNH KHÔNG: các chương trình khác (script tự động, Xiaowei…) vẫn đang dùng adb.
    /// Khoá JSON mới ("killAdbOnExit"): giá trị cũ "stopAdbOnExit" (mặc định bật ở bản ≤ 2.0.9) không còn được dùng.
    /// </summary>
    public bool KillAdbOnExit { get; set; }
    public bool AutoPersistKey { get; set; } = true;
    public int MaxParallelStart { get; set; } = 6;
    public int MaxParallelJobs { get; set; } = 10;
    public string ScreenshotDir { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public Settings Clone() => (Settings)MemberwiseClone();
}

/// <summary>Thông tin riêng của từng máy (khoá = hwId, thường là ro.serialno).</summary>
public sealed class DeviceMeta
{
    public string Label { get; set; } = "";
    public int Num { get; set; }
    public List<string> Groups { get; set; } = [];
    public string? AndroidId { get; set; }
    /// <summary>ro.serialno lúc kết nối USB gần nhất (để nhận ra chính máy này khi nó kết nối qua WiFi).</summary>
    public string? SerialNo { get; set; }
    public string? Wifi { get; set; }
    public bool Paused { get; set; }
    public bool KeyPushed { get; set; }
    /// <summary>
    /// "IP:15555" của xwdb (adbd riêng của Xiaowei, chạy root qua WiFi) nếu máy có: đường vào khi ADB USB bị "chờ cho phép"
    /// để tự cấy lại khoá. Cập nhật theo IP mới nhất.
    /// </summary>
    public string? Xwdb { get; set; }
    /// <summary>Chế độ "chỉ nhập từ PC": dùng bàn phím ControlPhone (không hiện bàn phím trên điện thoại).</summary>
    public bool PcKeyboard { get; set; }
    /// <summary>Bàn phím trước khi bật chế độ trên (để trả lại khi tắt).</summary>
    public string? PrevIme { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class StoreState
{
    public Settings Settings { get; set; } = new();
    public Dictionary<string, DeviceMeta> Devices { get; set; } = [];
    public List<string> Groups { get; set; } = [];
    public List<string> WifiHistory { get; set; } = [];
    public int NextNum { get; set; } = 1;
    public List<string>? Solo { get; set; }
    public List<string> DupSerials { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Tuỳ chọn giao diện của bản native (data/native-ui.json).</summary>
public sealed class UiPrefs
{
    public double TileW { get; set; } = 180;
    public double TileWBeforeSolo { get; set; }
    public string Sort { get; set; } = "num";
    public string Filter { get; set; } = "all";
    public string Theme { get; set; } = "dark";
    public string Lang { get; set; } = "";
    public bool SyncOff { get; set; }
    /// <summary>Chỉ hiển thị máy đang kết nối (ẩn máy mất kết nối khỏi lưới).</summary>
    public bool HideOffline { get; set; }
    public bool Sidebar { get; set; } = true;
    public List<string> ShellHist { get; set; } = [];
    public string PushDir { get; set; } = "/sdcard/Download/";
    public string FilesPath { get; set; } = "/sdcard/";
    public List<string> Selected { get; set; } = [];
    /// <summary>Máy chọn gần nhất ở chế độ "Chỉ hiển thị 1 điện thoại" (đưa lên đầu danh sách).</summary>
    public string LastSingle { get; set; } = "";
}

/// <summary>Lưu trữ dùng chung với bản web: data/config.json. Ghi trễ 300 ms, ghi qua tệp tạm.</summary>
public static class Store
{
    public static readonly string DataDir = FindDataDir();
    static readonly string File = Path.Combine(DataDir, "config.json");
    static readonly string UiFile = Path.Combine(DataDir, "native-ui.json");
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    static readonly object gate = new();
    static Timer? saveTimer, uiTimer;

    public static StoreState State { get; private set; } = new();
    public static Settings Settings => State.Settings;
    public static UiPrefs Ui { get; private set; } = new();

    static string FindDataDir()
    {
        var env = Environment.GetEnvironmentVariable("CP_DATA_DIR");
        if (!string.IsNullOrEmpty(env)) return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var cand = Path.Combine(d.FullName, "data");
            if (System.IO.File.Exists(Path.Combine(cand, "config.json"))) return cand;
        }
        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    /// <summary>Lỗi đọc cấu hình (tệp hỏng): khi có lỗi, KHÔNG bao giờ ghi đè config.json.</summary>
    public static string? LoadError { get; private set; }
    static bool saveBlocked, uiBlocked, sessionBackupDone;
    public static string ConfigPath => File;

    /// <summary>Ngừng ghi (vd. vừa nhập dữ liệu bản cũ và sắp khởi động lại): không ghi đè tệp vừa chép vào.</summary>
    public static void SuspendSaving() { saveBlocked = true; uiBlocked = true; }

    /// <summary>Đọc tệp, thử lại khi đang bị chương trình khác ghi (khoá tạm thời).</summary>
    static string? ReadWithRetry(string file)
    {
        for (int i = 0; ; i++)
        {
            try { return System.IO.File.Exists(file) ? System.IO.File.ReadAllText(file) : null; }
            catch (IOException) when (i < 10) { Thread.Sleep(200); }
        }
    }

    public static void Load()
    {
        string? json = null;
        try
        {
            json = ReadWithRetry(File);
            State = json == null ? new() : JsonSerializer.Deserialize<StoreState>(json, Json) ?? throw new JsonException("config.json rỗng");
        }
        catch (Exception e)
        {
            // tệp có nhưng không đọc được: giữ nguyên tệp gốc, sao lưu, và chặn mọi lần ghi để không mất tên/số thứ tự máy
            State = new();
            saveBlocked = true;
            LoadError = e.Message;
            try { if (json != null) System.IO.File.WriteAllText(File + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}", json); } catch { }
        }
        State.Settings ??= new();
        // 2.0.11–2.0.15 từng tự hạ lưới xuống 5 fps/150 kbps rồi 10 fps/300 kbps; người dùng không muốn giảm khung hình
        // → 2.0.16 trả về mặc định 15 fps / 600 kbps (chỉ khi đang đúng các mức app tự đặt, không đụng mức người dùng chọn)
        if (State.Settings.Rev < 3)
        {
            var st = State.Settings;
            if (st.ThumbMaxSize == 480 && ((st.ThumbBitRate == 300_000 && st.ThumbFps == 10) || (st.ThumbBitRate == 150_000 && st.ThumbFps == 5))) { st.ThumbBitRate = 600_000; st.ThumbFps = 15; }
            st.Rev = 3;
        }
        // 2.1.1: ưu tiên bộ mã hoá phần cứng khi đang xem (2.0.13–2.1.0 mặc định dùng phần mềm cho lưới)
        if (State.Settings.Rev < 4) { State.Settings.SoftwareEncoder = false; State.Settings.Rev = 4; }
        State.Devices ??= [];
        foreach (var m in State.Devices.Values) m.Groups ??= [];
        if (string.IsNullOrWhiteSpace(Settings.ScreenshotDir)) Settings.ScreenshotDir = Path.Combine(Path.GetDirectoryName(DataDir) ?? AppContext.BaseDirectory, "screenshots");
        try { Ui = JsonSerializer.Deserialize<UiPrefs>(System.IO.File.ReadAllText(UiFile), Json) ?? new(); } catch { Ui = new(); }
    }

    public static DeviceMeta Meta(string hwId)
    {
        lock (gate)
        {
            if (!State.Devices.TryGetValue(hwId, out var m))
            {
                int next = Math.Max(State.NextNum, State.Devices.Count == 0 ? 1 : State.Devices.Values.Max(x => x.Num) + 1);
                m = new DeviceMeta { Num = next };
                State.Devices[hwId] = m;
                State.NextNum = next + 1;
                Save();
            }
            return m;
        }
    }

    public static void Save()
    {
        lock (gate)
        {
            saveTimer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            saveTimer.Change(300, Timeout.Infinite);
        }
    }

    public static void SaveUi()
    {
        lock (gate)
        {
            uiTimer ??= new Timer(_ => FlushUi(), null, Timeout.Infinite, Timeout.Infinite);
            uiTimer.Change(500, Timeout.Infinite);
        }
    }

    public static void Flush()
    {
        if (saveBlocked) return; // cấu hình đọc lỗi lúc khởi động → không ghi đè dữ liệu thật
        string json;
        lock (gate) json = JsonSerializer.Serialize(State, Json);
        try
        {
            if (System.IO.File.Exists(File))
            {
                // bản chụp đầu phiên + bản trước đó (khôi phục được nếu có sự cố)
                if (!sessionBackupDone) { System.IO.File.Copy(File, File + ".session.bak", true); sessionBackupDone = true; }
                System.IO.File.Copy(File, File + ".bak", true);
            }
        }
        catch { }
        WriteAtomic(File, json);
    }

    public static void FlushUi()
    {
        if (uiBlocked) return;
        string json;
        lock (gate) json = JsonSerializer.Serialize(Ui, Json);
        WriteAtomic(UiFile, json);
    }

    static void WriteAtomic(string file, string json)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = file + ".tmp";
            System.IO.File.WriteAllText(tmp, json);
            System.IO.File.Move(tmp, file, overwrite: true);
        }
        catch { /* thư mục chỉ đọc: bỏ qua */ }
    }
}
