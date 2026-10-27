using System.Windows;
using System.Windows.Controls;
using ControlPhone.Core;

namespace ControlPhone;

/// <summary>Chế độ "Chỉ hiển thị 1 điện thoại": ẩn lưới / thanh bên, màn hình lớn chiếm cả cửa sổ, chỉ kết nối máy đã chọn.</summary>
public partial class MainWindow
{
    void ApplyModeUi()
    {
        bool one = single != null;
        BtnSingleMode.Visibility = one ? Visibility.Collapsed : Visibility.Visible;
        BtnPickPhone.Visibility = BtnAllMode.Visibility = one ? Visibility.Visible : Visibility.Collapsed;
        BtnSingleMode.Click += (_, _) => SwitchMode("pick");
        BtnPickPhone.Click += (_, _) => SwitchMode("pick");
        BtnAllMode.Click += (_, _) => SwitchMode("all");
        if (!one) return;

        Title = "ControlPhone — " + T("Chỉ hiển thị 1 điện thoại");
        foreach (var el in new UIElement[] { BtnSidebar, Sidebar, BtnSync, BtnSolo, SortSel, BtnHideOff, BtnFit, SizeBox, BtnLive, VPrev, VNext, VClose })
            el.Visibility = Visibility.Collapsed;
        if (StatSel.Parent is UIElement selBadge) selBadge.Visibility = Visibility.Collapsed; // "x đã chọn" không có nghĩa khi chỉ 1 máy
        // lưới + thanh thao tác ẩn; màn hình lớn chiếm cột giữa (toàn bộ chiều rộng), canh giữa
        if (GridArea.Parent is FrameworkElement main) main.Visibility = Visibility.Collapsed;
        Grid.SetColumn(ViewerPane, 1);
        ViewerPane.BorderThickness = new Thickness(0);
        VBody.HorizontalAlignment = HorizontalAlignment.Center;
        ViewerPane.Visibility = Visibility.Visible;
        VTitle.Text = T("Đang kết nối…");
        VSub.Text = single;
        VScreen.Width = 360; VScreen.Height = 740;
        VOvText.Text = T("Đang chờ điện thoại… (cắm USB và bật Gỡ lỗi USB)");
    }

    void SwitchMode(string modeArgs)
    {
        App.RestartIn(modeArgs);
        Close();
    }

    /// <summary>Gọi mỗi lần danh sách máy đổi: mở màn hình lớn khi máy đã sẵn sàng, hiện trạng thái khi chưa / mất kết nối.</summary>
    void UpdateSingleMode()
    {
        if (single == null) return;
        var d = manager.Devices.FirstOrDefault(x => !x.Pending);
        if (d != null && viewerId == null && d.ActiveAddr != null) OpenViewer(d.Id);
        string? wait = null;
        if (d == null)
        {
            var pend = manager.Devices.FirstOrDefault(x => x.Pending);
            wait = pend != null && pend.Error != "" ? I18n.T(pend.Error) : T("Đang chờ điện thoại… (cắm USB và bật Gỡ lỗi USB)");
        }
        else if (d.Status == "offline") wait = T("Mất kết nối — đang chờ máy kết nối lại…");
        else if (!d.IsOnline && d.Error != "") wait = I18n.T(d.Error);
        if (wait != null) { VOvText.Text = wait; VOv.Visibility = Visibility.Visible; }
        else if (VOv.Visibility == Visibility.Visible && vBmp == null) VOvText.Text = T("Đang mở luồng nét…");
    }
}
