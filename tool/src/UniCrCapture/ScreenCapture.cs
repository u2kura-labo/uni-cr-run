using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UniCrCapture.Core;
using static UniCrCapture.NativeMethods;

namespace UniCrCapture;

/// <summary>
/// FF14 のウィンドウ（見つからなければ画面全体）を撮る。
/// ゲームのメモリや通信には触れず、画面に映っているものを写すだけ。
/// ボーダーレスウィンドウ（仮想フルスクリーン）かウィンドウモードで動く。
/// </summary>
internal static class ScreenCapture
{
    private static readonly string[] GameProcesses = { "ffxiv_dx11", "ffxiv" };

    public sealed record Shot(BitmapSource Image, bool FoundGame);

    public static Shot Capture()
    {
        var (x, y, w, h, found) = FindGameArea();
        return new Shot(CaptureRect(x, y, w, h), found);
    }

    private static (int X, int Y, int W, int H, bool Found) FindGameArea()
    {
        foreach (var name in GameProcesses)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                var hwnd = p.MainWindowHandle;
                if (hwnd == IntPtr.Zero || IsIconic(hwnd)) continue;
                if (!GetClientRect(hwnd, out var rc)) continue;
                var origin = new POINT();
                if (!ClientToScreen(hwnd, ref origin)) continue;
                var w = rc.Right - rc.Left;
                var h = rc.Bottom - rc.Top;
                if (w > 200 && h > 200) return (origin.X, origin.Y, w, h, true);
            }
        }
        return (0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN), false);
    }

    private static BitmapSource CaptureRect(int x, int y, int w, int h)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, w, h);
        var old = SelectObject(memDc, bitmap);
        try
        {
            BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY | CAPTUREBLT);
            SelectObject(memDc, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static BitmapSource Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static void SavePng(BitmapSource image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>BGRA の画素の並びにする（OCR と色の判定に使う）。</summary>
    public static BgraImage ToBgra(BitmapSource image)
    {
        var converted = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var w = converted.PixelWidth;
        var h = converted.PixelHeight;
        var pixels = new byte[w * h * 4];
        converted.CopyPixels(pixels, w * 4, 0);
        return new BgraImage(pixels, w, h);
    }
}

internal sealed class BgraImage(byte[] pixels, int width, int height) : IPixelSource
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;

    public (byte R, byte G, byte B) GetPixel(int x, int y)
    {
        var i = (y * Width + x) * 4;
        return (Pixels[i + 2], Pixels[i + 1], Pixels[i]);
    }
}
