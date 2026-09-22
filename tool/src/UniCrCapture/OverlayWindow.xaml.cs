using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using UniCrCapture.Core;
using static UniCrCapture.NativeMethods;

namespace UniCrCapture;

/// <summary>FF14 の上に出しておく小さなボタン。押すと撮影して保存する。</summary>
public partial class OverlayWindow : Window
{
    public const string ViewerUrl = "https://u2kura-labo.github.io/uni-cr-run/";
    private const int HotkeyId = 0xC1;

    private readonly AppSettings _settings;
    private readonly CaptureService _capture;
    private readonly DispatcherTimer _topmost = new() { Interval = TimeSpan.FromSeconds(2) };
    private IntPtr _hwnd;
    private bool _busy;
    private Point? _pressAt;
    private bool _dragged;
    private ToastWindow? _toast;

    internal OverlayWindow(AppSettings settings)
    {
        _settings = settings;
        _capture = new CaptureService(settings);
        InitializeComponent();

        var area = SystemParameters.WorkArea;
        Left = settings.ButtonLeft ?? area.Right - 80;
        Top = settings.ButtonTop ?? area.Top + area.Height * 0.35;
        KeepOnScreen();

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => UnregisterHotKey(_hwnd, HotkeyId);
        Root.MouseEnter += (_, _) => Root.Opacity = 1;
        Root.MouseLeave += (_, _) => Root.Opacity = 0.8;
        Root.MouseLeftButtonDown += OnPress;
        Root.MouseMove += OnMove;
        Root.MouseLeftButtonUp += OnRelease;
        Root.MouseRightButtonUp += (_, _) => ShowMenu();

        // ゲームが前に出てきても、ボタンが隠れないように定期的に最前面へ
        _topmost.Tick += (_, _) => SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        _topmost.Start();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // 押してもゲームからフォーカスを奪わない・タスクバーや Alt+Tab に出さない
        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST));
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        RegisterHotkey();
    }

    private void RegisterHotkey()
    {
        UnregisterHotKey(_hwnd, HotkeyId);
        if (!Hotkey.TryParse(_settings.Hotkey, out var mods, out var vk)) return;
        if (!RegisterHotKey(_hwnd, HotkeyId, mods, vk))
            ShowToast(false, "ホットキーを登録できませんでした", new[] { $"{_settings.Hotkey} は他のアプリが使っているかもしれません。設定で変えてください。" });
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _ = CaptureAsync();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---------- クリックとドラッグ ----------

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        _pressAt = e.GetPosition(this);
        _dragged = false;
        Root.CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_pressAt is null || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(this);
        if (!_dragged && (Math.Abs(now.X - _pressAt.Value.X) > 4 || Math.Abs(now.Y - _pressAt.Value.Y) > 4))
        {
            _dragged = true;
            Root.ReleaseMouseCapture();
            DragMove();
            _pressAt = null;
            KeepOnScreen();
            _settings.ButtonLeft = Left;
            _settings.ButtonTop = Top;
            _settings.Save();
        }
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        Root.ReleaseMouseCapture();
        var clicked = _pressAt is not null && !_dragged;
        _pressAt = null;
        if (clicked) _ = CaptureAsync();
    }

    private void KeepOnScreen()
    {
        var v = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Left = Math.Clamp(Left, v.Left, v.Right - 52);
        Top = Math.Clamp(Top, v.Top, v.Bottom - 52);
    }

    // ---------- 撮影 ----------

    private async Task CaptureAsync()
    {
        if (_busy) return;
        _busy = true;
        SetStatus(Brushes.Transparent);
        var toastWasOpen = _toast is not null;
        _toast?.Close();
        try
        {
            // 自分（ボタンと通知）が写り込まないように、いったん消してから撮る
            Opacity = 0;
            await Task.Delay(toastWasOpen ? 160 : 80);
            var at = DateTimeOffset.Now;
            var shot = ScreenCapture.Capture();
            Opacity = 1;
            SetStatus((Brush)FindResource("Accent"));

            var outcome = await _capture.ProcessAsync(shot.Image, at);
            var details = outcome.Details.ToList();
            if (!shot.FoundGame) details.Add("FF14 のウィンドウが見つからなかったので、画面全体を撮りました。");
            ShowToast(outcome.Ok, outcome.Title, details);
            await FlashAsync(outcome.Ok ? (outcome.Details.Any(d => d.StartsWith("要確認")) ? "Warning" : "Good") : "Critical");
        }
        catch (Exception ex)
        {
            Opacity = 1;
            ShowToast(false, "撮影できませんでした", new[] { ex.Message });
            await FlashAsync("Critical");
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetStatus(Brush brush) => StatusRing.Stroke = brush;

    private async Task FlashAsync(string resource)
    {
        SetStatus((Brush)FindResource(resource));
        await Task.Delay(2500);
        SetStatus(Brushes.Transparent);
    }

    private void ShowToast(bool ok, string title, IReadOnlyList<string> details)
    {
        _toast?.Close();
        _toast = new ToastWindow(ok, title, details);
        _toast.Closed += (s, _) => { if (ReferenceEquals(_toast, s)) _toast = null; };
        _toast.ShowNextTo(new Rect(Left, Top, ActualWidth, ActualHeight));
    }

    // ---------- 右クリックのメニュー ----------

    private void ShowMenu()
    {
        var menu = new ContextMenu();

        var job = new MenuItem { Header = $"自分のジョブ：{GameData.JobName(_settings.SelfJob)}" };
        foreach (var group in GameData.Jobs.GroupBy(j => j.Role))
        {
            var sub = new MenuItem { Header = group.Key };
            foreach (var j in group)
            {
                var item = new MenuItem { Header = j.Name, IsCheckable = true, IsChecked = _settings.SelfJob == j.Code };
                item.Click += (_, _) => { _settings.SelfJob = j.Code; _settings.Save(); };
                sub.Items.Add(item);
            }
            job.Items.Add(sub);
        }
        menu.Items.Add(job);

        var map = new MenuItem { Header = $"次の試合のマップ：{_capture.NextMap ?? "指定しない"}" };
        foreach (var name in GameData.Maps.Prepend(null))
        {
            var item = new MenuItem { Header = name ?? "指定しない", IsCheckable = true, IsChecked = _capture.NextMap == name };
            item.Click += (_, _) => _capture.NextMap = name;
            map.Items.Add(item);
        }
        menu.Items.Add(map);
        menu.Items.Add(new Separator());

        menu.Items.Add(Item("今すぐ撮る", () => _ = CaptureAsync()));
        menu.Items.Add(Item("保存フォルダを開く", OpenFolder));
        menu.Items.Add(Item("スクリーンショットから読み込む…", () => _ = ImportImagesAsync()));
        menu.Items.Add(Item("ビューアを開く（Web）", () => Open(ViewerUrl)));
        menu.Items.Add(Item("設定…", () => OpenSettings(firstRun: false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("終了", () => Application.Current.Shutdown()));

        menu.PlacementTarget = Root;
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(_settings.SaveFolder);
        Open(_settings.SaveFolder);
    }

    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    internal void OpenSettings(bool firstRun)
    {
        var window = new SettingsWindow(_settings, firstRun);
        if (window.ShowDialog() == true)
        {
            _settings.Save();
            RegisterHotkey();
        }
    }

    /// <summary>あとから：保存してあるスクリーンショット（ゲームのスクショ機能で撮ったものなど）を読み込む。</summary>
    private async Task ImportImagesAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "リザルト画面のスクリーンショットを選ぶ",
            Filter = "画像 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;

        _busy = true;
        SetStatus((Brush)FindResource("Accent"));
        int saved = 0, known = 0;
        var failed = new List<string>();
        try
        {
            foreach (var file in dialog.FileNames)
            {
                try
                {
                    var outcome = await _capture.ProcessAsync(ScreenCapture.Load(file), File.GetLastWriteTime(file), file);
                    if (!outcome.Ok) failed.Add($"{Path.GetFileName(file)}：{outcome.Details.FirstOrDefault()}");
                    else if (outcome.Title == "保存済みの試合です") known++;
                    else saved++;
                }
                catch (Exception ex)
                {
                    failed.Add($"{Path.GetFileName(file)}：{ex.Message}");
                }
            }
        }
        finally
        {
            _busy = false;
        }
        var lines = new List<string> { $"保存 {saved} 試合・保存済み {known} 試合・失敗 {failed.Count} 枚" };
        lines.AddRange(failed.Take(5));
        ShowToast(failed.Count == 0, "スクリーンショットを読み込みました", lines);
        await FlashAsync(failed.Count == 0 ? "Good" : "Warning");
    }
}
