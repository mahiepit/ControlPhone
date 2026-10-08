using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ControlPhone.Core;

/// <summary>
/// Cấu hình dùng chung với bản web: data/config.json (cài đặt luồng + tên/số thứ tự từng máy).
/// Tìm thư mục data/ cạnh file chạy, hoặc ở các thư mục cha (khi chạy bản dựng trong native/…/bin).
/// </summary>
public sealed class AppConfig
{
    public string DataDir { get; }
    string File => Path.Combine(DataDir, "config.json");
    JsonObject root = new();
    readonly object gate = new();

    public int ThumbMaxSize => GetSetting("thumbMaxSize", 480);
    public int ThumbBitRate => GetSetting("thumbBitRate", 600_000);
    public int ThumbFps => GetSetting("thumbFps", 15);
    public bool PowerOnConnect => GetSetting("powerOnConnect", true);
    public bool ScreenOffOnConnect => GetSetting("screenOffOnConnect", true);
    public int MaxParallelStart => GetSetting("maxParallelStart", 6);

    public AppConfig()
    {
        DataDir = FindDataDir();
        Load();
    }

    static string FindDataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = dir; d != null; d = d.Parent)
        {
            var cand = Path.Combine(d.FullName, "data");
            if (System.IO.File.Exists(Path.Combine(cand, "config.json"))) return cand;
        }
        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    void Load()
    {
        try { root = JsonNode.Parse(System.IO.File.ReadAllText(File)) as JsonObject ?? new(); }
        catch { root = new(); }
    }

    T GetSetting<T>(string key, T def)
    {
        lock (gate)
        {
            try { var v = root["settings"]?[key]; return v != null ? v.GetValue<T>() : def; }
            catch { return def; }
        }
    }

    /// <summary>Tên + số thứ tự của máy (khoá = ro.serialno, như bản web). Máy mới được cấp số kế tiếp.</summary>
    public (string Label, int Num) DeviceMeta(string hwId)
    {
        lock (gate)
        {
            var devices = root["devices"] as JsonObject ?? (JsonObject)(root["devices"] = new JsonObject());
            if (devices[hwId] is not JsonObject m)
            {
                int next = (root["nextNum"]?.GetValue<int>() ?? 1);
                foreach (var kv in devices) next = Math.Max(next, (kv.Value?["num"]?.GetValue<int>() ?? 0) + 1);
                m = new JsonObject { ["label"] = "", ["num"] = next, ["groups"] = new JsonArray() };
                devices[hwId] = m;
                root["nextNum"] = next + 1;
                Save();
            }
            return (m["label"]?.GetValue<string>() ?? "", m["num"]?.GetValue<int>() ?? 0);
        }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            System.IO.File.Move(tmp, File, overwrite: true);
        }
        catch { /* bỏ qua: thư mục chỉ đọc */ }
    }
}
