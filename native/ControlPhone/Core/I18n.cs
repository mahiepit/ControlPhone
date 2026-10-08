using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ControlPhone.Core;

/// <summary>
/// Đa ngôn ngữ dùng chung tệp dịch với bản web (lang/*.js: { "câu tiếng Việt": "bản dịch" }).
/// Văn bản gốc trong code là tiếng Việt; câu có biến dùng {0}, {1}… và được khớp theo mẫu.
/// </summary>
public static class I18n
{
    public static readonly (string Code, string Name)[] Langs =
        [("vi", "Tiếng Việt"), ("en", "English"), ("zh-CN", "简体中文"), ("zh-TW", "繁體中文"), ("de", "Deutsch"), ("fr", "Français")];

    public static string Lang { get; private set; } = "vi";
    static Dictionary<string, string> dict = [];
    static List<(Regex Re, string[] Order, string Out)> patterns = [];
    static readonly Regex Vi = new("[ăâđêôơưàảãáạằẳẵắặầẩẫấậèẻẽéẹềểễếệìỉĩíịòỏõóọồổỗốộờởỡớợùủũúụừửữứựỳỷỹýỵ]", RegexOptions.IgnoreCase);

    public static void Init(string? preferred)
    {
        Lang = Langs.Any(l => l.Code == preferred) ? preferred! : Detect();
        if (Lang == "vi") return;
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "lang", Lang + ".js");
            var txt = File.ReadAllText(file);
            var json = txt[txt.IndexOf('{')..(txt.LastIndexOf('}') + 1)];
            json = Regex.Replace(json, @",\s*}$", "}");
            var src = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
            dict = src;
            patterns = src.Where(kv => Regex.IsMatch(kv.Key, @"\{\d+\}") && kv.Value != "")
                .OrderByDescending(kv => kv.Key.Length)
                .Select(kv =>
                {
                    var order = new List<string>();
                    var re = "^" + Regex.Replace(Regex.Escape(kv.Key), @"\\\{(\d+)}", m => { order.Add(m.Groups[1].Value); return @"([\s\S]*?)"; }) + "$";
                    return (new Regex(re), order.ToArray(), kv.Value);
                }).ToList();
        }
        catch { dict = []; patterns = []; }
    }

    static string Detect()
    {
        var c = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
        if (c.StartsWith("vi")) return "vi";
        if (c.StartsWith("zh")) return Regex.IsMatch(c, "tw|hk|mo|hant") ? "zh-TW" : "zh-CN";
        if (c.StartsWith("de")) return "de";
        if (c.StartsWith("fr")) return "fr";
        return "en";
    }

    /// <summary>Dịch câu tiếng Việt; T("Đã chọn {0} máy", 3).</summary>
    public static string T(string text, params object[] args)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string o = text;
        if (Lang != "vi")
        {
            if (dict.TryGetValue(text, out var hit) && hit != "") o = hit;
            else if (text.Trim() is var core && core != text && dict.TryGetValue(core, out var hit2) && hit2 != "") o = text.Replace(core, hit2); // giữ khoảng trắng 2 đầu
            else if (args.Length == 0) o = Dynamic(text) ?? text;
        }
        if (args.Length == 0) return o;
        return Regex.Replace(o, @"\{(\d+)\}", m => int.TryParse(m.Groups[1].Value, out var i) && i < args.Length ? Convert.ToString(args[i], CultureInfo.CurrentCulture) ?? "" : m.Value);
    }

    static string? Dynamic(string text)
    {
        foreach (var (re, order, outp) in patterns)
        {
            var m = re.Match(text);
            if (!m.Success) continue;
            var vals = new Dictionary<string, string>();
            for (int i = 0; i < order.Length; i++) vals[order[i]] = m.Groups[i + 1].Value;
            return Regex.Replace(outp, @"\{(\d+)\}", x => vals.TryGetValue(x.Groups[1].Value, out var v) ? (Vi.IsMatch(v) ? (dict.GetValueOrDefault(v.Trim()) ?? Dynamic(v.Trim()) ?? v) : v) : x.Value);
        }
        return null;
    }

    static string? Translate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || Lang == "vi") return null;
        var core = s.Trim();
        var tr = dict.GetValueOrDefault(core) ?? (Vi.IsMatch(core) ? Dynamic(core) : null);
        return string.IsNullOrEmpty(tr) ? null : s.Replace(core, tr);
    }

    /// <summary>Dịch toàn bộ chữ tĩnh trong một cây giao diện (TextBlock, nhãn nút, ToolTip, Header…).</summary>
    public static void Apply(DependencyObject root)
    {
        if (Lang == "vi" || root == null) return;
        Walk(root, 0);
    }

    static void Walk(DependencyObject o, int depth)
    {
        if (depth > 60) return;
        if (o is FrameworkElement fe && fe.Tag as string == "no-i18n") return;
        switch (o)
        {
            case TextBlock tb when System.Windows.Data.BindingOperations.GetBindingExpression(tb, TextBlock.TextProperty) == null:
                if (Translate(tb.Text) is { } t1) tb.Text = t1;
                break;
            case HeaderedContentControl hc when hc.Header is string h:
                if (Translate(h) is { } t2) hc.Header = t2;
                break;
            case HeaderedItemsControl hi when hi.Header is string h2:
                if (Translate(h2) is { } t3) hi.Header = t3;
                break;
        }
        if (o is ContentControl cc && cc.Content is string cs && Translate(cs) is { } t4) cc.Content = t4;
        if (o is FrameworkElement f && f.ToolTip is string tip && Translate(tip) is { } t5) f.ToolTip = t5;
        if (o is ComboBoxItem ci && ci.Content is string cis && Translate(cis) is { } t6) ci.Content = t6;
        if (o is TextBox tbx && tbx.Tag is string ph && ph != "no-i18n" && Translate(ph) is { } t7) tbx.Tag = t7; // placeholder
        foreach (var child in LogicalTreeHelper.GetChildren(o)) if (child is DependencyObject d) Walk(d, depth + 1);
        if (o is ItemsControl ic && ic is not Selector) foreach (var item in ic.Items) if (item is DependencyObject di) Walk(di, depth + 1);
    }
}
