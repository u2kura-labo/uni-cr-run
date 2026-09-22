using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using UniCrCapture.Core;
using OcrWord = UniCrCapture.Core.OcrWord;

namespace UniCrCapture;

/// <summary>Windows に入っている文字認識（Windows.Media.Ocr）。無料・オフラインで動く。</summary>
internal static class WindowsOcr
{
    private static OcrEngine? _engine;

    private static OcrEngine Engine =>
        _engine ??= OcrEngine.TryCreateFromLanguage(new Language("ja"))
            ?? OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                "日本語の文字認識が使えません。Windows の「設定 → 時刻と言語 → 言語と地域」で日本語を追加してください。");

    public static bool HasJapanese => OcrEngine.IsLanguageSupported(new Language("ja"));

    /// <summary>画像を OCR して、語と位置（元の画像の座標）を返す。</summary>
    public static Task<List<OcrWord>> RecognizeAsync(BitmapSource image)
    {
        // 小さい文字は拡大したほうが読みやすいので、上限の範囲で 2 倍まで広げる
        var longest = Math.Max(image.PixelWidth, image.PixelHeight);
        return ReadAsync(image, Math.Min(2.0, (double)OcrEngine.MaxImageDimension / longest));
    }

    /// <summary>
    /// 画像の一部だけを、整数倍に拡大して読み直す。座標は元の画像のものに直して返す。
    ///
    /// 拡大は「ドットをそのまま並べる」方法で行う。ふつうの拡大（にじませる補間）だと
    /// 細い数字の線がぼけて、1桁の数字が丸ごと読み落とされる。実際のスクリーンショット3枚では、
    /// にじませる拡大だと K / D の列が落ちたが、そのまま並べる拡大だと 30 個すべて読めた。
    /// </summary>
    public static async Task<List<OcrWord>> RecognizeAreaAsync(BitmapSource image, PixelRect area, int zoom, int pad = 12)
    {
        var x = Math.Clamp((int)Math.Floor(area.X) - pad, 0, Math.Max(0, image.PixelWidth - 1));
        var y = Math.Clamp((int)Math.Floor(area.Y) - pad, 0, Math.Max(0, image.PixelHeight - 1));
        var w = Math.Min(image.PixelWidth - x, (int)Math.Ceiling(area.Width) + pad * 2);
        var h = Math.Min(image.PixelHeight - y, (int)Math.Ceiling(area.Height) + pad * 2);
        if (w <= 0 || h <= 0) return new List<OcrWord>();

        var crop = new CroppedBitmap(image, new Int32Rect(x, y, w, h));
        crop.Freeze();
        // 拡大しすぎて OCR の上限を超えないようにする（MaxImageDimension は uint）
        var limit = (int)Math.Max(1, (double)OcrEngine.MaxImageDimension / Math.Max(w, h));
        var n = Math.Clamp(zoom, 1, limit);
        var words = await ReadBgraAsync(Enlarge(ScreenCapture.ToBgra(crop), n));
        return words
            .Select(t => t with { X = t.X / n + x, Y = t.Y / n + y, Width = t.Width / n, Height = t.Height / n })
            .ToList();
    }

    private static async Task<List<OcrWord>> ReadAsync(BitmapSource image, double scale)
    {
        var source = Math.Abs(scale - 1) < 0.01 ? image : new TransformedBitmap(image, new ScaleTransform(scale, scale));
        var words = await ReadBgraAsync(ScreenCapture.ToBgra(source));
        return words
            .Select(t => t with { X = t.X / scale, Y = t.Y / scale, Width = t.Width / scale, Height = t.Height / scale })
            .ToList();
    }

    private static async Task<List<OcrWord>> ReadBgraAsync(BgraImage bgra)
    {
        var buffer = CryptographicBuffer.CreateFromByteArray(bgra.Pixels);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, bgra.Width, bgra.Height, BitmapAlphaMode.Premultiplied);
        var result = await Engine.RecognizeAsync(bitmap);

        var words = new List<OcrWord>();
        foreach (var line in result.Lines)
        foreach (var word in line.Words)
        {
            var r = word.BoundingRect;
            words.Add(new OcrWord(word.Text, r.X, r.Y, r.Width, r.Height));
        }
        return words;
    }

    /// <summary>ドットをそのまま n 倍に並べる（色を混ぜない）。</summary>
    private static BgraImage Enlarge(BgraImage src, int n)
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
}
