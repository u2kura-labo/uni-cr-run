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

    /// <summary>
    /// ドットをそのまま n 倍に並べる（色を混ぜない）。
    /// にじませる拡大だと細い文字の線がぼけて、1桁の数字や短い名前が丸ごと読み落とされる。
    /// </summary>
    public static BgraImage Enlarge(BgraImage src, int n)
    {
        if (n <= 1) return src;
        var w = src.Width * n;
        var pixels = new byte[w * src.Height * n * 4];
        for (var sy = 0; sy < src.Height; sy++)
        for (var sx = 0; sx < src.Width; sx++)
        {
            var i = (sy * src.Width + sx) * 4;
            for (var dy = 0; dy < n; dy++)
            {
                var o = ((sy * n + dy) * w + sx * n) * 4;
                for (var dx = 0; dx < n; dx++, o += 4)
                {
                    pixels[o] = src.Pixels[i];
                    pixels[o + 1] = src.Pixels[i + 1];
                    pixels[o + 2] = src.Pixels[i + 2];
                    pixels[o + 3] = src.Pixels[i + 3];
                }
            }
        }
        return new BgraImage(pixels, w, src.Height * n);
    }

    /// <summary>
    /// 色の鮮やかさを、字の濃さに置き換える（鮮やかなほど黒くする）。
    /// チーム合計の欄は色つきの字が模様の上に乗っていて、明るさだけでは字と下地が分かれない。
    /// </summary>
    public static BgraImage ColourToInk(BgraImage src)
    {
        var pixels = new byte[src.Pixels.Length];
        for (var i = 0; i < src.Pixels.Length; i += 4)
        {
            var b = src.Pixels[i];
            var g = src.Pixels[i + 1];
            var r = src.Pixels[i + 2];
            var saturation = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            var ink = (byte)Math.Clamp(255 - saturation * 2.2, 0, 255);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = ink;
            pixels[i + 3] = 255;
        }
        return new BgraImage(pixels, src.Width, src.Height);
    }

    /// <summary>PNG のバイト列にする（Tesseract に渡すのに使う）。</summary>
    public static byte[] ToPng(BgraImage src)
    {
        var bitmap = BitmapSource.Create(src.Width, src.Height, 96, 96, PixelFormats.Bgra32, null, src.Pixels, src.Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
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
