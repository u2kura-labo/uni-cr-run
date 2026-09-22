using UniCrCapture.Core;

namespace UniCrCapture.Core.Tests;

/// <summary>
/// リザルト画面（1132×578 のスクリーンショット）の配置をまねた、OCR の結果と画素の作り物。
/// Windows の OCR の癖（日本語は1文字ずつの語になる、数字がカンマで割れる など）を再現する。
/// </summary>
public sealed class FakeScreen : IPixelSource
{
    public int Width => 1132;
    public int Height => 578;

    public List<OcrWord> Words { get; } = new();
    private readonly List<(OcrWord Box, (byte, byte, byte) Color)> _colored = new();
    private (double Top, double Bottom)? _highlight;

    public (byte R, byte G, byte B) GetPixel(int x, int y)
    {
        foreach (var (box, color) in _colored)
            if (x >= box.X && x < box.Right && y >= box.Y && y < box.Bottom && (x + y) % 3 != 0) // 文字の画素（すき間あり）
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
        public string AstraKda { get; init; } = "K:18 D:16 A:54";
        public string UmbraKda { get; init; } = "K:16 D:18 A:49";
    }

    public static FakeScreen Build(List<Player>? players = null, Options? opt = null)
    {
        players ??= RealMatch();
        opt ??= new Options();
        var s = new FakeScreen();
        const double h = 16;

        // ---- 上のバナー ----
        s.Japanese("チーム・アストラ", 115, 43, h);
        s.Japanese("進行度", 360, 34, h);
        s.Latin("50.1%", 435, 30, 95, 22);
        s.Latin("WIN", 105, 73, 34, 12);
        foreach (var (t, i) in opt.AstraKda.Split(' ').Select((t, i) => (t, i))) s.Latin(t, 298 + i * 88, 63, 60, 18);
        s.Japanese("チーム・アンブラ", 610, 43, h);
        s.Japanese("進行度", 885, 34, h);
        s.Latin("50.0%", 960, 30, 90, 22);
        s.Latin("LOSE", 596, 73, 40, 12);
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
        const double hy = 170;
        s.Japanese("ジョブ", 8, hy, 13);
        s.Japanese("キャラクター名", 80, hy, 13);
        s.Japanese("ホームワールド", 256, hy, 13);
        s.Japanese("階級", 418, hy, 13);
        if (opt.KdaHeaders)
        {
            s.Latin("K", 494, hy, 9, 13);
            s.Latin("D", 533, hy, 9, 13);
            s.Latin("A", 572, hy, 9, 13);
        }
        s.Japanese(opt.MisreadLongVowel ? "総与ダメ一ジ量" : "総与ダメージ量", 625, hy, 13);
        s.Japanese("総被ダメージ量", 764, hy, 13);
        s.Japanese("総与ヒール量", 910, hy, 13);
        s.Japanese("移送時間", 1040, hy, 13);

        // ---- 行 ----
        for (var i = 0; i < players.Count; i++)
        {
            var p = players[i];
            var y = 202 + i * 32.0;
            if (opt.NoiseFromJobIcons) s.Latin("回", 10, y, 14, 16);
            var nameX = 47.0;
            foreach (var part in p.Name.Split(' '))
            {
                var w = part.Length * 9.0;
                var word = s.Latin(part, nameX, y, w, h);
                s._colored.Add((word, p.Team == "astra" ? ((byte)40, (byte)70, (byte)170) : ((byte)215, (byte)95, (byte)55)));
                nameX += w + 6;
            }
            s.Latin(p.World, 270, y, p.World.Length * 8.5, h);
            s.Japanese(p.Tier, 405, y, h);
            s.Latin(p.K.ToString(), 494, y, 9, h);
            s.Latin(p.D.ToString(), 533, y, 9, h);
            s.Latin(p.A.ToString(), 569, y, 16, h);
            s.Number(p.Dmg, 637, y, opt.SplitNumbersAtComma);
            s.Number(p.Taken, 777, y, opt.SplitNumbersAtComma);
            s.Number(p.Heal, 917, y, opt.SplitNumbersAtComma);
            s.Latin(opt.DotInClock ? p.Crystal.Replace(':', '.') : p.Crystal, 1047, y, 40, h);
            if (p.Self) s._highlight = (y - 8, y + h + 8);
        }

        // ---- 表の下 ----
        s.Japanese("キャラクター名の表示をネームプレート設定に依存させる", 52, 537, 13);
        s.Japanese("コロセウム退出時間まで1:35", 52, 565, 13);
        return s;
    }

    private OcrWord Latin(string text, double x, double y, double w, double h)
    {
        var word = new OcrWord(text, x, y, w, h);
        Words.Add(word);
        return word;
    }

    /// <summary>日本語は1文字ずつの語になる（Windows の OCR と同じ）。</summary>
    private void Japanese(string text, double x, double y, double h)
    {
        var cw = h * 0.95;
        for (var i = 0; i < text.Length; i++) Words.Add(new OcrWord(text[i].ToString(), x + i * cw, y, cw, h));
    }

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
