using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using ControlPhone.Core;

namespace ControlPhone;

/// <summary>Một ô điện thoại: hiện khung hình mới nhất. Thao tác chuột do cửa sổ chính xử lý tập trung.</summary>
public partial class PhoneTile : UserControl
{
    WriteableBitmap? bmp;
    PhoneDevice? bound;

    /// <summary>Các ô đang tồn tại (cửa sổ chính cập nhật hình cho chúng mỗi khung hình).</summary>
    public static readonly HashSet<PhoneTile> Live = [];
    public static double TileWidth = 180;

    public PhoneDevice? Device => DataContext as PhoneDevice;
    public FrameworkElement ScreenArea => Screen;
    public FrameworkElement HeadArea => Head;

    public PhoneTile()
    {
        InitializeComponent();
        Loaded += (_, _) => { Live.Add(this); SetTileWidth(TileWidth); };
        Unloaded += (_, _) => Live.Remove(this);
        DataContextChanged += (_, e) =>
        {
            if (bound != null) bound.PropertyChanged -= OnDevChanged;
            bound = e.NewValue as PhoneDevice;
            if (bound != null) bound.PropertyChanged += OnDevChanged;
            bmp = null; Img.Source = null;
            UpdateOverlayIcon();
        };
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
        SpinRot.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    void OnDevChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or null) { UpdateOverlayIcon(); FitScreen(); }
    }

    void UpdateOverlayIcon()
    {
        var d = Device;
        if (d == null) return;
        OvIcon.Text = d.OverlayKind switch
        {
            "err" => Icons.Get(d.Pending ? "shield" : "plug"),
            "soft" => Icons.Get(d.Zoomed ? "maximize" : "pause"),
            "pause" => Icons.Get("pause"),
            _ => "",
        };
    }

    /// <summary>Chiều rộng ô (px); chiều cao màn hình theo tỉ lệ khung hình của máy.</summary>
    public void SetTileWidth(double w)
    {
        Width = w;
        FitScreen();
    }

    double Aspect()
    {
        if (bmp != null) return (double)bmp.PixelHeight / bmp.PixelWidth;
        var d = Device;
        if (d is { VideoW: > 0, VideoH: > 0 }) return (double)d.VideoH / d.VideoW;
        return 2.055;
    }

    void FitScreen()
    {
        if (double.IsNaN(Width)) return;
        double w = Width - 10 - 4; // margin ngoài 2×5 + viền/đệm 2×2
        Screen.Height = Math.Max(40, Math.Round(w * Aspect()));
    }

    /// <summary>Gọi mỗi khung hình màn hình (luồng giao diện): chép khung mới nếu có.</summary>
    public void UpdateFrame()
    {
        var d = Device;
        var fb = d?.Frames;
        if (fb == null || !PhoneDevice.Live) return;
        bool resized = false;
        fb.TryTake((px, w, h) =>
        {
            if (bmp == null || bmp.PixelWidth != w || bmp.PixelHeight != h)
            {
                bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                Img.Source = bmp;
                resized = true;
            }
            bmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        });
        if (resized) FitScreen();
        if (bmp != null && !d!.HasFrame) d.HasFrame = true;
    }

    /// <summary>Toạ độ chuẩn hoá 0..1 trên màn hình điện thoại (ảnh phủ kín khung màn hình).</summary>
    public Point Norm(Point pInScreen)
    {
        double W = Math.Max(1, Screen.ActualWidth), H = Math.Max(1, Screen.ActualHeight);
        return new Point(Math.Clamp(pInScreen.X / W, 0, 1), Math.Clamp(pInScreen.Y / H, 0, 1));
    }

    /// <summary>Ảnh BGRA hiện tại (chụp nhanh từ lưới).</summary>
    public BitmapSource? Snapshot => bmp;
}
