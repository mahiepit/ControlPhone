using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ControlPhone.Core;
using ControlPhone.Scrcpy;

namespace ControlPhone;

/// <summary>Một ô điện thoại: hiện khung hình mới nhất và chuyển chuột thành thao tác chạm.</summary>
public partial class PhoneTile : UserControl
{
    WriteableBitmap? bmp;
    bool touching;

    public PhoneDevice? Device => DataContext as PhoneDevice;

    /// <summary>Các ô đang hiển thị (cửa sổ chính cập nhật hình cho chúng mỗi khung hình).</summary>
    public static readonly HashSet<PhoneTile> Live = [];
    public static double TileWidth = 200;

    public PhoneTile()
    {
        InitializeComponent();
        Loaded += (_, _) => { Live.Add(this); SetTileWidth(TileWidth); };
        Unloaded += (_, _) => Live.Remove(this);
        DataContextChanged += (_, _) => { bmp = null; Img.Source = null; };
        Img.MouseLeftButtonDown += OnDown;
        Img.MouseMove += OnMove;
        Img.MouseLeftButtonUp += OnUp;
        Img.LostMouseCapture += (_, _) => { if (touching) EndTouch(null); };
        Img.MouseRightButtonDown += (_, e) => { Device?.ControlSession?.KeyPress(AndroidKey.Back); e.Handled = true; };
        Img.MouseWheel += OnWheel;
        SizeChanged += (_, _) => FitScreen();
    }

    /// <summary>Chiều rộng ô (px); chiều cao màn hình theo tỉ lệ khung hình hiện tại của máy.</summary>
    public void SetTileWidth(double w)
    {
        Width = w;
        FitScreen();
    }

    void FitScreen()
    {
        double w = Width - 10 - 8; // margin + viền
        double aspect = bmp != null ? (double)bmp.PixelHeight / bmp.PixelWidth : 2.05;
        Screen.Height = Math.Max(40, w * aspect);
    }

    /// <summary>Gọi mỗi khung hình màn hình (luồng giao diện): chép khung mới nếu có.</summary>
    public void UpdateFrame()
    {
        var d = Device;
        var fb = d?.Frames;
        if (fb == null) return;
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

    // ---------- chạm ----------
    bool Norm(MouseEventArgs e, out double nx, out double ny)
    {
        var p = e.GetPosition(Img);
        nx = Img.ActualWidth > 0 ? p.X / Img.ActualWidth : 0;
        ny = Img.ActualHeight > 0 ? p.Y / Img.ActualHeight : 0;
        return Img.ActualWidth > 0;
    }

    void OnDown(object s, MouseButtonEventArgs e)
    {
        var sess = Device?.ControlSession;
        if (sess == null || !Norm(e, out var x, out var y)) return;
        touching = true;
        Img.CaptureMouse();
        sess.Touch(ControlMessages.ActionDown, x, y);
        e.Handled = true;
    }

    void OnMove(object s, MouseEventArgs e)
    {
        if (!touching || !Norm(e, out var x, out var y)) return;
        Device?.ControlSession?.Touch(ControlMessages.ActionMove, x, y);
    }

    void OnUp(object s, MouseButtonEventArgs e)
    {
        if (!touching) return;
        EndTouch(e);
        Img.ReleaseMouseCapture();
        e.Handled = true;
    }

    void EndTouch(MouseEventArgs? e)
    {
        touching = false;
        double x = 0.5, y = 0.5;
        if (e != null) Norm(e, out x, out y);
        Device?.ControlSession?.Touch(ControlMessages.ActionUp, x, y);
    }

    void OnWheel(object s, MouseWheelEventArgs e)
    {
        if (!Norm(e, out var x, out var y)) return;
        Device?.ControlSession?.Scroll(x, y, 0, e.Delta / 120f);
        e.Handled = true;
    }
}
