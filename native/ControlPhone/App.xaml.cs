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
        Store.Load();
        I18n.Init(Store.Ui.Lang);
        Theme.Apply(Store.Ui.Theme);
        base.OnStartup(e);
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Store.Flush();
        Store.FlushUi();
        try { single?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
