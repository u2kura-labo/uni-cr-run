using System.Text.RegularExpressions;
using UniCrCapture.Core;

namespace UniCrCapture.Core.Tests;

/// <summary>
/// リザルト画面（1132×578 のスクリーンショット）の配置をまねた、OCR の結果と画素の作り物。
/// Windows の OCR の癖（日本語は1文字ずつの語になる、数字がカンマで割れる など）を再現する。
/// </summary>
public sealed class FakeScreen : IPixelSource
{
    public int Width => 1132 + (int)_tableAt;
    public int Height => 578;

    public List<OcrWord> Words { get; } = new();
    private readonly List<(OcrWord Box, (byte, byte, byte) Color)> _colored = new();
    private readonly List<(double X0, double Y0, double X1, double Y1, (byte, byte, byte) Color)> _panels = new();
    private (double Top, double Bottom)? _highlight;
    /// <summary>表ぜんぶを右にずらす量。左にチャットを置く（＝表の外がある）ときに使う。</summary>
    private double _tableAt;

    public (byte R, byte G, byte B) GetPixel(int x, int y)
    {
        foreach (var (box, color) in _colored)
            if (x >= box.X && x < box.Right && y >= box.Y && y < box.Bottom && (x + y) % 3 != 0) // 文字の画素（すき間あり）
                return color;
        foreach (var (x0, y0, x1, y1, color) in _panels) // 上のチーム欄の下地
            if (x >= x0 && x < x1 && y >= y0 && y < y1)
                return color;
        if (_highlight is { } h && y >= h.Top && y <= h.Bottom) return (246, 228, 188); // 自分の行のハイライト（黄みがかった色）
        return (228, 222, 210); // 表の背景
    }

    public sealed record Player(string Team, string Name, string World, string Tier, int K, int D, int A,
        long Dmg, long Taken, long Heal, string Crystal, bool Self = false);

    /// <summary>実際のスクショの試合の数字（K/D/A の合計もチーム合計と一致する）。名前は架空のものに置き換えてある。</summary>
    public static List<Player> RealMatch() => new()
    {
        new("astra", "Maple Custard", "Garuda", "クリスタル", 0, 4, 14, 1324585, 1826297, 2442242, "6:55"),
        new("astra", "Yuzu Pon", "Ridill", "ダイヤモンド", 3, 3, 14, 2011841, 1124882, 1478040, "3:02"),
        new("astra", "Kinako Mochi", "Tiamat", "ダイヤモンド", 3, 2, 14, 1490909, 1357536, 2951730, "2:38", Self: true),
        new("umbra", "Hinata Sunflower", "Atomos", "ダイヤモンド", 1, 4, 13, 1088078, 1937020, 1734590, "3:34"),
        new("umbra", "Kaito Rain", "Ultima", "クリスタル", 1, 4, 12, 1100614, 1848067, 3586999, "4:55"),
        new("umbra", "Sen Ebi", "Ixion", "クリスタル", 4, 2, 11, 2138685, 1712785, 1522450, "2:55"),
        new("umbra", "Ne'lu Starwind", "Ridill", "ダイヤモンド", 2, 2, 10, 1325923, 1389235, 3469640, "2:03"),
        new("astra", "Tsubame Kazehaya", "Ultima", "クリスタル", 6, 1, 6, 2255449, 1320875, 1321500, "0:38"),
        new("astra", "Rio Aki", "Asura", "ダイヤモンド", 6, 6, 6, 1967260, 1607051, 860779, "3:23"),
        new("umbra", "Nono Everlight", "Chocobo", "ダイヤモンド", 8, 6, 3, 1568341, 2218437, 1666650, "2:30"),
    };

    public sealed class Options
    {
        public bool KdaHeaders { get; init; } = true;
        public bool SplitNumbersAtComma { get; init; }
        public bool MisreadLongVowel { get; init; }
        public bool DotInClock { get; init; }
        public bool NoiseFromJobIcons { get; init; }
        /// <summary>K と D の数字を丸ごと落とす（離れて並ぶ1桁を OCR が拾わないことがある）。</summary>
        public bool DropKdValues { get; init; }
        /// <summary>WIN の字を読み落とす（明るい下地に白なので、LOSE より読めないことが多い）。</summary>
        public bool NoWinWord { get; init; }
        /// <summary>名前とワールドの中の r が「 になり、そこで語が切れる（Kura → "Ku" "「" "a"）。</summary>
        public bool MisreadR { get; init; }
        /// <summary>画面の左にあるチャットが、表の行と同じ高さに出ている。</summary>
        public bool ChatOnTheLeft { get; init; }
        /// <summary>アストラが赤、アンブラが青の配色（実際の画面はこちらだった）。</summary>
        public bool AstraIsRed { get; init; }
        /// <summary>名前の中の l（小文字のエル）が I（大文字のアイ）になる。</summary>
        public bool MisreadL { get; init; }
        /// <summary>後ろ半分の行で K / D / A が丸ごと読めない（実際に起きた）。</summary>
        public bool DropKdaInLaterRows { get; init; }
        /// <summary>表の上の「チーム・○○の勝利！」の見出し。null なら出さない。</summary>
        public string? Headline { get; init; } = "アストラ";
        /// <summary>LOSE の字も読み落とす。</summary>
        public bool NoLoseWord { get; init; }
        public string AstraKda { get; init; } = "K:18 D:16 A:54";
        public string UmbraKda { get; init; } = "K:16 D:18 A:49";
    }

    // 表の K / D / A 列の横位置（見出しと各行で同じ）
    private const double KX = 494, DX = 533, AX = 569, HeaderY = 170, FirstRowY = 202, RowStep = 32;

    /// <summary>
    /// K / D / A の列だけを拡大して読み直したときに返ってくる語。
    /// ResultParser.ReplaceArea に渡して、2回目の読み取りをまねるのに使う。
    /// </summary>
    public static List<OcrWord> KdaWords(List<Player>? players = null)
    {
        players ??= RealMatch();
        var words = new List<OcrWord>
        {
            new("K", KX, HeaderY, 9, 13),
            new("D", DX, HeaderY, 9, 13),
            new("A", AX + 3, HeaderY, 9, 13),
        };
        for (var i = 0; i < players.Count; i++)
        {
            var (p, y) = (players[i], FirstRowY + i * RowStep);
            words.Add(new OcrWord(p.K.ToString(), KX, y, 9, 16));
            words.Add(new OcrWord(p.D.ToString(), DX, y, 9, 16));
            words.Add(new OcrWord(p.A.ToString(), AX, y, 16, 16));
        }
        return words;
    }

    public static FakeScreen Build(List<Player>? players = null, Options? opt = null)
    {
        players ??= RealMatch();
        opt ??= new Options();
        var s = new FakeScreen();
        const double h = 16;
        // チャットを置くときは、その幅だけ表を右にずらす（実際の画面でも、表の左にチャットがある）
        s._tableAt = opt.ChatOnTheLeft ? 420 : 0;

        // ---- 上のバナー ----
        // チーム欄の下地。赤寄り / 青寄りのどちらがどちらの隊かは、この色から決まる
        var warmPanel = ((byte)221, (byte)164, (byte)153);
        var coolPanel = ((byte)150, (byte)167, (byte)205);
        s._panels.Add((90 + s._tableAt, 24, 540 + s._tableAt, 100, opt.AstraIsRed ? warmPanel : coolPanel));
        s._panels.Add((585 + s._tableAt, 24, 1035 + s._tableAt, 100, opt.AstraIsRed ? coolPanel : warmPanel));

        // チーム欄より上に出る見出し（隊の名前がここにも出るので、色を測るときに取り違えやすい）
        if (opt.Headline is not null) s.Japanese($"チーム・{opt.Headline}の勝利！", 400, 4, 16);

        s.Japanese("チーム・アストラ", 115, 43, h);
        s.Japanese("進行度", 360, 34, h);
        s.Latin("50.1%", 435, 30, 95, 22);
        if (!opt.NoWinWord) s.Latin("WIN", 105, 73, 34, 12);
        foreach (var (t, i) in opt.AstraKda.Split(' ').Select((t, i) => (t, i))) s.Latin(t, 298 + i * 88, 63, 60, 18);
        s.Japanese("チーム・アンブラ", 610, 43, h);
        s.Japanese("進行度", 885, 34, h);
        s.Latin("50.0%", 960, 30, 90, 22);
        if (!opt.NoLoseWord) s.Latin("LOSE", 596, 73, 40, 12);
        foreach (var (t, i) in opt.UmbraKda.Split(' ').Select((t, i) => (t, i))) s.Latin(t, 793 + i * 88, 63, 60, 18);

        // ---- 中段 ----
        s.Japanese("すべて", 36, 120, 14);
        s.Latin("REWARD", 434, 108, 80, 12);
        foreach (var (t, x) in new[] { ("0", 395), ("0", 460), ("900", 540), ("500", 598) }) s.Latin(t, x, 130, 30, 16);
        s.Japanese("勝ち星（連勝ボーナス継続中！）", 640, 108, 13);
        s.Japanese("ダイヤモンド5★★★→ダイヤモンド4★★☆", 640, 131, 13);
        s.Japanese("経過時間", 1028, 128, 14);
        s.Latin("14:04", 1030, 149, 60, 20);
        s.Japanese("ソート：アシスト数", 24, 152, 12);

        // ---- 見出し ----
        const double hy = HeaderY;
        s.Japanese("ジョブ", 8, hy, 13);
        s.Japanese("キャラクター名", 80, hy, 13);
        s.Japanese("ホームワールド", 256, hy, 13);
        s.Japanese("階級", 418, hy, 13);
        if (opt.KdaHeaders)
        {
            s.Latin("K", KX, hy, 9, 13);
            s.Latin("D", DX, hy, 9, 13);
            s.Latin("A", AX + 3, hy, 9, 13);
        }
        s.Japanese(opt.MisreadLongVowel ? "総与ダメ一ジ量" : "総与ダメージ量", 625, hy, 13);
        s.Japanese("総被ダメージ量", 764, hy, 13);
        s.Japanese("総与ヒール量", 910, hy, 13);
        s.Japanese("移送時間", 1040, hy, 13);

        // ---- 行 ----
        for (var i = 0; i < players.Count; i++)
        {
            var p = players[i];
            var y = FirstRowY + i * RowStep;
            if (opt.NoiseFromJobIcons) s.Latin("回", 10, y, 14, 16);
            // 名前の色は、上のチーム欄と同じ側の色になる
            var warmText = ((byte)215, (byte)95, (byte)55);
            var coolText = ((byte)40, (byte)70, (byte)170);
            var color = (p.Team == "astra") == opt.AstraIsRed ? warmText : coolText;
            var nameX = 47.0;
            foreach (var part in p.Name.Split(' '))
            {
                // l（小文字のエル）は、小文字にはさまれているときに I と読み違えられる
                var text = opt.MisreadL ? Regex.Replace(part, "(?<=[a-z])l(?=[a-z])", "I") : part;
                s.Word(text, ref nameX, y, 9.0, h, opt.MisreadR, color);
                nameX += 6; // 語と語のあいだ（読み違いで切れたところより、はっきり広い）
            }
            var worldX = 270.0;
            s.Word(p.World, ref worldX, y, 8.5, h, opt.MisreadR, null);
            s.Japanese(p.Tier, 380, y, h); // 階級の文字は K の列まで届かない（実際の画面と同じ）
            var noKda = opt.DropKdaInLaterRows && i >= 5;
            if (!opt.DropKdValues && !noKda)
            {
                s.Latin(p.K.ToString(), KX, y, 9, h);
                s.Latin(p.D.ToString(), DX, y, 9, h);
            }
            if (!noKda) s.Latin(p.A.ToString(), AX, y, 16, h);
            s.Number(p.Dmg, 637, y, opt.SplitNumbersAtComma);
            s.Number(p.Taken, 777, y, opt.SplitNumbersAtComma);
            s.Number(p.Heal, 917, y, opt.SplitNumbersAtComma);
            s.Latin(opt.DotInClock ? p.Crystal.Replace(':', '.') : p.Crystal, 1047, y, 40, h);
            if (p.Self) s._highlight = (y - 8, y + h + 8);
        }

        // ---- 画面の左のチャット（表の行とちょうど同じ高さに重なる） ----
        if (opt.ChatOnTheLeft)
        {
            for (var i = 0; i < players.Count; i++)
            {
                var y = FirstRowY + i * RowStep;
                s.Outside("[22:37]", 20, y, 45, 13);
                s.Outside($"({i + 1}{players[i].Name.Replace(" ", "")}", 70, y, 100, 13);
                s.Outside(players[i].World, 175, y, 55, 13);
                var cw = 12 * 0.95;
                for (var c = 0; c < "よろしくお願いします".Length; c++)
                    s.Outside("よろしくお願いします"[c].ToString(), 235 + c * cw, y, cw, 12);
            }
        }

        // ---- 表の下 ----
        s.Japanese("キャラクター名の表示をネームプレート設定に依存させる", 52, 537, 13);
        s.Japanese("コロセウム退出時間まで1:35", 52, 565, 13);
        return s;
    }

    /// <summary>
    /// 1語を置く。misreadR のときは、最初の r を「 に置き換えて語を3つに割る
    /// （Windows の OCR が実際にやること）。色を渡すと、その語の画素に色を付ける。
    /// </summary>
    private void Word(string text, ref double x, double y, double cw, double h, bool misreadR, (byte, byte, byte)? color)
    {
        var at = misreadR ? text.IndexOf('r') : -1;
        // r のところで「語・「・語」の3つに割れる（幅がせまく、前後とすき間なく並ぶ）
        var pieces = at <= 0
            ? new[] { (Text: text, Width: text.Length * cw) }
            : at + 1 < text.Length
                ? new[] { (Text: text[..at], Width: at * cw), (Text: "「", Width: cw * 0.6), (Text: text[(at + 1)..], Width: (text.Length - at - 1) * cw) }
                : new[] { (Text: text[..at], Width: at * cw), (Text: "「", Width: cw * 0.6) };

        foreach (var piece in pieces)
        {
            var word = Latin(piece.Text, x, y, piece.Width, h);
            if (color is { } c) _colored.Add((word, c));
            x += piece.Width;
        }
    }

    private OcrWord Latin(string text, double x, double y, double w, double h)
    {
        var word = new OcrWord(text, x + _tableAt, y, w, h);
        Words.Add(word);
        return word;
    }

    /// <summary>日本語は1文字ずつの語になる（Windows の OCR と同じ）。</summary>
    private void Japanese(string text, double x, double y, double h)
    {
        var cw = h * 0.95;
        for (var i = 0; i < text.Length; i++) Words.Add(new OcrWord(text[i].ToString(), x + _tableAt + i * cw, y, cw, h));
    }

    /// <summary>表の外に置くもの（チャットなど）。表のずらしを受けない。</summary>
    private void Outside(string text, double x, double y, double w, double h) => Words.Add(new OcrWord(text, x, y, w, h));

    private void Number(long value, double x, double y, bool split)
    {
        var text = value.ToString("N0");
        if (!split || text.Length < 6)
        {
            Latin(text, x, y, text.Length * 8.5, 16);
            return;
        }
        var cut = text.IndexOf(',') + 4;
        Latin(text[..cut], x, y, cut * 8.5, 16);
        Latin(text[cut..], x + cut * 8.5 + 3, y, (text.Length - cut) * 8.5, 16);
    }
}
