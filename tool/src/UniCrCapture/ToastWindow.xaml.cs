using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static UniCrCapture.NativeMethods;

namespace UniCrCapture;

/// <summary>ボタンの横に出る小さな通知。しばらくすると消える（クリックでも消える）。</summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;

    /// <summary>tone を渡すと色を決め打ちできる（例：停止のお知らせは、成功の緑ではなく灰色にする）。</summary>
    public ToastWindow(bool ok, string title, IReadOnlyList<string> details, string? tone = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        DetailList.ItemsSource = details;
        var warn = ok && details.Any(d => d.StartsWith("要確認"));
        Stripe.Background = (Brush)FindResource(tone ?? (!ok ? "Critical" : warn ? "Warning" : "Good"));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ok && !warn ? 5 : 10) };
        _timer.Tick += (_, _) => Close();
        MouseLeftButtonUp += (_, _) => Close();
        Closed += (_, _) => _timer.Stop();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        };
    }

    /// <summary>ボタンの左側（入らなければ右側）に出す。</summary>
    public void ShowNextTo(Rect button)
    {
        Opacity = 0;
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = button.Left - ActualWidth - 4 >= SystemParameters.VirtualScreenLeft
            ? button.Left - ActualWidth - 4
            : button.Right + 4;
        Top = Math.Clamp(button.Top + button.Height / 2 - ActualHeight / 2, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight));
        Opacity = 1;
        _timer.Start();
    }
}
