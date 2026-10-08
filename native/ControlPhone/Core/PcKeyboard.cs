using System.IO;
using ControlPhone.Adb;

namespace ControlPhone.Core;

/// <summary>
/// Chế độ "chỉ nhập từ PC": cài + chọn bàn phím ControlPhone (native/keyboard) — một bàn phím không bao giờ hiện
/// trên màn hình điện thoại; phím, chữ và lệnh dán từ PC vẫn đi thẳng vào ô nhập. Tắt chế độ = trả lại bàn phím cũ.
/// </summary>
public static class PcKeyboard
{
    public const string Package = "com.controlphone.keyboard";
    public const string Ime = Package + "/.PcKeyboard";
    static readonly string Apk = Path.Combine(AppContext.BaseDirectory, "ControlPhoneKeyboard.apk");

    static async Task<string> Sh(string addr, string cmd, int ms = 15000) => (await AdbClient.ShellAsync(addr, cmd, ms)).Trim();

    public static async Task<string> CurrentIme(string addr) => await Sh(addr, "settings get secure default_input_method");

    /// <summary>Bật chế độ. Trả về (ok, thông báo, bàn phím trước đó).</summary>
    public static async Task<(bool Ok, string Msg, string? Prev)> EnableAsync(string addr)
    {
        var prev = await CurrentIme(addr);
        if ((await Sh(addr, $"pm path {Package}")) == "")
        {
            if (!File.Exists(Apk)) return (false, "Không thấy ControlPhoneKeyboard.apk", null);
            var r = await AdbClient.RunAsync([.. AdbClient.Sel(addr), "install", "-r", Apk], 120000);
            if (!r.Out.Contains("Success")) return (false, "Không cài được bàn phím: " + r.Out, null);
        }
        // Android đăng ký bàn phím mới sau khi cài vài giây → thử lại
        string o = "";
        for (int i = 0; i < 15; i++)
        {
            o = await Sh(addr, $"ime enable {Ime} 2>&1");
            if (!o.Contains("Unknown input method")) break;
            await Task.Delay(1000);
        }
        if (o.Contains("Unknown input method")) return (false, o, null);
        await Sh(addr, $"ime set {Ime} 2>&1");
        var now = await CurrentIme(addr);
        if (now != Ime) return (false, "Không chọn được bàn phím: " + now, null);
        return (true, "Đã tắt bàn phím điện thoại — chỉ nhập từ PC", prev == Ime ? null : prev);
    }

    /// <summary>Tắt chế độ: trả lại bàn phím cũ (hoặc bàn phím đầu tiên còn bật).</summary>
    public static async Task<(bool Ok, string Msg)> DisableAsync(string addr, string? prev)
    {
        var enabled = (await Sh(addr, "ime list -s")).Split('\n').Select(x => x.Trim()).Where(x => x != "" && x != Ime).ToList();
        var target = !string.IsNullOrEmpty(prev) && prev != "null" ? prev : enabled.FirstOrDefault(x => !x.Contains("voice", StringComparison.OrdinalIgnoreCase)) ?? enabled.FirstOrDefault();
        if (target == null) return (false, "Không có bàn phím nào khác đang bật");
        if (!enabled.Contains(target)) await Sh(addr, $"ime enable {target} 2>&1");
        await Sh(addr, $"ime set {target} 2>&1");
        await Sh(addr, $"ime disable {Ime} 2>&1");
        var now = await CurrentIme(addr);
        return now == target ? (true, "Đã bật lại bàn phím điện thoại: " + target) : (false, "Không chọn lại được bàn phím: " + now);
    }

    /// <summary>Khi máy kết nối lại: nếu đang bật chế độ mà bàn phím bị đổi → chọn lại bàn phím ControlPhone.</summary>
    public static async Task EnsureAsync(string addr)
    {
        if (await CurrentIme(addr) == Ime) return;
        await EnableAsync(addr);
    }
}
