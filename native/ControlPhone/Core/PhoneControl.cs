using ControlPhone.Scrcpy;

namespace ControlPhone.Core;

/// <summary>Các thao tác có tên (nút Home, Quay lại, Nguồn…), gửi tới nhiều máy cùng lúc.</summary>
public static class PhoneControl
{
    public static void Act(IEnumerable<PhoneDevice> devices, string name)
    {
        foreach (var d in devices)
        {
            if (d.Recovery is { } rec) { ActRecovery(rec, name); continue; }
            var s = d.ControlSession;
            if (s == null) continue;
            switch (name)
            {
                case "home": s.KeyPress(AndroidKey.Home); break;
                case "back": s.KeyPress(AndroidKey.Back); break;
                case "recents": s.KeyPress(AndroidKey.AppSwitch); break;
                case "menu": s.KeyPress(AndroidKey.Menu); break;
                case "power": s.KeyPress(AndroidKey.Power); break;
                case "volup": s.KeyPress(AndroidKey.VolumeUp); break;
                case "voldown": s.KeyPress(AndroidKey.VolumeDown); break;
                case "mute": s.KeyPress(164); break;
                case "wake": s.KeyPress(AndroidKey.Wakeup); break;
                case "sleep": s.KeyPress(AndroidKey.Sleep); break;
                case "enter": s.KeyPress(AndroidKey.Enter); break;
                case "notif": s.Send(ControlMessages.Empty(ControlMessages.ExpandNotificationPanel)); break;
                case "quick": s.Send(ControlMessages.Empty(ControlMessages.ExpandSettingsPanel)); break;
                case "collapse": s.Send(ControlMessages.Empty(ControlMessages.CollapsePanels)); break;
                case "rotate": s.Send(ControlMessages.Empty(ControlMessages.RotateDevice)); break;
                case "screenoff": s.Send(ControlMessages.DisplayPower(false)); break;
                case "screenon": s.Send(ControlMessages.DisplayPower(true)); break;
                case "copy": s.Send(ControlMessages.GetClipboardMsg(1)); break;
                case "getclip": s.Send(ControlMessages.GetClipboardMsg(0)); break;
                case "unlock": _ = UnlockAsync(s); break;
            }
        }
    }

    /// <summary>Máy đang ở recovery (TWRP): chỉ có phím điều hướng / nguồn / âm lượng và vuốt thanh mở khoá.</summary>
    static void ActRecovery(Recovery.RecoverySession r, string name)
    {
        switch (name)
        {
            case "home": r.KeyPress(Recovery.RecoverySession.KeyHome); break;
            case "back": r.KeyPress(Recovery.RecoverySession.KeyBack); break;
            case "power": case "wake": case "sleep": r.KeyPress(Recovery.RecoverySession.KeyPower); break;
            case "volup": r.KeyPress(Recovery.RecoverySession.KeyVolumeUp); break;
            case "voldown": r.KeyPress(Recovery.RecoverySession.KeyVolumeDown); break;
            // TWRP tự khoá màn hình sau thời gian chờ: thanh "Swipe to Unlock" nằm sát đáy, vuốt từ trái sang phải
            case "unlock": _ = r.SwipeAsync(0.10, 0.865, 0.93, 0.865); break;
        }
    }

    /// <summary>Bật màn hình + một lần vuốt lên để mở màn hình khoá (không mật khẩu).</summary>
    static async Task UnlockAsync(ScrcpySession s)
    {
        s.KeyPress(AndroidKey.Wakeup);
        await Task.Delay(350);
        s.Touch(ControlMessages.ActionDown, 0.5, 0.85);
        for (int i = 1; i <= 8; i++)
        {
            await Task.Delay(16);
            double y = 0.85 - 0.55 * i / 8;
            s.Touch(i == 8 ? ControlMessages.ActionUp : ControlMessages.ActionMove, 0.5, y);
        }
    }
}
