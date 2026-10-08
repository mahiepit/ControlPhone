using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ControlPhone.Adb;
using ControlPhone.Core;
using ControlPhone.Video;

namespace ControlPhone;

public partial class MainWindow : Window
{
    readonly AppConfig config = new();
    readonly DeviceManager manager;
    readonly DispatcherTimer statsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    long lastFrames, lastBytes;
    TimeSpan lastCpu;
    readonly Stopwatch statsClock = Stopwatch.StartNew();

    public MainWindow()
    {
        InitializeComponent();
        manager = new DeviceManager(Dispatcher, config);
        manager.Log += m => Dispatcher.BeginInvoke(() => LogText.Text = $"{DateTime.Now:HH:mm:ss}  {m}");
        Grid.ItemsSource = manager.Devices;
        manager.Devices.CollectionChanged += (_, _) => UpdateCount();
        SizeSlider.ValueChanged += (_, e) =>
        {
            PhoneTile.TileWidth = e.NewValue;
            foreach (var t in PhoneTile.Live) t.SetTileWidth(e.NewValue);
        };
        CompositionTarget.Rendering += OnRender;
        statsTimer.Tick += (_, _) => UpdateStats();
        Loaded += (_, _) =>
        {
            try { LogText.Text = $"FFmpeg avcodec {Ffmpeg.VersionString(Ffmpeg.avcodec_version())} · ADB: {AdbClient.AdbPath} · dữ liệu: {config.DataDir}"; }
            catch (Exception e) { LogText.Text = "Không nạp được FFmpeg: " + e.Message; }
            manager.Start();
            statsTimer.Start();
        };
        Closed += (_, _) => manager.Dispose();
    }

    void OnRender(object? sender, EventArgs e)
    {
        foreach (var t in PhoneTile.Live) t.UpdateFrame();
    }

    void UpdateCount()
    {
        int online = manager.Devices.Count(d => d.Status == PhoneStatus.Online);
        CountText.Text = $"● {online} / {manager.Devices.Count} online";
    }

    void UpdateStats()
    {
        UpdateCount();
        long frames = 0, bytes = 0;
        foreach (var d in manager.Devices)
        {
            frames += d.Frames?.Frames ?? 0;
            bytes += d.Session?.BytesReceived ?? 0;
        }
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        double sec = statsClock.Elapsed.TotalSeconds;
        statsClock.Restart();
        double fps = Math.Max(0, frames - lastFrames) / sec;
        double mbps = Math.Max(0, bytes - lastBytes) * 8 / sec / 1e6;
        double cpuPct = (cpu - lastCpu).TotalSeconds / sec / Environment.ProcessorCount * 100;
        lastFrames = frames; lastBytes = bytes; lastCpu = cpu;
        StatsText.Text = $"{fps:0} khung/giây · {mbps:0.0} Mbps · CPU {cpuPct:0}% ({Environment.ProcessorCount} luồng) · RAM {Process.GetCurrentProcess().WorkingSet64 / 1048576} MB";
    }
}
