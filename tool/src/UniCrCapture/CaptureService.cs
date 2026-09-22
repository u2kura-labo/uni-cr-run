using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Media.Imaging;
using UniCrCapture.Core;

namespace UniCrCapture;

/// <summary>撮影 → OCR → 読み取り → JSONL への追記、をまとめて行う。</summary>
internal sealed class CaptureService(AppSettings settings)
{
    public sealed record Outcome(bool Ok, string Title, IReadOnlyList<string> Details);

    /// <summary>次の1試合だけに使うマップ（リザルト画面に出ないのでメニューで選ぶ）。保存したら消す。</summary>
    public string? NextMap { get; set; }

    private string CapturesFolder => Path.Combine(settings.SaveFolder, "captures");

    public async Task<Outcome> ProcessAsync(BitmapSource image, DateTimeOffset at, string? existingFile = null)
    {
        // 画像は先に保存しておく（読み取りを直したあとに、読み直せるように）
        var imageName = existingFile is null ? $"cap_{at:yyyyMMdd_HHmmss}.png" : Path.GetFileName(existingFile);
        var imagePath = existingFile ?? Path.Combine(CapturesFolder, imageName);
        if (existingFile is null && settings.KeepImages) ScreenCapture.SavePng(image, imagePath);

        List<OcrWord> words;
        try
        {
            words = await WindowsOcr.RecognizeAsync(image);
            words = await RereadSmallNumbersAsync(image, words);
        }
        catch (Exception e)
        {
            return new Outcome(false, "文字を読み取れませんでした", new[] { e.Message });
        }

        var result = ResultParser.Parse(words, ScreenCapture.ToBgra(image), new ParseOptions
        {
            SelfName = settings.SelfName,
            SelfJob = settings.SelfJob,
            Map = NextMap,
            CapturedAt = at,
            SourceFile = imageName,
            AppName = $"conflict-record-capture/{AppSettings.Version}",
        });

        if (!result.Success || result.Match is null)
        {
            // うまく読めなかったときは、OCR の結果を画像の隣に残す（読み取りを直すための手がかり）
            WriteOcrDump(imagePath, words, result);
            return new Outcome(false, "リザルト画面として読めませんでした", result.Errors.Concat(result.Warnings).ToList());
        }

        var match = result.Match;
        var self = match.Players.First(p => p.Self);
        match.Owner = OwnerMark.For(match);
        var store = new JsonlStore(settings.SaveFolder);
        var written = store.Append(match, at);
        if (written) NextMap = null;
        if (result.Warnings.Count > 0) WriteOcrDump(imagePath, words, result);

        var summary = $"{(match.Teams[self.Team].Result == "win" ? "勝ち" : "負け")}・{GameData.JobName(self.Job)}・" +
                      $"{self.K}/{self.D}/{self.A}・与ダメ {self.Dmg:N0}";
        var details = new List<string> { summary };
        if (!written) details.Insert(0, "この試合はもう保存されていました。");
        details.AddRange(result.Warnings.Select(w => "要確認：" + w));
        var title = !written ? "保存済みの試合です" : result.Warnings.Count > 0 ? "保存しました（要確認あり）" : "保存しました";
        return new Outcome(true, title, details);
    }

    /// <summary>
    /// K / D / A の列だけを切り出して 4 倍に拡大し、読み直したもので置き換える。
    /// 1桁の数字が離れて並ぶだけの列は、画面ぜんぶを1枚で渡すと Windows の文字認識が
    /// 丸ごと落とすことがあるが、その部分だけを大きくすれば読める。
    /// </summary>
    private static async Task<List<OcrWord>> RereadSmallNumbersAsync(BitmapSource image, List<OcrWord> words)
    {
        var area = ResultParser.SmallNumberArea(words);
        if (area is null) return words;
        // 文字の高さが 48 画素くらいになるまで拡大する。倍率を決め打ちにすると、
        // スクリーンショットの大きさ（解像度）が変わったときに効き目が変わってしまう。
        var heights = words.Where(area.Value.Holds).Select(w => w.Height).OrderBy(h => h).ToList();
        var textHeight = heights.Count > 0 ? heights[heights.Count / 2] : 0;
        var zoom = textHeight > 0 ? (int)Math.Clamp(Math.Round(48 / textHeight), 2, 8) : 4;
        var reread = await WindowsOcr.RecognizeAreaAsync(image, area.Value, zoom, (int)Math.Max(8, textHeight));

        // 読み直したほうが語数が少ないなら、前のままにしておく（読み直しで悪くしない）
        var before = words.Count(area.Value.Holds);
        var after = reread.Count(area.Value.Holds);
        return after < before ? words : ResultParser.ReplaceArea(words, area.Value, reread);
    }

    private static readonly JsonSerializerOptions DumpJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private void WriteOcrDump(string imagePath, List<OcrWord> words, ParseResult result)
    {
        try
        {
            var path = Path.ChangeExtension(imagePath, ".ocr.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { result.Errors, result.Warnings, words }, DumpJson));
        }
        catch
        {
            // 手がかりが書けなくても本体の処理は続ける
        }
    }
}
