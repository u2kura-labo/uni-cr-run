using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace UniCrCapture.Core;

/// <summary>
/// クリスタルコンフリクトのリザルト画面の OCR 結果から、1試合分のデータを組み立てる。
///
/// 手順：
/// 1. 表の見出し（キャラクター名・ホームワールド・階級・総与ダメージ量 など）を探して、列の位置を決める
/// 2. 見出しより下の語を行ごとにまとめ、列の位置で各マスに振り分ける
/// 3. 見出しより上から、チームの勝敗・進行度・K/D/A 合計、ランク、経過時間を読む
/// 4. 名前の文字の色でチーム（どちらの色がどちらの隊かは、チーム欄の下地の色から合わせる）、設定の名前か行のハイライトで自分の行を決める
/// 5. チーム合計は、各プレイヤーの K/D/A を足して出す（画面の合計欄は読み違えやすいので使わない）
/// </summary>
public static partial class ResultParser
{
    public static readonly string[] Tiers = { "ブロンズ", "シルバー", "ゴールド", "プラチナ", "ダイヤモンド", "クリスタル" };

    private enum Col { Name, World, Tier, K, D, A, Dmg, Taken, Heal, Crystal }

    private static readonly (Col Col, string Header)[] Headers =
    {
        (Col.Name, "キャラクター名"),
        (Col.World, "ホームワールド"),
        (Col.Tier, "階級"),
        (Col.Dmg, "総与ダメージ量"),
        (Col.Taken, "総被ダメージ量"),
        (Col.Heal, "総与ヒール量"),
        (Col.Crystal, "移送時間"),
    };

    public static ParseResult Parse(IReadOnlyList<OcrWord> words, IPixelSource? pixels, ParseOptions options)
    {
        var result = new ParseResult();
        var lines = TextLayout.GroupLines(words);

        // ---------- 1. 見出しと列 ----------
        var header = FindHeader(lines);
        if (header is null)
        {
            result.Errors.Add("リザルト画面の表（キャラクター名・総与ダメージ量 などの見出し）が見つかりませんでした。");
            return result;
        }
        var (headerLine, centers) = header.Value;
        var headerBottom = headerLine.Bottom;
        var rowHeight = headerLine.Words.Average(w => w.Height);
        var bounds = ColumnBounds(centers);

        // ---------- 2. 表の行 ----------
        var rows = ReadRows(lines, headerBottom, rowHeight, bounds);
        if (rows.Count < 2)
        {
            result.Errors.Add($"表の行を読み取れませんでした（{rows.Count} 行）。");
            return result;
        }
        if (rows.Count != 10) result.Warnings.Add($"プレイヤーの行が {rows.Count} 行でした（通常は 10 行）。");

        // ---------- 3. 見出しより上 ----------
        var above = lines.Where(l => l.Bottom < headerLine.Top).ToList();
        var tableCenter = (centers[Col.Name] + centers[Col.Crystal]) / 2;
        var teams = ReadTeams(above, tableCenter, out var totalsRead);
        var outcome = ReadOutcome(above, tableCenter);
        var rank = ReadRank(above);
        var duration = ReadDuration(above);
        // どちらの隊が赤寄りの色かは、画面の下地から合わせる（決め打ちにしない）
        var warm = AstraIsWarm(pixels, above);
        if (warm is null)
            result.Warnings.Add("隊の色を測れなかったので、アストラ＝赤寄りとして扱いました。チームが入れ替わっていないか確認してください。");
        var astraIsWarm = warm ?? true;

        // ---------- 4. プレイヤー ----------
        // ジョブのアイコンは、名前のすぐ左に並ぶ四角
        var nameLeft = rows.SelectMany(r => r.Cells[Col.Name]).Select(w => w.X).DefaultIfEmpty(double.NaN).Min();
        var players = new List<PlayerRecord>();
        var rowBands = new List<(double Top, double Bottom)>();
        var icons = new List<PixelRect?>();
        foreach (var row in rows)
        {
            var p = ToPlayer(row, result);
            if (p is null) continue;
            p.Team = DetectTeam(row.NameWords, pixels, astraIsWarm) ?? "";
            var icon = double.IsNaN(nameLeft) ? null : IconArea(pixels, nameLeft, rowHeight, row.Top, row.Bottom);
            if (icon is not null)
            {
                p.Role = DetectRole(pixels, icon.Value);
                var signature = JobIcons.Signature(pixels, icon.Value);
                if (signature is not null) p.Job = JobIcons.Match(signature, options.JobIcons);
            }
            // 5 分の試合でキル・デス・アシストが 20 を超えることはない。
            // 読み直しても直らなかった分は、そのままにして知らせる（勝手に書き換えない）。
            foreach (var (label, n) in new[] { ("K", p.K), ("D", p.D), ("A", p.A) })
                if (n >= 20) result.Warnings.Add($"{p.Name} の {label} が {n} でした。読み違えている可能性が高いです。");
            icons.Add(icon);
            players.Add(p);
            rowBands.Add((row.Top, row.Bottom));
        }
        result.JobIconAreas = icons;
        if (players.Any(p => p.Team == ""))
        {
            result.Errors.Add("名前の色からチームを判定できない行がありました。");
            return result;
        }

        var selfIndex = FindSelfByName(players, options.SelfName);
        if (selfIndex < 0)
        {
            selfIndex = FindSelfByHighlight(pixels, rowBands, bounds);
            if (selfIndex >= 0 && !string.IsNullOrWhiteSpace(options.SelfName))
                result.Warnings.Add($"設定のキャラクター名（{options.SelfName}）が表になかったので、行のハイライトで自分（{players[selfIndex].Name}）を判定しました。");
        }
        if (selfIndex < 0)
        {
            result.Errors.Add(string.IsNullOrWhiteSpace(options.SelfName)
                ? "自分の行が見つかりませんでした。設定で自分のキャラクター名を入れてください。"
                : $"自分の行（{options.SelfName}）が見つかりませんでした。設定のキャラクター名を確認してください。");
            return result;
        }
        players[selfIndex].Self = true;
        if (!string.IsNullOrWhiteSpace(options.SelfJob)) players[selfIndex].Job = options.SelfJob;

        // K/D/A の合計は、必ず表の数字を足して出す。
        //
        // 画面の合計欄（K:11 D:9 A:29）にも同じ数字が出ているが、模様の上に色つきの小さな字が
        // 乗っていて、表の中の数字より読み違えやすい。手元の 19 枚では、A:29 が 293、A:21 が 211、
        // K:14 D:11 A:41 が K:140:4A:46 と読めた。読めたほうを採ると正しい表の数字を壊すので、
        // 記録にも、照らし合わせにも使わない（食い違いのほとんどが合計欄側の読み違いで、ただの誤報になった）。
        foreach (var t in new[] { "astra", "umbra" })
        {
            if (!teams.TryGetValue(t, out var team)) teams[t] = team = new TeamRecord();
            var members = players.Where(p => p.Team == t).ToList();
            team.K = members.Sum(p => p.K);
            team.D = members.Sum(p => p.D);
            team.A = members.Sum(p => p.A);
        }
        // WIN / LOSE の語の位置から決めたほうが確かなので、そちらを優先する
        if (outcome is not null)
        {
            teams["astra"].Result = outcome;
            teams["umbra"].Result = outcome == "win" ? "lose" : "win";
        }
        FillResults(teams, result);
        if (result.Errors.Count > 0) return result;

        // マップはリザルト画面に出ないが、1 時間ごとに決まった順で変わるので、
        // 試合が始まった時刻（撮った時刻 − 経過時間）から決める。メニューで選んであればそちらを使う。
        var map = options.Map;
        if (string.IsNullOrWhiteSpace(map))
        {
            var started = options.CapturedAt - Elapsed(duration);
            map = GameData.MapAt(started);
            // 撮るまでに間があるぶん、実際の開始はこれより前。切り替わり直後だと1つ前のマップかもしれない
            if (GameData.MinutesIntoMap(started) < 3)
                result.Warnings.Add($"マップは時刻から決めました（{map}）。切り替わった直後なので、1つ前のマップかもしれません。");
        }

        var match = new MatchRecord
        {
            Ts = options.CapturedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            Map = map,
            Duration = duration,
            Rank = rank,
            Teams = teams,
            Players = players,
            Src = options.SourceFile,
            App = options.AppName,
            Warnings = result.Warnings.Count > 0 ? new List<string>(result.Warnings) : null,
        };
        match.Id = ContentId(match);
        result.Match = match;
        return result;
    }

    // ---------- 拡大して読み直す範囲 ----------

    /// <summary>
    /// K / D / A の列の範囲を返す（なければ null）。
    ///
    /// この3列は1桁の数字が離れて並ぶだけなので、画面ぜんぶを1枚の画像として渡すと
    /// Windows の文字認識が丸ごと取りこぼすことがある（10行すべての K と D が落ちる例がある）。
    /// 呼ぶ側は、この範囲だけを切り出して拡大し、読み直した語で ReplaceArea する。
    /// </summary>
    public static PixelRect? SmallNumberArea(IReadOnlyList<OcrWord> words)
    {
        var lines = TextLayout.GroupLines(words);
        var header = FindHeader(lines);
        if (header is null) return null;
        var (headerLine, centers) = header.Value;
        var rowHeight = headerLine.Words.Average(w => w.Height);
        var bounds = ColumnBounds(centers);
        var rows = ReadRows(lines, headerLine.Bottom, rowHeight, bounds);
        if (rows.Count == 0) return null;

        // K・D・A の間隔から、3列ぶんの幅を見当づける
        var step = (centers[Col.A] - centers[Col.K]) / 2;
        if (step <= 0) return null;
        var left = centers[Col.K] - step * 0.8;
        var right = centers[Col.A] + step * 0.8;

        // 隣の列（階級・総与ダメージ量）の文字まで巻き込まないように、読めている語の位置でせばめる
        var tierRight = rows.SelectMany(r => r.Cells[Col.Tier]).Select(w => w.Right).DefaultIfEmpty(double.MinValue).Max();
        var dmgLeft = rows.SelectMany(r => r.Cells[Col.Dmg]).Select(w => w.X).DefaultIfEmpty(double.MaxValue).Min();
        left = Math.Max(left, tierRight + 1);
        right = Math.Min(right, dmgLeft - 1);
        if (right - left < step) return null; // せばまりすぎたら、読み直さない

        var top = headerLine.Top;
        var bottom = rows[^1].Bottom;
        // 行として数えられなかった行があっても帯が届くように、10 行ぶんまで下に伸ばす
        if (rows.Count is > 1 and < 10)
        {
            var gaps = rows.Zip(rows.Skip(1), (a, b) => b.Top - a.Top).OrderBy(g => g).ToList();
            var spacing = gaps[gaps.Count / 2];
            if (spacing > 0) bottom = Math.Max(bottom, rows[0].Top + spacing * 9 + rowHeight * 2);
        }
        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// K / D / A のマスを、行ごと・列ごとに返す（表が見つからなければ空）。
    ///
    /// この3列は 1〜2 桁の小さな数字で、日本語のモデルでは丸ごと落ちることがある。
    /// 落ちたマスは 0 として記録され、平均も合計も静かに狂うので、
    /// 帯ごと拡大して読み直してもなお残るマスを、1つずつ英語のモデルで読み直すため。
    /// 手元の 19 枚では、570 マスのうち 52 マスが落ちていた。
    /// </summary>
    public static IReadOnlyList<PixelRect> NumberCells(IReadOnlyList<OcrWord> words)
    {
        var cells = new List<PixelRect>();
        var lines = TextLayout.GroupLines(words);
        var header = FindHeader(lines);
        if (header is null) return cells;
        var (headerLine, centers) = header.Value;
        var rowHeight = headerLine.Words.Average(w => w.Height);
        var bounds = ColumnBounds(centers);
        var rows = ReadRows(lines, headerLine.Bottom, rowHeight, bounds);
        if (rows.Count == 0) return cells;

        var spacing = rows.Count > 1
            ? rows.Zip(rows.Skip(1), (a, b) => b.Top - a.Top).OrderBy(g => g).ElementAt(rows.Count / 2)
            : rowHeight * 2;
        var height = Math.Max(rowHeight * 1.6, spacing * 0.9);

        foreach (var col in new[] { Col.K, Col.D, Col.A })
        {
            var (left, right) = bounds[col];
            // 見出しの K / D / A は 1 文字で読めないことが多く、列の位置は当て推量になる。
            // 読めている数字があれば、その中心にマスを合わせる（隣の列の文字が入りにくくなる）。
            var seen = rows.SelectMany(r => r.Cells[col]).OrderBy(w => w.CenterX).ToList();
            if (seen.Count >= 2)
            {
                var center = seen[seen.Count / 2].CenterX;
                var half = Math.Min(center - left, right - center);
                if (half > rowHeight * 0.4) (left, right) = (center - half, center + half);
            }
            if (right - left < rowHeight * 0.5) continue;
            foreach (var row in rows)
                cells.Add(new PixelRect(left, (row.Top + row.Bottom) / 2 - height / 2, right - left, height));
        }
        return cells;
    }

    /// <summary>
    /// 名前とワールド名のマスを、行ごとに返す（表が見つからなければ空）。
    /// この2列はラテン文字なので、日本語のモデルではなく英語のモデルで読み直すため。
    /// 1マス = 1行として読ませるので、行ごとに分けて返す。
    /// </summary>
    public static IReadOnlyList<PixelRect> LatinCells(IReadOnlyList<OcrWord> words)
    {
        var cells = new List<PixelRect>();
        var lines = TextLayout.GroupLines(words);
        var header = FindHeader(lines);
        if (header is null) return cells;
        var (headerLine, centers) = header.Value;
        var rowHeight = headerLine.Words.Average(w => w.Height);
        var bounds = ColumnBounds(centers);
        var rows = ReadRows(lines, headerLine.Bottom, rowHeight, bounds);
        if (rows.Count == 0) return cells;

        // 行の高さは、行と行の間隔から見当づける（文字が読めていない行でも同じ高さで切り出せるように）
        var spacing = rows.Count > 1
            ? rows.Zip(rows.Skip(1), (a, b) => b.Top - a.Top).OrderBy(g => g).ElementAt(rows.Count / 2)
            : rowHeight * 2;
        var height = Math.Max(rowHeight * 1.6, spacing * 0.9);

        foreach (var col in new[] { Col.Name, Col.World })
        {
            var (left, right) = bounds[col];
            // 列の端は、実際に読めている文字の位置で詰める。
            // 名前の左はジョブのアイコン、ワールドの右は階級（日本語）で、
            // どちらも入れると英語のモデルが無理に文字として読んでしまう。
            var texts = rows.SelectMany(r => r.Cells[col]).ToList();
            if (texts.Count > 0) left = Math.Max(left, texts.Min(w => w.X) - rowHeight * 0.3);
            if (col == Col.World)
            {
                var tierLeft = rows.SelectMany(r => r.Cells[Col.Tier]).Select(w => w.X).DefaultIfEmpty(double.MaxValue).Min();
                right = Math.Min(right, tierLeft - 1);
            }
            if (right - left < rowHeight) continue;
            foreach (var row in rows)
            {
                var top = (row.Top + row.Bottom) / 2 - height / 2;
                cells.Add(new PixelRect(left, top, right - left, height));
            }
        }
        return cells;
    }

    /// <summary>
    /// チーム合計（K:8 D:0 A:26）の欄を、左右それぞれ返す。
    /// この欄は色つきの字が模様の上に乗っていて、日本語のモデルでは数字が「ロ」になってしまう。
    /// 「進行度」のすぐ下に並ぶので、その位置から範囲を決めて、英語のモデルで読み直す。
    /// </summary>
    public static IReadOnlyList<PixelRect> TeamTotalAreas(IReadOnlyList<OcrWord> words) =>
        AreasNearProgress(words, (hit, h) =>
            new PixelRect(hit.X - h * 2.5, hit.Bottom, hit.Right + h * 8 - (hit.X - h * 2.5), h * 2));

    /// <summary>
    /// 進行度の数字（0.0% / 100.0%）の欄を、左右それぞれ返す。
    /// ここも色つきの字なので、日本語のモデルだと 100.0% が「1ロロ.ロ%」になり、1% と読めてしまう。
    /// 「進行度」の字そのものは残したいので、その右どなりだけを指す。
    /// </summary>
    public static IReadOnlyList<PixelRect> ProgressAreas(IReadOnlyList<OcrWord> words) =>
        AreasNearProgress(words, (hit, h) =>
            new PixelRect(hit.Right + h * 0.1, hit.Y - h * 0.4, h * 8, h * 1.8));

    private static IReadOnlyList<PixelRect> AreasNearProgress(IReadOnlyList<OcrWord> words,
        Func<TextHit, double, PixelRect> area)
    {
        var areas = new List<PixelRect>();
        var lines = TextLayout.GroupLines(words);
        var header = FindHeader(lines);
        if (header is null) return areas;

        // 左右のチーム欄は同じ高さに並ぶので、1行に「進行度」が2つ出てくる
        foreach (var line in lines.Where(l => l.Bottom < header.Value.Line.Top))
        foreach (var hit in TextLayout.FindAll(line, "進行度"))
        {
            if (hit.Height <= 0) continue;
            areas.Add(area(hit, hit.Height));
        }
        return areas;
    }

    /// <summary>area の中の語を、読み直した語で置き換える。</summary>
    public static List<OcrWord> ReplaceArea(IReadOnlyList<OcrWord> words, PixelRect area, IEnumerable<OcrWord> reread)
    {
        var merged = words.Where(w => !area.Holds(w)).ToList();
        merged.AddRange(reread.Where(area.Holds));
        return merged;
    }

    // ---------- 見出し ----------

    private static (VisualLine Line, Dictionary<Col, double> Centers)? FindHeader(List<VisualLine> lines)
    {
        foreach (var line in lines)
        {
            var centers = new Dictionary<Col, double>();
            foreach (var (col, text) in Headers)
            {
                var hit = TextLayout.Find(line, text);
                if (hit is not null) centers[col] = hit.Value.CenterX;
            }
            // 数字の列の見出しが2つ以上と、キャラクター名があれば表とみなす
            var numeric = new[] { Col.Dmg, Col.Taken, Col.Heal }.Count(centers.ContainsKey);
            if (numeric < 2 || !centers.ContainsKey(Col.Name)) continue;

            // 見つからなかった見出しは、見つかったものから補う
            if (!centers.ContainsKey(Col.Dmg)) centers[Col.Dmg] = 2 * centers[Col.Taken] - centers[Col.Heal];
            if (!centers.ContainsKey(Col.Taken)) centers[Col.Taken] = (centers[Col.Dmg] + centers[Col.Heal]) / 2;
            if (!centers.ContainsKey(Col.Heal)) centers[Col.Heal] = 2 * centers[Col.Taken] - centers[Col.Dmg];
            var step = centers[Col.Heal] - centers[Col.Taken];
            if (!centers.ContainsKey(Col.Crystal)) centers[Col.Crystal] = centers[Col.Heal] + step * 0.85;
            if (!centers.ContainsKey(Col.World)) centers[Col.World] = centers[Col.Name] + step * 1.15;
            if (!centers.ContainsKey(Col.Tier)) centers[Col.Tier] = centers[Col.World] + step * 0.97;

            // K / D / A は1文字の見出しなので、階級と総与ダメージ量の間で探し、なければ等分する
            var left = centers[Col.Tier];
            var right = centers[Col.Dmg];
            var kda = new[] { (Col.K, 'K'), (Col.D, 'D'), (Col.A, 'A') };
            var found = kda.Select(x => line.Glyphs.Where(g => g.C == x.Item2 && g.CenterX > left && g.CenterX < right)
                .Select(g => (double?)g.CenterX).FirstOrDefault()).ToArray();
            if (found.All(v => v.HasValue) && found[0] < found[1] && found[1] < found[2])
            {
                centers[Col.K] = found[0]!.Value;
                centers[Col.D] = found[1]!.Value;
                centers[Col.A] = found[2]!.Value;
            }
            else
            {
                // 階級〜総与ダメージ量の間に、階級の右半分・K・D・A・総与ダメージ量の左半分が並ぶ
                var span = right - left;
                centers[Col.K] = left + span * 0.30;
                centers[Col.D] = left + span * 0.47;
                centers[Col.A] = left + span * 0.63;
            }
            return (line, centers);
        }
        return null;
    }

    /// <summary>
    /// 列ごとの左右の範囲。となりの列との中間で区切る。
    /// 両端は、いちばん外の列と同じ幅ぶんだけ外に広げたところで止める
    /// （止めないと、画面の左にあるチャットや、右にあるパーティ一覧の文字まで表の中に入ってしまう）。
    /// </summary>
    private static Dictionary<Col, (double Left, double Right)> ColumnBounds(Dictionary<Col, double> centers)
    {
        var order = Enum.GetValues<Col>().OrderBy(c => centers[c]).ToList();
        var bounds = new Dictionary<Col, (double, double)>();
        for (var i = 0; i < order.Count; i++)
        {
            var left = i == 0
                ? centers[order[0]] - (centers[order[1]] - centers[order[0]])
                : (centers[order[i - 1]] + centers[order[i]]) / 2;
            var right = i == order.Count - 1
                ? centers[order[^1]] + (centers[order[^1]] - centers[order[^2]])
                : (centers[order[i]] + centers[order[i + 1]]) / 2;
            bounds[order[i]] = (left, right);
        }
        return bounds;
    }

    // ---------- 表の行 ----------

    private sealed class Row
    {
        public Dictionary<Col, List<OcrWord>> Cells { get; } = Enum.GetValues<Col>().ToDictionary(c => c, _ => new List<OcrWord>());
        public List<OcrWord> NameWords => Cells[Col.Name];
        public double Top { get; set; } = double.MaxValue;
        public double Bottom { get; set; } = double.MinValue;
        public string Joined(Col c) => string.Concat(Cells[c].OrderBy(w => w.X).Select(w => w.Text));
    }

    private static List<Row> ReadRows(List<VisualLine> lines, double headerBottom, double rowHeight,
        Dictionary<Col, (double Left, double Right)> bounds)
    {
        var rows = new List<Row>();
        foreach (var line in lines.Where(l => l.CenterY > headerBottom))
        {
            var row = new Row();
            foreach (var w in line.Words)
            {
                // 表の外（同じ高さにあるチャットなど）は入れない
                var col = bounds.Where(b => w.CenterX >= b.Value.Left && w.CenterX < b.Value.Right)
                    .Select(b => (Col?)b.Key).FirstOrDefault();
                if (col is null) continue;
                row.Cells[col.Value].Add(w);
                row.Top = Math.Min(row.Top, w.Y);
                row.Bottom = Math.Max(row.Bottom, w.Bottom);
            }
            // 数字の列が4つ以上埋まっている行を、プレイヤーの行とみなす。
            // K / D / A が丸ごと落ちても、残る4列（総与・総被・総与ヒール・移送時間）で行と分かるようにする。
            // 5つ必要にしていたため、K/D/A が落ちた行が行として数えられず、
            // 読み直しの帯もそこまで届かない、という悪循環になっていた。
            var numericCols = new[] { Col.K, Col.D, Col.A, Col.Dmg, Col.Taken, Col.Heal, Col.Crystal };
            var filled = numericCols.Count(c => TextLayout.DigitsOnly(row.Joined(c)).Length > 0);
            if (filled >= 4) rows.Add(row);
            else if (rows.Count > 0 && line.CenterY - rows[^1].Bottom > rowHeight * 3) break; // 表の下の文字に着いた
            if (rows.Count == 10) break;
        }
        return rows;
    }

    private static PlayerRecord? ToPlayer(Row row, ParseResult result)
    {
        var name = CleanName(row.NameWords);
        if (name.Length == 0)
        {
            result.Warnings.Add("名前を読めない行がありました（その行は飛ばしました）。");
            return null;
        }
        int Int(Col c)
        {
            var digits = TextLayout.DigitsOnly(row.Joined(c));
            if (digits.Length == 0 || digits.Length > 3)
            {
                result.Warnings.Add($"{name} の {c} を読めませんでした（「{row.Joined(c)}」）。");
                return 0;
            }
            return int.Parse(digits, CultureInfo.InvariantCulture);
        }
        long Big(Col c)
        {
            var digits = TextLayout.DigitsOnly(row.Joined(c));
            if (digits.Length == 0 || digits.Length > 9)
            {
                result.Warnings.Add($"{name} の {c} を読めませんでした（「{row.Joined(c)}」）。");
                return 0;
            }
            return long.Parse(digits, CultureInfo.InvariantCulture);
        }
        return new PlayerRecord
        {
            Name = name,
            World = CleanWorld(row.Cells[Col.World]),
            Tier = MatchTier(row.Joined(Col.Tier)) ?? "",
            K = Int(Col.K),
            D = Int(Col.D),
            A = Int(Col.A),
            Dmg = Big(Col.Dmg),
            Taken = Big(Col.Taken),
            Heal = Big(Col.Heal),
            Crystal = ParseClock(row.Joined(Col.Crystal)),
        };
    }

    /// <summary>
    /// 日本語で読ませているせいで、名前の中の r が「 になることがある（Kura → "Ku" "「" "a"）。
    /// 「 のところで語も切れてしまうので、戻す。
    /// </summary>
    private static string Latin(string text) => TextLayout.Normalize(text).Replace('「', 'r').Replace('｢', 'r');

    /// <summary>
    /// 名前を組み立てる。OCR は1つの名前を途中で切って返すことがあるので、
    /// 語の間隔が「1文字ぶんよりずっと狭い」ところはつなぎ直し、広いところだけを空白にする。
    /// 記号だけの語（ジョブのアイコンが読まれたもの）は捨てる。
    /// </summary>
    private static string CleanName(IReadOnlyList<OcrWord> words)
    {
        var parts = Join(words)
            .Select(p => NameTrim().Replace(p, ""))
            // 小文字にはさまれた I は、l（小文字のエル）の読み違い（Salim → SaIim）
            .Select(p => MisreadL().Replace(p, "l"))
            // 小文字のすぐあとの大文字は、空白を読み落としたしるし
            // （FF14 の名前で大文字になるのは、先頭と ' - の次だけ）
            .SelectMany(p => LostSpace().Split(p))
            .Where(p => p.Length >= 2 && char.IsLetter(p[0]))
            .Select(FixCase)
            .ToList();
        // FF14 の名前は「名 姓」の 2 語と決まっている。3 語以上になるのは、名前のすぐ左にある
        // ジョブのアイコンの端が文字として読まれて、頭に付いたとき
        // （Sugar Last が Ney Sugar Last、Sd Sugar Last になる）。後ろの 2 語だけを採る。
        if (parts.Count > 2) parts = parts.TakeLast(2).ToList();
        return string.Join(" ", parts);
    }

    /// <summary>ワールド名は1語なので、間隔に関係なくつなぐ。</summary>
    private static string CleanWorld(IReadOnlyList<OcrWord> words)
    {
        var s = WorldTrim().Replace(string.Concat(words.OrderBy(w => w.X).Select(w => Latin(w.Text))), "");
        return s.Length == 0 ? "" : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
    }

    /// <summary>
    /// FF14 の名前で大文字になるのは、先頭と ' - の次だけ。
    /// それ以外の大文字は読み違いなので小文字に直す（KO → Ko、Pinot NOir → Pinot Noir）。
    /// </summary>
    private static string FixCase(string part)
    {
        var chars = part.ToCharArray();
        for (var i = 1; i < chars.Length; i++)
            if (chars[i - 1] is not ('\'' or '-'))
                chars[i] = char.ToLowerInvariant(chars[i]);
        chars[0] = char.ToUpperInvariant(chars[0]);
        return new string(chars);
    }

    /// <summary>
    /// 語の間隔で、ひとつながりの語にまとめる。区切りは1文字ぶんの幅から決めるので、
    /// スクリーンショットの大きさが変わっても同じように働く。
    /// </summary>
    private static List<string> Join(IReadOnlyList<OcrWord> words)
    {
        var ordered = words.Where(w => Latin(w.Text).Length > 0).OrderBy(w => w.X).ToList();
        if (ordered.Count == 0) return new List<string>();
        var charWidth = ordered.Sum(w => w.Width) / ordered.Sum(w => Latin(w.Text).Length);

        var parts = new List<string>();
        var current = new StringBuilder();
        var prevRight = 0.0;
        foreach (var w in ordered)
        {
            if (current.Length > 0 && w.X - prevRight > charWidth * 0.35)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            current.Append(Latin(w.Text));
            prevRight = w.Right;
        }
        parts.Add(current.ToString());
        return parts;
    }

    public static string? MatchTier(string raw)
    {
        var s = TextLayout.Normalize(raw);
        if (s.Length == 0) return null;
        string? best = null;
        var bestScore = int.MaxValue;
        foreach (var t in Tiers)
        {
            var d = TextLayout.Distance(s, t);
            if (d < bestScore) (best, bestScore) = (t, d);
        }
        return bestScore <= 2 ? best : null;
    }

    /// <summary>"1:43" のような経過時間を、長さに直す。</summary>
    private static TimeSpan Elapsed(string? duration)
    {
        if (string.IsNullOrEmpty(duration)) return TimeSpan.Zero;
        var m = ClockPattern().Match(duration);
        return m.Success
            ? new TimeSpan(0, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            : TimeSpan.Zero;
    }

    public static string? ParseClock(string raw)
    {
        var s = TextLayout.Normalize(raw).Replace('.', ':').Replace(';', ':').Replace(',', ':');
        var m = ClockPattern().Match(s);
        if (m.Success) return $"{int.Parse(m.Groups[1].Value)}:{m.Groups[2].Value}";
        var digits = TextLayout.DigitsOnly(s);
        if (digits.Length is 3 or 4) return $"{int.Parse(digits[..^2])}:{digits[^2..]}";
        return null;
    }

    // ---------- 見出しより上：チーム・ランク・経過時間 ----------

    /// <summary>
    /// 左右のチーム欄を読む。K/D/A の合計・勝敗・進行度は、どれか1つが読めなくても
    /// 残りは使えるように、別々に見る（合計が読めないだけで勝敗まで捨てないため）。
    /// totalsRead には、K/D/A の合計を実際に読めたチームだけが入る。
    /// </summary>
    private static Dictionary<string, TeamRecord> ReadTeams(List<VisualLine> above, double tableCenter, out HashSet<string> totalsRead)
    {
        var teams = new Dictionary<string, TeamRecord>();
        totalsRead = new HashSet<string>();
        foreach (var (team, isLeft) in new[] { ("astra", true), ("umbra", false) })
        {
            // 左右は語の中心で分ける（文字の中心で分けると、真ん中にある語が途中で切れてしまう）
            var text = string.Concat(above
                .SelectMany(l => l.Words)
                .Where(w => isLeft ? w.CenterX < tableCenter : w.CenterX >= tableCenter)
                .Select(w => TextLayout.Normalize(w.Text)));
            var rec = new TeamRecord();
            var any = false;

            var kda = KdaPattern().Match(text);
            if (kda.Success)
            {
                rec.K = int.Parse(TextLayout.DigitsOnly(kda.Groups[1].Value));
                rec.D = int.Parse(TextLayout.DigitsOnly(kda.Groups[2].Value));
                rec.A = int.Parse(TextLayout.DigitsOnly(kda.Groups[3].Value));
                totalsRead.Add(team);
                any = true;
            }

            var upper = text.ToUpperInvariant();
            if (upper.Contains("WIN")) { rec.Result = "win"; any = true; }
            else if (upper.Contains("LOSE") || upper.Contains("L0SE")) { rec.Result = "lose"; any = true; }

            var progress = ProgressPattern().Match(text);
            if (progress.Success && double.TryParse(progress.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pr))
            {
                rec.Progress = pr;
                any = true;
            }

            if (any) teams[team] = rec;
        }
        return teams;
    }

    /// <summary>
    /// WIN / LOSE の語を探して、アストラ（左のチーム欄）の勝敗を返す。
    /// 両方見つかれば左右の並びだけで決まるので、欄の境目がどこかに関係なく決まる。
    /// 片方しか読めなかったときは、チーム名の位置（なければ表の中心）でどちらの欄かを決める。
    /// </summary>
    private static string? ReadOutcome(List<VisualLine> above, double tableCenter)
    {
        // いちばん確かなのは、表の上に大きく出る「チーム・○○の勝利！」の見出し。
        // 欄の中の WIN / LOSE は字が小さく、下地との差も小さいので読めないことがある。
        var headline = ReadHeadline(above);
        if (headline is not null) return headline;

        var win = FindWord(above, "WIN");
        var lose = FindWord(above, "LOSE", "L0SE", "L05E", "LOSF");
        if (win is not null && lose is not null) return win < lose ? "win" : "lose";
        if (win is null && lose is null) return null;

        // 片方しか読めないときは、どちらの欄にある字かを決める。
        // チーム名が両方読めていればそこからの近さで、読めていなければ表の中心で分ける。
        var x = win ?? lose!.Value;
        var astraAt = TextLayout.FindInLines(above, "アストラ")?.Hit.CenterX;
        var umbraAt = TextLayout.FindInLines(above, "アンブラ")?.Hit.CenterX;
        var onAstra = astraAt is not null && umbraAt is not null
            ? Math.Abs(x - astraAt.Value) <= Math.Abs(x - umbraAt.Value)
            : x < tableCenter;
        var foundWin = win is not null;
        return foundWin == onAstra ? "win" : "lose"; // 見つけた字が、アストラの欄にあったかどうか
    }

    /// <summary>
    /// 「チーム・アンブラの勝利！」のような見出しから、アストラの勝敗を返す。
    /// 隊の名前は1文字くらい読み違えても通るようにする（アンブラ → アンフラ など）。
    /// </summary>
    private static string? ReadHeadline(List<VisualLine> above)
    {
        foreach (var line in above)
        {
            var text = line.Text;
            var at = text.IndexOf("勝利", StringComparison.Ordinal);
            if (at < 4) continue;
            var head = text[..at];
            var toAstra = Nearest(head, "アストラ");
            var toUmbra = Nearest(head, "アンブラ");
            if (toAstra == toUmbra || Math.Min(toAstra, toUmbra) > 1) continue;
            return toAstra < toUmbra ? "win" : "lose";
        }
        return null;

        static int Nearest(string text, string name)
        {
            var best = int.MaxValue;
            for (var i = 0; i + name.Length <= text.Length; i++)
                best = Math.Min(best, TextLayout.Distance(text.Substring(i, name.Length), name));
            return best;
        }
    }

    /// <summary>
    /// その語が出てくる、いちばん下の行での位置。
    /// 隊の名前は表の上の見出し（「チーム・○○の勝利！」）にも出るので、
    /// 素直に上から探すと、チーム欄ではなく見出しを拾ってしまう。
    /// </summary>
    private static TextHit? FindLowest(List<VisualLine> lines, string needle)
    {
        TextHit? best = null;
        foreach (var line in lines)
        {
            var hit = TextLayout.Find(line, needle);
            if (hit is not null && (best is null || hit.Value.Y > best.Value.Y)) best = hit;
        }
        return best;
    }

    /// <summary>語まるごとが targets のどれかと同じものを探して、その中心の X を返す。</summary>
    private static double? FindWord(List<VisualLine> lines, params string[] targets)
    {
        foreach (var line in lines)
        foreach (var w in line.Words)
        {
            var t = TextLayout.Normalize(w.Text).ToUpperInvariant();
            if (Array.IndexOf(targets, t) >= 0) return w.CenterX;
        }
        return null;
    }

    /// <summary>片方の勝敗しか読めなかったら、もう片方は反対にする。どちらも読めなければ進行度で決める。</summary>
    private static void FillResults(Dictionary<string, TeamRecord> teams, ParseResult result)
    {
        var a = teams["astra"];
        var u = teams["umbra"];
        if (a.Result == "" && u.Result != "") a.Result = u.Result == "win" ? "lose" : "win";
        if (u.Result == "" && a.Result != "") u.Result = a.Result == "win" ? "lose" : "win";
        if (a.Result == "" && u.Result == "" && a.Progress is { } ap && u.Progress is { } up && Math.Abs(ap - up) > 0.5)
        {
            a.Result = ap > up ? "win" : "lose";
            u.Result = ap > up ? "lose" : "win";
            result.Warnings.Add("WIN / LOSE を読めなかったので、進行度から勝敗を決めました。");
        }
        if (a.Result == "" || u.Result == "" || a.Result == u.Result)
            result.Errors.Add("勝敗（WIN / LOSE）を読み取れませんでした。");
    }

    private static RankRecord? ReadRank(List<VisualLine> above)
    {
        foreach (var line in above)
        {
            var text = line.Text;
            var arrow = text.IndexOf('→');
            if (arrow < 0) continue;
            string? Side(string s)
            {
                var m = RankPattern().Match(s);
                if (!m.Success) return null;
                var tier = MatchTier(m.Groups[1].Value) ?? m.Groups[1].Value;
                return tier + m.Groups[2].Value + m.Groups[3].Value;
            }
            var before = Side(text[..arrow]);
            var after = Side(text[(arrow + 1)..]);
            if (before is not null || after is not null) return new RankRecord { Before = before, After = after };
        }
        return null;
    }

    private static string? ReadDuration(List<VisualLine> above)
    {
        var label = TextLayout.FindInLines(above, "経過時間");
        if (label is null) return null;
        var (labelLine, hit) = label.Value;
        // 同じ行の右、またはすぐ下の行で、ラベルと横位置が重なる m:ss を探す
        // 1:43 は「1」「:」「43」と3つの語に割れて返ることがあるので、行ごとにつないでから探す
        var lines = above.Where(l => l.CenterY >= labelLine.CenterY - 1 && l.CenterY <= labelLine.Bottom + hit.Height * 4);
        foreach (var line in lines)
        {
            var text = string.Concat(line.Words
                .Where(w => w.Right > hit.X - hit.Width && w.X < hit.Right + hit.Width * 2)
                .OrderBy(w => w.X)
                .Select(w => TextLayout.Normalize(w.Text)))
                .Replace('.', ':');
            var clock = ClockPattern().Match(text);
            if (clock.Success) return $"{int.Parse(clock.Groups[1].Value)}:{clock.Groups[2].Value}";
        }
        return null;
    }

    // ---------- 色 ----------

    /// <summary>左右の色みの差が、これより小さければ「決められない」とみなす。</summary>
    private const double WarmthMargin = 20;

    /// <summary>
    /// アストラが赤寄りの側かどうかを決める。決められなければ null。
    ///
    /// 色を決め打ちにしてはいけない。実際の画面ではアストラが赤・アンブラが青で、
    /// 逆に決め打ちしていたため、チームが入れ替わり、自分の勝敗まで反対に記録されていた。
    ///
    /// 隊の名前（「チーム・アストラ」）を探してその周りを測るやり方は、当てにならなかった。
    /// 「アンブラ」が「アンフラ」と読めるだけで測る場所が変わり、7 秒差で撮った同じ画面が
    /// 逆のチームになった（勝敗も反対になった）。
    /// なので、まずは文字の読みに頼らない「進行度の欄が並ぶ帯」の下地で決める。
    /// この帯は隊の色で塗られていて、左がアストラ・右がアンブラと決まっている。
    /// </summary>
    private static bool? AstraIsWarm(IPixelSource? pixels, List<VisualLine> above)
    {
        var banner = BannerIsWarmOnLeft(pixels, above);
        if (banner is not null) return banner;

        // 帯が測れないときだけ、隊の名前のまわりを測る
        var astra = Warmth(pixels, FindLowest(above, "アストラ"));
        var umbra = Warmth(pixels, FindLowest(above, "アンブラ"));
        if (astra is not null && umbra is not null)
            return Math.Abs(astra.Value - umbra.Value) < WarmthMargin ? null : astra > umbra;
        var one = astra ?? umbra;
        if (one is null || Math.Abs(one.Value) < WarmthMargin) return null;
        return astra is not null ? one > 0 : one < 0;
    }

    /// <summary>
    /// 進行度の欄が並ぶ帯の下地の色みを、左右で比べる。
    /// 「進行度」は 3 文字で読み違えが起きにくく、左右に必ず 1 つずつ出るので、位置の目印に使える。
    /// </summary>
    private static bool? BannerIsWarmOnLeft(IPixelSource? pixels, List<VisualLine> above)
    {
        if (pixels is null) return null;
        var hits = above.SelectMany(l => TextLayout.FindAll(l, "進行度"))
            .Where(h => h.Height > 0).OrderBy(h => h.X).ToList();
        if (hits.Count != 2) return null;
        // 左右の帯が混ざらないよう、2 つの真ん中で区切る
        var mid = (hits[0].CenterX + hits[1].CenterX) / 2;
        var left = BandWarmth(pixels, hits[0], double.MinValue, mid);
        var right = BandWarmth(pixels, hits[1], mid, double.MaxValue);
        if (left is null || right is null) return null;
        return Math.Abs(left.Value - right.Value) < WarmthMargin ? null : left > right;
    }

    /// <summary>「進行度」のまわりを、行の高さぶん上下・左右に広げた帯の色み。</summary>
    private static double? BandWarmth(IPixelSource pixels, TextHit at, double minX, double maxX)
    {
        var pad = at.Height * 1.5;
        var x0 = (int)Math.Max(Math.Max(0, at.X - pad), minX);
        var x1 = (int)Math.Min(Math.Min(pixels.Width - 1, at.Right + pad), maxX);
        var y0 = (int)Math.Max(0, at.Y - at.Height);
        var y1 = (int)Math.Min(pixels.Height - 1, at.Bottom + at.Height);
        return AverageWarmth(pixels, x0, x1, y0, y1);
    }

    /// <summary>その文字のまわりの色み。赤寄りなら正、青寄りなら負。</summary>
    private static double? Warmth(IPixelSource? pixels, TextHit? at)
    {
        if (pixels is null || at is null) return null;
        var pad = at.Value.Height;
        var x0 = (int)Math.Max(0, at.Value.X - pad);
        var x1 = (int)Math.Min(pixels.Width - 1, at.Value.Right + pad);
        var y0 = (int)Math.Max(0, at.Value.Y - pad);
        var y1 = (int)Math.Min(pixels.Height - 1, at.Value.Bottom + pad);
        return AverageWarmth(pixels, x0, x1, y0, y1);
    }

    /// <summary>その四角の中の色み。赤寄りなら正、青寄りなら負。</summary>
    private static double? AverageWarmth(IPixelSource pixels, int x0, int x1, int y0, int y1)
    {
        long sum = 0;
        var n = 0;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var (r, _, b) = pixels.GetPixel(x, y);
            sum += r - b;
            n++;
        }
        return n == 0 ? null : (double)sum / n;
    }

    /// <summary>
    /// ジョブのアイコンの四角を探す。名前のすぐ左にあり、下地が暗い色で塗られている。
    /// （表の下地や行のハイライトは明るいので、暗くて色のある画素の広がりを取れば見つかる）
    /// </summary>
    private static PixelRect? IconArea(IPixelSource? pixels, double nameLeft, double rowHeight, double top, double bottom)
    {
        if (pixels is null) return null;
        var x0 = (int)Math.Max(0, nameLeft - rowHeight * 4);
        var x1 = (int)Math.Min(pixels.Width - 1, nameLeft - 1);
        var y0 = (int)Math.Max(0, top - rowHeight * 0.6);
        var y1 = (int)Math.Min(pixels.Height - 1, bottom + rowHeight * 0.6);
        int left = int.MaxValue, right = int.MinValue, up = int.MaxValue, down = int.MinValue;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var (r, g, b) = pixels.GetPixel(x, y);
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            if (max >= 140 || max - min < 8) continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            up = Math.Min(up, y);
            down = Math.Max(down, y);
        }
        if (right - left < rowHeight || down - up < rowHeight) return null;
        return new PixelRect(left, up, right - left + 1, down - up + 1);
    }

    /// <summary>
    /// ジョブのアイコンの下地の色で、ロール（tank / healer / dps）を見分ける。
    /// 絵柄からジョブそのものは読めないが、下地の色はロールごとに決まっている
    /// （DPS は赤 R80 G48 B47、ヒーラーは緑 R52 G73 B39、タンクは青）。
    /// 暗くて色のある画素だけを見る。行のハイライトや表の下地は明るいので外れる。
    /// </summary>
    public static string? DetectRole(IPixelSource? pixels, PixelRect icon)
    {
        if (pixels is null) return null;
        var x0 = (int)Math.Max(0, icon.X);
        var x1 = (int)Math.Min(pixels.Width - 1, icon.Right);
        var y0 = (int)Math.Max(0, icon.Y);
        var y1 = (int)Math.Min(pixels.Height - 1, icon.Bottom);
        var rs = new List<int>();
        var gs = new List<int>();
        var bs = new List<int>();
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var (r, g, b) = pixels.GetPixel(x, y);
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            if (max >= 140 || max - min < 8) continue; // 明るい画素（ハイライト・下地）と、色のない画素は数えない
            rs.Add(r);
            gs.Add(g);
            bs.Add(b);
        }
        if (rs.Count < 20) return null;
        var (mr, mg, mb) = (Median(rs), Median(gs), Median(bs));
        if (mg > mr && mg > mb) return "healer";
        if (mb > mr && mb > mg) return "tank";
        return "dps";
    }

    /// <summary>名前の文字の色で、どちらの隊かを判定する（どちらの色が赤寄りかは呼ぶ側が決める）。</summary>
    public static string? DetectTeam(IReadOnlyList<OcrWord> nameWords, IPixelSource? pixels, bool astraIsWarm = true)
    {
        if (pixels is null || nameWords.Count == 0) return null;
        long r = 0, b = 0;
        var n = 0;
        foreach (var w in nameWords)
        {
            for (var y = (int)w.Y; y < (int)w.Bottom; y++)
            for (var x = (int)w.X; x < (int)w.Right; x++)
            {
                if (x < 0 || y < 0 || x >= pixels.Width || y >= pixels.Height) continue;
                var (pr, pg, pb) = pixels.GetPixel(x, y);
                var max = Math.Max(pr, Math.Max(pg, pb));
                var min = Math.Min(pr, Math.Min(pg, pb));
                if (max - min < 60) continue; // 色の薄い画素（背景・縁取り）は数えない
                r += pr;
                b += pb;
                n++;
            }
        }
        if (n < 8) return null;
        return r > b == astraIsWarm ? "astra" : "umbra";
    }

    /// <summary>設定の名前に近い行（読み違えを少し許す）。</summary>
    private static int FindSelfByName(List<PlayerRecord> players, string? selfName)
    {
        if (string.IsNullOrWhiteSpace(selfName)) return -1;
        var target = selfName.Replace(" ", "").ToLowerInvariant();
        var scored = players.Select((p, i) => (i, d: TextLayout.Distance(p.Name.Replace(" ", "").ToLowerInvariant(), target)))
            .OrderBy(x => x.d).First();
        return scored.d <= Math.Max(1, target.Length / 6) ? scored.i : -1;
    }

    private static int FindSelfByHighlight(IPixelSource? pixels,
        List<(double Top, double Bottom)> bands, Dictionary<Col, (double Left, double Right)> bounds)
    {
        // 自分の行は背景の色が違う（ハイライト）：行ごとの背景色（画素の中央値）が、他の行の中央値からはっきり離れた行を探す
        if (pixels is null || bands.Count < 3) return -1;
        var left = (int)Math.Max(0, bounds[Col.World].Left);
        var right = (int)Math.Min(pixels.Width - 1, bounds[Col.Heal].Right);
        var colors = bands.Select(band =>
        {
            var rs = new List<int>();
            var gs = new List<int>();
            var bs = new List<int>();
            for (var y = (int)band.Top; y <= (int)band.Bottom; y += 2)
            for (var x = left; x <= right; x += 3)
            {
                if (y < 0 || y >= pixels.Height) continue;
                var (r, g, b) = pixels.GetPixel(x, y);
                rs.Add(r);
                gs.Add(g);
                bs.Add(b);
            }
            return (R: Median(rs), G: Median(gs), B: Median(bs));
        }).ToList();
        var typical = (R: Median(colors.Select(c => c.R)), G: Median(colors.Select(c => c.G)), B: Median(colors.Select(c => c.B)));
        var distances = colors.Select(c => Math.Sqrt(Math.Pow(c.R - typical.R, 2) + Math.Pow(c.G - typical.G, 2) + Math.Pow(c.B - typical.B, 2))).ToList();
        var max = distances.Max();
        var second = distances.OrderByDescending(d => d).Skip(1).First();
        // 一番離れた行が、はっきり離れていて、2番目と区別できるときだけ採用する
        return max > 12 && max > second * 2 ? distances.IndexOf(max) : -1;
    }

    private static int Median(IEnumerable<int> values)
    {
        var list = values.OrderBy(v => v).ToList();
        return list.Count == 0 ? 0 : list[list.Count / 2];
    }

    // ---------- id ----------

    /// <summary>中身から作る id。同じ画面を撮り直しても、読み取りが同じなら同じ id になる。</summary>
    public static string ContentId(MatchRecord m)
    {
        var key = string.Join("|", m.Players
            .OrderBy(p => p.Team, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.World, StringComparer.Ordinal)
            .Select(p => $"{p.Team}/{p.Name}@{p.World}:{p.K},{p.D},{p.A},{p.Dmg},{p.Taken},{p.Heal}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    [GeneratedRegex(@"(\d{1,2}):(\d{2})")]
    private static partial Regex ClockPattern();

    [GeneratedRegex(@"K[:;]?([0-9OoIl|SZB]{1,3})D[:;]?([0-9OoIl|SZB]{1,3})A[:;]?([0-9OoIl|SZB]{1,3})")]
    private static partial Regex KdaPattern();

    [GeneratedRegex(@"進行度(\d{1,3}(?:[.,]\d)?)%?")]
    private static partial Regex ProgressPattern();

    [GeneratedRegex(@"(ブロンズ|シルバー|ゴールド|プラチナ|ダイヤモンド|クリスタル|[ァ-ヶー]{3,6})(\d?)([★☆]*)")]
    private static partial Regex RankPattern();

    [GeneratedRegex(@"[^A-Za-z'-]")]
    private static partial Regex NameTrim();

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex LostSpace();

    [GeneratedRegex(@"(?<=[a-z])I(?=[a-z])")]
    private static partial Regex MisreadL();

    [GeneratedRegex(@"[^A-Za-z]")]
    private static partial Regex WorldTrim();
}
