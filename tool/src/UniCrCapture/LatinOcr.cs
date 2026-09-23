using System.Windows;
using System.Windows.Media.Imaging;
using Tesseract;
using UniCrCapture.Core;
using OcrWord = UniCrCapture.Core.OcrWord;

namespace UniCrCapture;

/// <summary>
/// 名前とワールド名を読むための、英語だけの文字認識（Tesseract、モデルは exe に同梱）。
///
/// Windows の文字認識は日本語のモデルしか入っていないことが多く、ラテン文字を日本語として読む。
/// そのため r が「 に、l が I になり、短い名前は丸ごと落ちる。英語のモデルなら、
/// そもそも日本語の文字を出さないので、この種の読み違いが起きない。
/// 実際のスクリーンショットでは、名前が 8/10 → 10/10 になった。
/// </summary>
internal static class LatinOcr
{
    private static TesseractEngine? _engine;
    private static bool _failed;

    /// <summary>名前に出る文字だけに絞る（数字や記号に化けるのを防ぐ）。</summary>
    public const string NameLetters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz'- ";

    /// <summary>チーム合計（K:8 D:0 A:26）に出る文字だけ。</summary>
    public const string TotalLetters = "KDA:0123456789 ";

    /// <summary>進行度（100.0%）に出る文字だけ。</summary>
    public const string PercentLetters = "0123456789.% ";

    public static bool Available => Engine is not null;

    private static TesseractEngine? Engine
    {
        get
        {
            if (_engine is not null || _failed) return _engine;
            try
            {
                var folder = Path.Combine(AppContext.BaseDirectory, "tessdata");
                _engine = new TesseractEngine(folder, "eng", EngineMode.Default);
                _engine.SetVariable("tessedit_char_whitelist", NameLetters);
            }
            catch
            {
                // 読めなくても、Windows の文字認識の結果でそのまま進む
                _failed = true;
            }
            return _engine;
        }
    }

    /// <summary>
    /// 1行ぶんの範囲を読む。1行として読ませるので、表のマスを1つずつ渡すこと。
    /// 返す語の位置は、元の画像の座標に直してある。
    /// </summary>
    public static List<OcrWord> ReadLine(BitmapSource image, PixelRect area, int zoom = 4,
        string letters = NameLetters, bool byColour = false)
    {
        var words = new List<OcrWord>();
        var engine = Engine;
        if (engine is null) return words;
        engine.SetVariable("tessedit_char_whitelist", letters);

        var x = Math.Clamp((int)Math.Floor(area.X), 0, Math.Max(0, image.PixelWidth - 1));
        var y = Math.Clamp((int)Math.Floor(area.Y), 0, Math.Max(0, image.PixelHeight - 1));
        var w = Math.Min(image.PixelWidth - x, (int)Math.Ceiling(area.Width));
        var h = Math.Min(image.PixelHeight - y, (int)Math.Ceiling(area.Height));
        if (w <= 2 || h <= 2) return words;

        try
        {
            var crop = new CroppedBitmap(image, new Int32Rect(x, y, w, h));
            crop.Freeze();
            var bgra = ScreenCapture.ToBgra(crop);
            if (byColour) bgra = ScreenCapture.ColourToInk(bgra);
            var big = ScreenCapture.Enlarge(bgra, zoom);

            using var pix = Pix.LoadFromMemory(ScreenCapture.ToPng(big));
            using var page = engine.Process(pix, PageSegMode.SingleLine);
            using var it = page.GetIterator();
            it.Begin();
            do
            {
                var text = it.GetText(PageIteratorLevel.Word)?.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                if (!it.TryGetBoundingBox(PageIteratorLevel.Word, out var box)) continue;
                words.Add(new OcrWord(text,
                    x + (double)box.X1 / zoom, y + (double)box.Y1 / zoom,
                    (double)box.Width / zoom, (double)box.Height / zoom));
            } while (it.Next(PageIteratorLevel.Word));
        }
        catch
        {
            // この行だけ読めなくても、ほかは続ける
        }
        return words;
    }
}
