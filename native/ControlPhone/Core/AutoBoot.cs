using System.Collections.Concurrent;
using System.IO;
using System.Text;
using ControlPhone.Adb;

namespace ControlPhone.Core;

/// <summary>
/// "autoboot": boxphone không có nút nguồn, máy tắt khi đang cắm cáp (hoặc đôi khi sau adb reboot) dừng ở chế độ
/// sạc (LPM) và không hiện trên USB nữa. File init trong /system reboot thẳng vào Android khi máy vào chế độ sạc.
/// Tự cài khi máy kết nối mà chưa có (xem D:\BuildRomAndroid\GHI-CHU-CHO-CONTROLPHONE.md).
/// Không reboot máy, không thử bằng cách tắt nguồn.
/// </summary>
public static class AutoBoot
{
    // nội dung chính xác của n9autoboot.rc (264 byte, LF): phải nằm trong /system/etc/init — rc ở /vendor chạy dưới
    // vendor_init, không được ghi sys.powerctl; "reboot" không kèm lý do để bootloader khởi động Android bình thường
    const string Rc =
        "# Boxphone: no power button. If the phone ever lands in charger mode (LPM)\n" +
        "# because the cable is plugged in, reboot straight into Android.\n" +
        "# Plain \"reboot\" (no reason): the Samsung bootloader treats it as a normal boot.\n" +
        "on charger\n" +
        "    setprop sys.powerctl reboot\n";

    // cài (cần root), chạy nhiều lần được: đã đúng nội dung thì bỏ qua, không remount. Điểm mount là /system nếu
    // có mount riêng, không thì / (system-as-root). Chỉ remount ro lại nếu trước đó đang ro (Note 8 LOS 18.1 để / rw sẵn).
    const string Script =
        "SRC=${1:-/data/local/tmp/n9autoboot.rc}\n" +
        "DST=/system/etc/init/n9autoboot.rc\n" +
        "[ -s \"$SRC\" ] || { echo \"FAIL: missing $SRC\"; exit 1; }\n" +
        "[ -d /system/etc/init ] || { echo \"FAIL: no /system/etc/init\"; exit 1; }\n" +
        "if cmp -s \"$SRC\" \"$DST\" 2>/dev/null; then echo \"OK: da co san\"; exit 0; fi\n" +
        "MP=/; grep -q ' /system ' /proc/mounts && MP=/system\n" +
        "RW=0; grep \" $MP \" /proc/mounts | grep -qE '[ ,]rw[ ,]' && RW=1\n" +
        "[ $RW = 1 ] || mount -o rw,remount $MP || { echo \"FAIL: remount $MP\"; exit 1; }\n" +
        "cat \"$SRC\" > \"$DST\" && chmod 644 \"$DST\" && chown 0:0 \"$DST\" && chcon u:object_r:system_file:s0 \"$DST\"\n" +
        "sync\n" +
        "[ $RW = 1 ] || mount -o ro,remount $MP 2>/dev/null\n" +
        "cmp -s \"$SRC\" \"$DST\" && echo \"OK: da cai $(ls -lZ $DST)\" || echo \"FAIL: verify\"\n";

    /// <summary>Máy đã có/đã cài trong lần chạy này (theo định danh máy) — không kiểm tra lại mỗi lần nhận máy.</summary>
    static readonly ConcurrentDictionary<string, byte> done = new();
    static readonly ConcurrentDictionary<string, byte> running = new();

    /// <summary>
    /// Mọi máy có root và Android 7+ (init đọc /system/etc/init). Đã thử LPM thật trên Note 9 LOS 20; Note 8 LOS 18.1
    /// cùng cơ chế (init.rc có "on charger", / là system-as-root). Máy không ghi được /system chỉ báo FAIL, không đổi gì.
    /// </summary>
    public static bool Applies(bool root, int sdk) => root && sdk >= 24;

    public static bool Done(string id) => done.ContainsKey(id);

    /// <summary>Kiểm tra và cài nếu thiếu (máy phải đã boot xong). Trả về dòng OK:/FAIL: của script.</summary>
    public static async Task<(bool Ok, string Msg)> EnsureAsync(string id, string addr)
    {
        if (done.ContainsKey(id)) return (true, "OK: da co san");
        if (!running.TryAdd(id, 0)) return (false, "đang cài");
        try
        {
            // 1 lệnh shell: ghi 2 tệp (base64 → giữ nguyên LF) rồi chạy script bằng su
            var b64 = (string s) => Convert.ToBase64String(Encoding.ASCII.GetBytes(s));
            var cmd = $"echo {b64(Rc)} | base64 -d > /data/local/tmp/n9autoboot.rc && " +
                      $"echo {b64(Script)} | base64 -d > /data/local/tmp/install-autoboot.sh && " +
                      "su -c 'sh /data/local/tmp/install-autoboot.sh /data/local/tmp/n9autoboot.rc' 2>&1";
            string o;
            try { o = await AdbClient.ShellAsync(addr, cmd, 30000).ConfigureAwait(false); }
            catch (Exception e) { o = "FAIL: " + e.Message; }
            var line = o.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("OK:") || l.StartsWith("FAIL:")) ?? ("FAIL: " + o.Trim());
            bool ok = line.StartsWith("OK:");
            if (ok) done[id] = 0;
            if (!ok || !line.StartsWith("OK: da co san")) Log($"{id} ({addr}): {line}");
            return (ok, line);
        }
        finally { running.TryRemove(id, out _); }
    }

    static void Log(string text)
    {
        try
        {
            var dir = Path.Combine(Store.DataDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "autoboot.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} · {text}\n");
        }
        catch { /* nhật ký không bắt buộc */ }
    }
}
