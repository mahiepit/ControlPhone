using System.Windows;
using System.Windows.Media;

namespace ControlPhone;

/// <summary>Chế độ sáng / tối: thay màu của các brush dùng chung (DynamicResource).</summary>
public static class Theme
{
    public static string Current { get; private set; } = "dark";

    static readonly Dictionary<string, (string Dark, string Light)> Colors = new()
    {
        ["Bg"] = ("#0F1115", "#F4F6FA"),
        ["Bg2"] = ("#171A21", "#FFFFFF"),
        ["Bg3"] = ("#1F232C", "#EEF1F6"),
        ["Line"] = ("#2A2F3B", "#D8DDE7"),
        ["Text"] = ("#E6E8EE", "#1B1F29"),
        ["Muted"] = ("#8A90A2", "#6A7183"),
        ["Accent"] = ("#4F7CFF", "#3366FF"),
        ["AccentSoft"] = ("#334F7CFF", "#263366FF"),
        ["Ok"] = ("#2FBF71", "#1E9E58"),
        ["Warn"] = ("#FF9F1C", "#E08600"),
        ["Bad"] = ("#F25F5C", "#D93C39"),
        ["Hover"] = ("#262B36", "#E3E8F2"),
        ["Screen"] = ("#000000", "#000000"),
    };

    public static void Apply(string theme)
    {
        Current = theme == "light" ? "light" : "dark";
        var res = Application.Current.Resources;
        foreach (var (key, (dark, light)) in Colors)
            res[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Current == "light" ? light : dark));
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}

/// <summary>Biểu tượng (font Segoe Fluent Icons / Segoe MDL2 Assets có sẵn trên Windows 10/11).</summary>
public static class Icons
{
    static readonly Dictionary<string, string> Map = new()
    {
        ["sidebar"] = "", ["phone"] = "", ["download"] = "", ["eye"] = "", ["pause"] = "", ["play"] = "",
        ["grid"] = "", ["fit"] = "", ["heart"] = "", ["moon"] = "", ["sun"] = "", ["settings"] = "",
        ["search"] = "", ["checksq"] = "", ["square"] = "", ["invert"] = "", ["back"] = "", ["home"] = "",
        ["recents"] = "", ["power"] = "", ["voldown"] = "", ["volup"] = "", ["unlock"] = "", ["screenoff"] = "",
        ["type"] = "", ["clipboard"] = "", ["apps"] = "", ["package"] = "", ["upload"] = "", ["link"] = "",
        ["terminal"] = "", ["camera"] = "", ["more"] = "", ["left"] = "", ["right"] = "", ["x"] = "",
        ["rotate"] = "", ["bell"] = "", ["shotcopy"] = "", ["folder"] = "", ["info"] = "", ["monitor"] = "",
        ["wifi"] = "", ["wifioff"] = "", ["usb"] = "", ["scan"] = "", ["key"] = "", ["refresh"] = "",
        ["eraser"] = "", ["tag"] = "", ["plus"] = "", ["plug"] = "", ["hash"] = "", ["shield"] = "",
        ["trash"] = "", ["file"] = "", ["check"] = "", ["copy"] = "", ["zap"] = "", ["stop"] = "",
        ["maximize"] = "", ["globe"] = "", ["up"] = "", ["mute"] = "", ["warn"] = "", ["keyboard"] = "",
    };

    public static string Get(string name) => Map.GetValueOrDefault(name, "");
}
