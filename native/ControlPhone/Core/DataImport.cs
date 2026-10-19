using System.IO;
using System.Text.Json;

namespace ControlPhone.Core;

/// <summary>
/// Mang dữ liệu (số thứ tự, tên, nhóm, cài đặt) từ bản ControlPhone cũ sang bản mới. Người dùng thường giải nén bản mới
/// vào một thư mục KHÁC → bản mới không thấy thư mục data cũ và đánh số lại từ đầu.
/// </summary>
public static class DataImport
{
    public sealed record Candidate(string Dir, int Phones, DateTime Modified);

    /// <summary>
    /// Tìm thư mục data của các bản cũ: các thư mục cạnh thư mục chương trình (các bản đã giải nén) và thư mục
    /// ControlPhone* trong Downloads / Desktop / Documents. Mới nhất trước.
    /// </summary>
    public static List<Candidate> FindPrevious(string? exeDir = null, string? ownDataDir = null, bool userFolders = true)
    {
        var own = Norm(ownDataDir ?? Store.DataDir);
        var found = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        var exe = new DirectoryInfo(exeDir ?? AppContext.BaseDirectory);
        // cạnh thư mục chương trình: mọi thư mục con (thường là các bản ControlPhone-vX đã giải nén)
        foreach (var root in new[] { exe.Parent, exe.Parent?.Parent })
            if (root != null) Scan(root.FullName, nameFilter: false, own, found);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (userFolders)
        foreach (var root in new[] { Path.Combine(home, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
            if (Directory.Exists(root)) Scan(root, nameFilter: true, own, found);
        return found.Values.OrderByDescending(c => c.Modified).ToList();
    }

    static string Norm(string p) { try { return Path.GetFullPath(p).TrimEnd('\\', '/'); } catch { return p; } }

    // root\data, root\X\data, root\X\Y\data (bản giải nén có thể lồng thêm 1 cấp thư mục)
    static void Scan(string root, bool nameFilter, string own, Dictionary<string, Candidate> found)
    {
        void Try(string dir)
        {
            var data = Norm(Path.Combine(dir, "data"));
            if (data.Equals(own, StringComparison.OrdinalIgnoreCase) || found.ContainsKey(data)) return;
            var cfg = Path.Combine(data, "config.json");
            if (!File.Exists(cfg)) return;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
                int phones = doc.RootElement.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Object ? devs.EnumerateObject().Count() : 0;
                if (phones > 0) found[data] = new Candidate(data, phones, File.GetLastWriteTime(cfg));
            }
            catch { /* tệp hỏng: bỏ qua */ }
        }
        try
        {
            if (!nameFilter) Try(root);
            foreach (var x in Directory.EnumerateDirectories(root).Take(300))
            {
                if (nameFilter && !Path.GetFileName(x).Contains("ControlPhone", StringComparison.OrdinalIgnoreCase)) continue;
                Try(x);
                foreach (var y in SafeDirs(x).Take(50)) Try(y);
            }
        }
        catch { /* không đọc được thư mục */ }
    }

    static IEnumerable<string> SafeDirs(string dir) { try { return Directory.EnumerateDirectories(dir).ToList(); } catch { return []; } }

    /// <summary>Chép config.json (+ native-ui.json) của bản cũ vào thư mục data hiện tại; tệp đang có được giữ lại (.before-import-…).</summary>
    public static void Import(string fromDataDir, string? toDataDir = null)
    {
        var to = toDataDir ?? Store.DataDir;
        Directory.CreateDirectory(to);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        foreach (var name in new[] { "config.json", "native-ui.json" })
        {
            var src = Path.Combine(fromDataDir, name);
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(to, name);
            if (File.Exists(dst)) File.Copy(dst, $"{dst}.before-import-{stamp}", true);
            File.Copy(src, dst, true);
        }
    }
}
