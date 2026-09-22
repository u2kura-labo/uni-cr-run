using System.Windows;
using System.Windows.Threading;

namespace UniCrCapture;

public partial class App : Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 二重起動しない（ボタンが2つ出ないように）
        _single = new Mutex(true, "ConflictRecordCapture.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("Conflict Record Capture はもう起動しています。", "Conflict Record Capture");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        var settings = AppSettings.Load();
        var overlay = new OverlayWindow(settings);
        overlay.Show();
        if (string.IsNullOrWhiteSpace(settings.SelfName)) overlay.OpenSettings(firstRun: true);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Conflict Record Capture：エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}
