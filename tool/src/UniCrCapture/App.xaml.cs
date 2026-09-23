using System.Windows;
using System.Windows.Threading;

namespace UniCrCapture;

public partial class App : Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 画面に出さずに、フォルダの画像をまとめて読み込む
        //   ConflictRecordCapture.exe --import <画像フォルダ> [--quiet]
        if (e.Args.Length >= 2 && e.Args[0] == "--import")
        {
            _ = ImportAsync(e.Args[1], e.Args.Contains("--quiet"));
            return;
        }

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

    /// <summary>
    /// フォルダの中の画像をまとめて読み込んで、戦績のファイルに追記する。
    /// 結果は images フォルダの import-result.txt に書く。
    /// </summary>
    private async Task ImportAsync(string images, bool quiet)
    {
        var settings = AppSettings.Load();
        var service = new CaptureService(settings);
        var report = new System.Text.StringBuilder();
        int saved = 0, known = 0, failed = 0;

        try
        {
            foreach (var file in Directory.GetFiles(images, "*.png").OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                try
                {
                    var outcome = await service.ProcessAsync(ScreenCapture.Load(file), File.GetLastWriteTime(file), file);
                    if (!outcome.Ok) failed++;
                    else if (outcome.Title == "保存済みの試合です") known++;
                    else saved++;
                    report.AppendLine($"{name}: {(outcome.Ok ? "OK" : "NG")} {outcome.Title}");
                    foreach (var d in outcome.Details) report.AppendLine($"    {d}");
                }
                catch (Exception ex)
                {
                    failed++;
                    report.AppendLine($"{name}: NG {ex.Message}");
                }
            }

            var summary = $"保存 {saved} 試合・保存済み {known} 試合・失敗 {failed} 枚";
            report.Insert(0, summary + Environment.NewLine + Environment.NewLine);
            File.WriteAllText(Path.Combine(images, "import-result.txt"), report.ToString(), new System.Text.UTF8Encoding(false));
            if (!quiet) MessageBox.Show(summary, "Conflict Record Capture：読み込みが終わりました");
        }
        catch (Exception ex)
        {
            if (!quiet) MessageBox.Show(ex.Message, "Conflict Record Capture：読み込めませんでした");
        }
        finally
        {
            Shutdown();
        }
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
