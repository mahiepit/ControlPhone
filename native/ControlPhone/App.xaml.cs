using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ControlPhone.Core;

namespace ControlPhone;

public partial class App : Application
{
    static Mutex? single;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

    protected override void OnStartup(StartupEventArgs e)
    {
        // chỉ chạy 1 bản: mở lần 2 → đưa cửa sổ đang chạy lên trước
        single = new Mutex(true, "ControlPhone.Native.SingleInstance", out bool first);
        if (!first && !e.Args.Contains("--restart"))
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName).Where(p => p.Id != me.Id && p.MainWindowHandle != IntPtr.Zero))
            {
                ShowWindow(p.MainWindowHandle, 9);
                SetForegroundWindow(p.MainWindowHandle);
            }
            Shutdown();
            return;
        }
        if (!first) { try { single.WaitOne(10000); } catch (AbandonedMutexException) { } } // khởi động lại (đổi ngôn ngữ): chờ bản cũ thoát
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "ControlPhone", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
        // tránh các lần dọn rác dài làm khựng hình / chuột (luồng video cấp phát liên tục)
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        OfferPreviousData();
        Store.Load();
        I18n.Init(Store.Ui.Lang);
        Theme.Apply(Store.Ui.Theme);
        base.OnStartup(e);
        // chọn chế độ: điều khiển tất cả máy / chỉ 1 điện thoại. "--mode all|pick|single <addr>" khi app tự khởi động lại.
        string? singleAddr = null;
        var args = e.Args.ToList();
        int mi = args.IndexOf("--mode");
        string mode = mi >= 0 && mi + 1 < args.Count ? args[mi + 1] : SelfTest.On ? "all" : "ask";
        if (mode == "single" && mi + 2 < args.Count) singleAddr = args[mi + 2];
        else if (mode is "ask" or "pick")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown; // đóng cửa sổ chọn chế độ không được thoát app
            var mw = new ModeWindow(startWithList: mode == "pick");
            mw.ShowDialog();
            if (!mw.Chosen) { Shutdown(); return; }
            singleAddr = mw.SingleAddr;
        }
        var main = new MainWindow(singleAddr);
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

    /// <summary>Khởi động lại app ở chế độ khác (đóng cửa sổ hiện tại sau khi gọi).</summary>
    public static void RestartIn(string modeArgs) =>
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart --mode " + modeArgs) { UseShellExecute = false });

    /// <summary>
    /// Lần đầu chạy bản mới ở thư mục mới (chưa có data): tìm dữ liệu của bản cũ và hỏi có dùng không — để không mất
    /// số thứ tự / tên / nhóm khi cập nhật bằng cách giải nén ra thư mục khác.
    /// </summary>
    static void OfferPreviousData()
    {
        if (SelfTest.On || Environment.GetEnvironmentVariable("CP_DATA_DIR") != null || System.IO.File.Exists(Store.ConfigPath)) return;
        try
        {
            foreach (var c in DataImport.FindPrevious().Take(3))
            {
                var r = MessageBox.Show(
                    $"Tìm thấy dữ liệu của ControlPhone bản cũ:\n{c.Dir}\n{c.Phones} máy · sửa lần cuối {c.Modified:dd/MM/yyyy HH:mm}\n\n" +
                    "Dùng dữ liệu này cho bản mới (giữ số thứ tự, tên, nhóm, cài đặt)?\n\n" +
                    "Found data from an older ControlPhone (above). Use it for this version (keeps phone numbers, names, groups, settings)?",
                    "ControlPhone", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes) { DataImport.Import(c.Dir); return; }
            }
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "ControlPhone", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Store.Flush();
        Store.FlushUi();
        try { single?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
