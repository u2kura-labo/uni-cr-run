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
/// 4. 名前の文字の色でチーム（青 = アストラ、オレンジ = アンブラ）、設定の名前か行のハイライトで自分の行を決める
/// 5. 各プレイヤーの K/D/A の合計とチーム合計を照らし合わせて、読み違いを警告する
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
        var teams = ReadTeams(above, tableCenter, result);
        var rank = ReadRank(above);
        var duration = ReadDuration(above);

        // ---------- 4. プレイヤー ----------
        var players = new List<PlayerRecord>();
        var rowBands = new List<(double Top, double Bottom)>();
        foreach (var row in rows)
        {
            var p = ToPlayer(row, result);
            if (p is null) continue;
            p.Team = DetectTeam(row.NameWords, pixels) ?? "";
            players.Add(p);
            rowBands.Add((row.Top, row.Bottom));
        }
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

        // チームの情報が読めなかった部分を、プレイヤーから補う
        foreach (var t in new[] { "astra", "umbra" })
        {
            if (!teams.TryGetValue(t, out var team))
            {
                var members = players.Where(p => p.Team == t).ToList();
                teams[t] = team = new TeamRecord { K = members.Sum(p => p.K), D = members.Sum(p => p.D), A = members.Sum(p => p.A) };
                result.Warnings.Add($"{t} のチーム合計が読めなかったので、個人の合計を使いました。");
            }
        }
        FillResults(teams, result);
        if (result.Errors.Count > 0) return result;

        // ---------- 5. 答え合わせ ----------
        foreach (var t in new[] { "astra", "umbra" })
        {
            var members = players.Where(p => p.Team == t).ToList();
            void Check(string label, int sum, int total)
            {
                if (sum != total) result.Warnings.Add($"{t} の {label} 合計が合いません（個人の合計 {sum} / チーム {total}）");
            }
            Check("K", members.Sum(p => p.K), teams[t].K);
            Check("D", members.Sum(p => p.D), teams[t].D);
            Check("A", members.Sum(p => p.A), teams[t].A);
        }

        var match = new MatchRecord
        {
            Ts = options.CapturedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            Map = string.IsNullOrWhiteSpace(options.Map) ? null : options.Map,
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

    private static Dictionary<Col, (double Left, double Right)> ColumnBounds(Dictionary<Col, double> centers)
    {
        var order = Enum.GetValues<Col>().OrderBy(c => centers[c]).ToList();
        var bounds = new Dictionary<Col, (double, double)>();
        for (var i = 0; i < order.Count; i++)
        {
            var left = i == 0 ? double.MinValue : (centers[order[i - 1]] + centers[order[i]]) / 2;
            var right = i == order.Count - 1 ? double.MaxValue : (centers[order[i]] + centers[order[i + 1]]) / 2;
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
        public string Text(Col c) => string.Join(" ", Cells[c].OrderBy(w => w.X).Select(w => w.Text));
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
                var col = bounds.First(b => w.CenterX >= b.Value.Left && w.CenterX < b.Value.Right).Key;
                row.Cells[col].Add(w);
                row.Top = Math.Min(row.Top, w.Y);
                row.Bottom = Math.Max(row.Bottom, w.Bottom);
            }
            // 数字の列が5つ以上埋まっている行だけを、プレイヤーの行とみなす
            var numericCols = new[] { Col.K, Col.D, Col.A, Col.Dmg, Col.Taken, Col.Heal, Col.Crystal };
            var filled = numericCols.Count(c => TextLayout.DigitsOnly(row.Joined(c)).Length > 0);
            if (filled >= 5) rows.Add(row);
            else if (rows.Count > 0 && line.CenterY - rows[^1].Bottom > rowHeight * 3) break; // 表の下の文字に着いた
            if (rows.Count == 10) break;
        }
        return rows;
    }

    private static PlayerRecord? ToPlayer(Row row, ParseResult result)
    {
        var name = CleanName(row.Text(Col.Name));
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
            World = CleanWorld(row.Text(Col.World)),
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

    /// <summary>名前は「英字で始まる語」だけをつなぐ（左のジョブアイコンが記号として読まれることがあるため）。</summary>
    private static string CleanName(string raw)
    {
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => NameTrim().Replace(p, ""))
            .Where(p => p.Length >= 2 && char.IsLetter(p[0]))
            .ToList();
        return string.Join(" ", parts);
    }

    private static string CleanWorld(string raw)
    {
        var s = WorldTrim().Replace(raw, "");
        return s.Length == 0 ? "" : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
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

    private static Dictionary<string, TeamRecord> ReadTeams(List<VisualLine> above, double tableCenter, ParseResult result)
    {
        var teams = new Dictionary<string, TeamRecord>();
        foreach (var (team, isLeft) in new[] { ("astra", true), ("umbra", false) })
        {
            var text = string.Concat(above.Select(l =>
                new string(l.Glyphs.Where(g => isLeft ? g.CenterX < tableCenter : g.CenterX >= tableCenter).Select(g => g.C).ToArray())));
            var kda = KdaPattern().Match(text);
            if (!kda.Success) continue;
            var rec = new TeamRecord
            {
                K = int.Parse(TextLayout.DigitsOnly(kda.Groups[1].Value)),
                D = int.Parse(TextLayout.DigitsOnly(kda.Groups[2].Value)),
                A = int.Parse(TextLayout.DigitsOnly(kda.Groups[3].Value)),
            };
            var upper = text.ToUpperInvariant();
            if (upper.Contains("WIN")) rec.Result = "win";
            else if (upper.Contains("LOSE") || upper.Contains("L0SE")) rec.Result = "lose";
            var progress = ProgressPattern().Match(text);
            if (progress.Success && double.TryParse(progress.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pr))
                rec.Progress = pr;
            teams[team] = rec;
        }
        return teams;
    }

    /// <summary>片方の勝敗しか読めなかったら、もう片方は反対にする。</summary>
    private static void FillResults(Dictionary<string, TeamRecord> teams, ParseResult result)
    {
        var a = teams["astra"];
        var u = teams["umbra"];
        if (a.Result == "" && u.Result != "") a.Result = u.Result == "win" ? "lose" : "win";
        if (u.Result == "" && a.Result != "") u.Result = a.Result == "win" ? "lose" : "win";
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
        var candidates = above
            .Where(l => l.CenterY >= labelLine.CenterY - 1 && l.CenterY <= labelLine.Bottom + hit.Height * 4)
            .SelectMany(l => l.Words)
            .Where(w => w.Right > hit.X - hit.Width && w.X < hit.Right + hit.Width * 2)
            .OrderBy(w => w.Y).ThenBy(w => w.X);
        foreach (var w in candidates)
        {
            var clock = ClockPattern().Match(TextLayout.Normalize(w.Text).Replace('.', ':'));
            if (clock.Success) return $"{int.Parse(clock.Groups[1].Value)}:{clock.Groups[2].Value}";
        }
        return null;
    }

    // ---------- 色 ----------

    /// <summary>名前の文字の色で、青（アストラ）かオレンジ（アンブラ）かを判定する。</summary>
    public static string? DetectTeam(IReadOnlyList<OcrWord> nameWords, IPixelSource? pixels)
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
        return b > r ? "astra" : "umbra";
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

    [GeneratedRegex(@"[^\p{L}'\-]")]
    private static partial Regex NameTrim();

    [GeneratedRegex(@"[^A-Za-z]")]
    private static partial Regex WorldTrim();
}
