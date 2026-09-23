using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Media.Imaging;
using UniCrCapture.Core;

namespace UniCrCapture;

/// <summary>撮影 → OCR → 読み取り → JSONL への追記、をまとめて行う。</summary>
internal sealed class CaptureService(AppSettings settings)
{
    public sealed record Outcome(bool Ok, string Title, IReadOnlyList<string> Details);

    /// <summary>次の1試合だけに使うマップ（時刻から判定するが、メニューで上書きできる）。保存したら消す。</summary>
    public string? NextMap { get; set; }

    /// <summary>覚えたジョブのアイコン。自分の行から少しずつ増える。</summary>
    public JobIconStore JobIconStore => _jobIcons;

    private readonly JobIconStore _jobIcons = JobIconStore.Load();


    /// <param name="confirm">
    /// 保存する前に呼ばれる。false を返すと保存しない。
    /// マップは時刻から決めているので、目で確かめてもらうために使う（まとめて読み込むときは渡さない）。
    /// </param>
    public async Task<Outcome> ProcessAsync(BitmapSource image, DateTimeOffset at, string? existingFile = null,
        Func<MatchRecord, IReadOnlyList<string>, bool>? confirm = null)
    {
        // 画像は先に保存しておく（読み取りを直したあとに、読み直せるように）
        var imageName = existingFile is null ? $"cap_{at:yyyyMMdd_HHmmss}.png" : Path.GetFileName(existingFile);
        var imagePath = existingFile ?? Path.Combine(settings.ScreenshotsFolder, imageName);
        if (existingFile is null && settings.KeepImages) ScreenCapture.SavePng(image, imagePath);

        List<OcrWord> words;
        try
        {
            words = await WindowsOcr.RecognizeAsync(image);
            words = await RereadSmallNumbersAsync(image, words);
            words = RereadLatinAsync(image, words);
            words = RereadTeamTotals(image, words);
        }
        catch (Exception e)
        {
            return new Outcome(false, "文字を読み取れませんでした", new[] { e.Message });
        }

        var pixels = ScreenCapture.ToBgra(image);
        var result = ResultParser.Parse(words, pixels, new ParseOptions
        {
            SelfName = settings.SelfName,
            SelfJob = settings.SelfJob,
            JobIcons = _jobIcons.Icons,
            Map = NextMap,
            CapturedAt = at,
            SourceFile = imageName,
            AppName = $"conflict-record-capture/{AppSettings.Version}",
        });

        if (!result.Success || result.Match is null)
        {
            // うまく読めなかったときは、OCR の結果を「読み取りログ」に残す（原因を追うための手がかり）
            WriteOcrDump(imagePath, words, result);
            return new Outcome(false, "リザルト画面として読めませんでした", result.Errors.Concat(result.Warnings).ToList());
        }

        var match = result.Match;
        var self = match.Players.First(p => p.Self);

        // 自分の行はジョブが分かっているので、そのアイコンを覚える（次からは他の人の行でも見分けられる）
        var selfAt = match.Players.IndexOf(self);
        if (!string.IsNullOrWhiteSpace(settings.SelfJob) && selfAt < result.JobIconAreas.Count
            && result.JobIconAreas[selfAt] is { } selfIcon)
            _jobIcons.Remember(settings.SelfJob, JobIcons.Signature(pixels, selfIcon));

        if (confirm is not null && !confirm(match, result.Warnings))
            return new Outcome(true, "登録しませんでした", new[] { "「登録しない」が選ばれました。" });

        match.Owner = OwnerMark.For(match);
        var store = new JsonlStore(settings.MatchesFolder);
        var written = store.Append(match, at);
        if (written) NextMap = null;
        if (result.Warnings.Count > 0) WriteOcrDump(imagePath, words, result);

        var summary = $"{(match.Teams[self.Team].Result == "win" ? "勝ち" : "負け")}・{GameData.JobName(self.Job)}・" +
                      $"{self.K}/{self.D}/{self.A}・与ダメ {self.Dmg:N0}" +
                      (string.IsNullOrEmpty(match.Map) ? "" : $"・{match.Map}");
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

    /// <summary>
    /// 名前とワールド名のマスを、英語のモデルで読み直す。
    /// 日本語のモデルはラテン文字を日本語として読むため（r → 「、l → I）、
    /// あとから直そうとしても限界がある。英語のモデルなら、その種の読み違いが起きない。
    /// </summary>
    private static List<OcrWord> RereadLatinAsync(BitmapSource image, List<OcrWord> words)
    {
        if (!LatinOcr.Available) return words;
        var merged = words;
        foreach (var cell in ResultParser.LatinCells(words))
        {
            var reread = LatinOcr.ReadLine(image, cell);
            if (reread.Count > 0) merged = ResultParser.ReplaceArea(merged, cell, reread);
        }
        return merged;
    }

    /// <summary>「K:8 D:0 A:26」の形に読めたものだけを採る。</summary>
    private static readonly System.Text.RegularExpressions.Regex TotalsShape =
        new(@"K[:;]?\d{1,3}\s*D[:;]?\d{1,3}\s*A[:;]?\d{1,3}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>「100.0%」の形に読めたものだけを採る。</summary>
    private static readonly System.Text.RegularExpressions.Regex ProgressShape =
        new(@"^\d{1,3}(\.\d)?%$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// チーム合計（K:8 D:0 A:26）の欄を、英語のモデルで読み直す。
    /// 色つきの字が模様の上に乗っていて、日本語のモデルでは数字が「ロ」になってしまう。
    /// 倍率によって読めたり読めなかったりするので、いくつか試して、
    /// 「K:数字 D:数字 A:数字」の形になったものだけを採用する（形が崩れていれば使わない）。
    /// </summary>
    private static List<OcrWord> RereadTeamTotals(BitmapSource image, List<OcrWord> words)
    {
        if (!LatinOcr.Available) return words;
        var merged = words;
        foreach (var area in ResultParser.TeamTotalAreas(words))
            merged = TryReread(image, merged, area, LatinOcr.TotalLetters, TotalsShape);
        foreach (var area in ResultParser.ProgressAreas(words))
            merged = TryReread(image, merged, area, LatinOcr.PercentLetters, ProgressShape);
        return merged;
    }

    /// <summary>形が合うまで、倍率と前処理を変えて読み直す。どれも形にならなければ、元のままにする。</summary>
    private static List<OcrWord> TryReread(BitmapSource image, List<OcrWord> words, PixelRect area,
        string letters, System.Text.RegularExpressions.Regex shape)
    {
        foreach (var (zoom, byColour) in new[] { (3, false), (6, false), (4, false), (4, true), (6, true) })
        {
            var reread = LatinOcr.ReadLine(image, area, zoom, letters, byColour);
            var text = string.Concat(reread.OrderBy(w => w.X).Select(w => w.Text));
            if (shape.IsMatch(text)) return ResultParser.ReplaceArea(words, area, reread);
        }
        return words;
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
            var path = Path.Combine(settings.OcrLogFolder, Path.GetFileNameWithoutExtension(imagePath) + ".ocr.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { result.Errors, result.Warnings, words }, DumpJson));
        }
        catch
        {
            // 手がかりが書けなくても本体の処理は続ける
        }
    }
}
