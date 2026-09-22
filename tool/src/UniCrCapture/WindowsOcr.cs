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
    /// 画像の一部だけを、大きく拡大して読み直す。座標は元の画像のものに直して返す。
    /// 1桁の数字が離れて並ぶ列（K / D / A）は、画面ぜんぶを渡すと丸ごと落ちることがあるが、
    /// その部分だけを切り出して 4 倍にすると読める。
    /// </summary>
    public static async Task<List<OcrWord>> RecognizeAreaAsync(BitmapSource image, PixelRect area, double zoom, int pad = 12)
    {
        var x = Math.Clamp((int)Math.Floor(area.X) - pad, 0, Math.Max(0, image.PixelWidth - 1));
        var y = Math.Clamp((int)Math.Floor(area.Y) - pad, 0, Math.Max(0, image.PixelHeight - 1));
        var w = Math.Min(image.PixelWidth - x, (int)Math.Ceiling(area.Width) + pad * 2);
        var h = Math.Min(image.PixelHeight - y, (int)Math.Ceiling(area.Height) + pad * 2);
        if (w <= 0 || h <= 0) return new List<OcrWord>();

        var crop = new CroppedBitmap(image, new Int32Rect(x, y, w, h));
        crop.Freeze();
        var limit = (double)OcrEngine.MaxImageDimension / Math.Max(w, h);
        var words = await ReadAsync(crop, Math.Max(1.0, Math.Min(zoom, limit)));
        return words.Select(t => t with { X = t.X + x, Y = t.Y + y }).ToList();
    }

    private static async Task<List<OcrWord>> ReadAsync(BitmapSource image, double scale)
    {
        var source = Math.Abs(scale - 1) < 0.01 ? image : new TransformedBitmap(image, new ScaleTransform(scale, scale));
        var bgra = ScreenCapture.ToBgra(source);

        var buffer = CryptographicBuffer.CreateFromByteArray(bgra.Pixels);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, bgra.Width, bgra.Height, BitmapAlphaMode.Premultiplied);
        var result = await Engine.RecognizeAsync(bitmap);

        var words = new List<OcrWord>();
        foreach (var line in result.Lines)
        foreach (var word in line.Words)
        {
            var r = word.BoundingRect;
            words.Add(new OcrWord(word.Text, r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale));
        }
        return words;
    }
}
