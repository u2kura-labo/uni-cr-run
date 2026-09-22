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
    public static async Task<List<OcrWord>> RecognizeAsync(BitmapSource image)
    {
        // 小さい文字は拡大したほうが読みやすいので、上限の範囲で 2 倍まで広げる
        var longest = Math.Max(image.PixelWidth, image.PixelHeight);
        var scale = Math.Min(2.0, (double)OcrEngine.MaxImageDimension / longest);
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
