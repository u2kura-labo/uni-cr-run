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
            AppName = $"conflict-record-capture/{typeof(CaptureService).Assembly.GetName().Version?.ToString(3)}",
        });

        if (!result.Success || result.Match is null)
        {
            // うまく読めなかったときは、OCR の結果を画像の隣に残す（読み取りを直すための手がかり）
            WriteOcrDump(imagePath, words, result);
            return new Outcome(false, "リザルト画面として読めませんでした", result.Errors.Concat(result.Warnings).ToList());
        }

        var match = result.Match;
        var store = new JsonlStore(settings.SaveFolder);
        var written = store.Append(match, at);
        if (written) NextMap = null;
        if (result.Warnings.Count > 0) WriteOcrDump(imagePath, words, result);

        var self = match.Players.First(p => p.Self);
        var summary = $"{(match.Teams[self.Team].Result == "win" ? "勝ち" : "負け")}・{GameData.JobName(self.Job)}・" +
                      $"{self.K}/{self.D}/{self.A}・与ダメ {self.Dmg:N0}";
        var details = new List<string> { summary };
        if (!written) details.Insert(0, "この試合はもう保存されていました。");
        details.AddRange(result.Warnings.Select(w => "要確認：" + w));
        var title = !written ? "保存済みの試合です" : result.Warnings.Count > 0 ? "保存しました（要確認あり）" : "保存しました";
        return new Outcome(true, title, details);
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
