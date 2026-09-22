using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using UniCrCapture.Core;
using static UniCrCapture.NativeMethods;

namespace UniCrCapture;

/// <summary>FF14 の上に出しておく小さなボタン。クリスタルで撮影、歯車で設定・停止・終了。</summary>
public partial class OverlayWindow : Window
{
    public const string ViewerUrl = "https://u2kura-labo.github.io/uni-cr-run/";
    private const int HotkeyId = 0xC1;

    // ボタン2つぶんの大きさ（画面の外に出ないように位置を丸めるのに使う）
    private const double WidgetWidth = 90;
    private const double WidgetHeight = 52;

    private readonly AppSettings _settings;
    private readonly CaptureService _capture;
    private readonly DispatcherTimer _topmost = new() { Interval = TimeSpan.FromSeconds(2) };
    private IntPtr _hwnd;
    private bool _busy;
    private bool _paused;
    private Point? _pressAt;
    private UIElement? _pressedOn;
    private bool _dragged;
    private ToastWindow? _toast;

    internal OverlayWindow(AppSettings settings)
    {
        _settings = settings;
        _capture = new CaptureService(settings);
        InitializeComponent();

        var area = SystemParameters.WorkArea;
        Left = settings.ButtonLeft ?? area.Right - WidgetWidth - 28;
        Top = settings.ButtonTop ?? area.Top + area.Height * 0.35;
        KeepOnScreen();

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => UnregisterHotKey(_hwnd, HotkeyId);
        Root.MouseEnter += (_, _) => Root.Opacity = 1;
        Root.MouseLeave += (_, _) => Root.Opacity = 0.8;
        foreach (var button in new UIElement[] { CaptureButton, MenuButton })
        {
            button.MouseLeftButtonDown += OnPress;
            button.MouseMove += OnMove;
            button.MouseLeftButtonUp += OnRelease;
            button.MouseRightButtonUp += (_, _) => ShowMenu();
        }

        // ゲームが前に出てきても、ボタンが隠れないように定期的に最前面へ
        _topmost.Tick += (_, _) => SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        _topmost.Start();
    }

    /// <summary>撮影を止めているあいだは、ボタンもホットキーも効かない。</summary>
    internal bool Paused => _paused;

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
        if (_paused) return;
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
        _pressedOn = sender as UIElement;
        _dragged = false;
        _pressedOn?.CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_pressAt is null || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(this);
        if (!_dragged && (Math.Abs(now.X - _pressAt.Value.X) > 4 || Math.Abs(now.Y - _pressAt.Value.Y) > 4))
        {
            _dragged = true;
            _pressedOn?.ReleaseMouseCapture();
            _pressAt = null;
            _pressedOn = null;
            DragMove();
            KeepOnScreen();
            _settings.ButtonLeft = Left;
            _settings.ButtonTop = Top;
            _settings.Save();
        }
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        _pressedOn?.ReleaseMouseCapture();
        var clicked = !_dragged && _pressAt is not null ? _pressedOn : null;
        _pressAt = null;
        _pressedOn = null;
        if (ReferenceEquals(clicked, MenuButton)) ShowMenu();
        else if (clicked is not null) _ = CaptureAsync();
    }

    private void KeepOnScreen()
    {
        var v = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Left = Math.Clamp(Left, v.Left, Math.Max(v.Left, v.Right - WidgetWidth));
        Top = Math.Clamp(Top, v.Top, Math.Max(v.Top, v.Bottom - WidgetHeight));
    }

    // ---------- 撮影 ----------

    private async Task CaptureAsync()
    {
        if (_busy) return;
        if (_paused)
        {
            ShowToast(true, "いまは停止中です", new[] { "歯車のボタンから「撮影を開始する」を選ぶと、また撮れるようになります。" }, tone: "Border");
            return;
        }
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
        SetStatus(_paused ? (Brush)FindResource("Border") : Brushes.Transparent);
    }

    private void ShowToast(bool ok, string title, IReadOnlyList<string> details, string? tone = null)
    {
        _toast?.Close();
        _toast = new ToastWindow(ok, title, details, tone);
        _toast.Closed += (s, _) => { if (ReferenceEquals(_toast, s)) _toast = null; };
        _toast.ShowNextTo(new Rect(Left, Top, ActualWidth, ActualHeight));
    }

    // ---------- 停止と開始 ----------

    /// <summary>撮影を止める／また始める。ホットキーの登録も合わせて切り替える。</summary>
    internal void SetPaused(bool paused, bool notify = true)
    {
        if (_paused == paused) return;
        _paused = paused;

        CrystalIcon.Opacity = paused ? 0.18 : 1;
        PauseMark.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(paused ? (Brush)FindResource("Border") : Brushes.Transparent);
        CaptureButton.ToolTip = paused
            ? "停止中です\n歯車のボタンから「撮影を開始する」を選んでください"
            : "リザルト画面で押すと保存します\nドラッグで移動できます";

        if (paused) UnregisterHotKey(_hwnd, HotkeyId);
        else RegisterHotkey();

        if (!notify) return;
        ShowToast(true, paused ? "撮影を停止しました" : "撮影を開始しました",
            paused
                ? new[] { "ボタンもホットキーも効きません。歯車のボタンから、また開始できます。" }
                : new[] { $"ボタンかホットキー（{_settings.Hotkey}）で撮れます。" },
            tone: paused ? "Border" : "Good");
    }

    /// <summary>終了する。歯車のメニューと設定の窓から呼ぶ。</summary>
    internal void Quit()
    {
        _toast?.Close();
        Application.Current.Shutdown();
    }

    // ---------- 歯車のメニュー ----------

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

        if (!_paused) menu.Items.Add(Item("今すぐ撮る", () => _ = CaptureAsync()));
        menu.Items.Add(Item(_paused ? "撮影を開始する" : "撮影を停止する", () => SetPaused(!_paused)));
        menu.Items.Add(new Separator());

        menu.Items.Add(Item("保存フォルダを開く", OpenFolder));
        menu.Items.Add(Item("スクリーンショットから読み込む…", () => _ = ImportImagesAsync()));
        menu.Items.Add(Item("ビューアを開く（Web）", () => Open(ViewerUrl)));
        menu.Items.Add(Item("設定…", () => OpenSettings(firstRun: false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("終了", Quit));

        // メニューを出しているあいだだけ、ふつうのウィンドウとして扱えるようにする
        // （WS_EX_NOACTIVATE のままだと、メニューが選べなかったり、すぐ閉じたりする）
        AllowActivation(true);
        Activate();
        menu.Closed += (_, _) => AllowActivation(false);

        menu.PlacementTarget = MenuButton;
        menu.Placement = PlacementMode.Left;
        menu.IsOpen = true;
    }

    /// <summary>フォーカスを受け取れるようにする／しない（ふだんはゲームから奪わない）。</summary>
    private void AllowActivation(bool allow)
    {
        if (_hwnd == IntPtr.Zero) return;
        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        ex = allow ? ex & ~WS_EX_NOACTIVATE : ex | WS_EX_NOACTIVATE;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        // メニューが閉じきってから動かす（設定の窓などが、開いたままのメニューの下に出ないように）
        item.Click += (_, _) => item.Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
        return item;
    }

    internal void OpenFolder()
    {
        Directory.CreateDirectory(_settings.SaveFolder);
        Open(_settings.SaveFolder);
    }


    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    internal void OpenSettings(bool firstRun)
    {
        // 設定の窓は文字を打てないと困るので、開いているあいだは活性化を許す
        AllowActivation(true);
        try
        {
            var window = new SettingsWindow(_settings, firstRun, this);
            if (window.ShowDialog() == true)
            {
                _settings.Save();
                RegisterHotkey();
            }
        }
        finally
        {
            AllowActivation(false);
        }
    }

    /// <summary>あとから：保存してあるスクリーンショット（ゲームのスクショ機能で撮ったものなど）を読み込む。</summary>
    internal async Task ImportImagesAsync()
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
