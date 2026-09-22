using System.Text.Json;
using UniCrCapture.Core;
using Xunit;

namespace UniCrCapture.Core.Tests;

public class ResultParserTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 22, 21, 14, 3, TimeSpan.FromHours(9));

    private static ParseResult Parse(FakeScreen screen, string? selfName = "Kinako Mochi", string? selfJob = null) =>
        ResultParser.Parse(screen.Words, screen, new ParseOptions
        {
            SelfName = selfName,
            SelfJob = selfJob,
            CapturedAt = At,
            SourceFile = "cap_20260922_211403.png",
        });

    [Fact]
    public void ReadsTheWholeResultScreen()
    {
        var r = Parse(FakeScreen.Build(), selfJob: "SGE");

        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Empty(r.Warnings);
        var m = r.Match!;
        Assert.Equal(1, m.V);
        Assert.Equal("2026-09-22T21:14:03+09:00", m.Ts);
        Assert.Equal("14:04", m.Duration);
        Assert.Equal("ダイヤモンド5★★★", m.Rank?.Before);
        Assert.Equal("ダイヤモンド4★★☆", m.Rank?.After);

        Assert.Equal("win", m.Teams["astra"].Result);
        Assert.Equal(50.1, m.Teams["astra"].Progress);
        Assert.Equal((18, 16, 54), (m.Teams["astra"].K, m.Teams["astra"].D, m.Teams["astra"].A));
        Assert.Equal("lose", m.Teams["umbra"].Result);
        Assert.Equal(50.0, m.Teams["umbra"].Progress);
        Assert.Equal((16, 18, 49), (m.Teams["umbra"].K, m.Teams["umbra"].D, m.Teams["umbra"].A));

        Assert.Equal(10, m.Players.Count);
        var expected = FakeScreen.RealMatch();
        for (var i = 0; i < 10; i++)
        {
            var (e, p) = (expected[i], m.Players[i]);
            Assert.Equal(e.Team, p.Team);
            Assert.Equal(e.Name, p.Name);
            Assert.Equal(e.World, p.World);
            Assert.Equal(e.Tier, p.Tier);
            Assert.Equal((e.K, e.D, e.A), (p.K, p.D, p.A));
            Assert.Equal((e.Dmg, e.Taken, e.Heal), (p.Dmg, p.Taken, p.Heal));
            Assert.Equal(e.Crystal, p.Crystal);
            Assert.Equal(e.Self, p.Self);
        }
        Assert.Equal("SGE", m.Players[2].Job);
        Assert.Null(m.Players[0].Job);
    }

    [Fact]
    public void FindsSelfByRowHighlightWhenNameIsNotSet()
    {
        var r = Parse(FakeScreen.Build(), selfName: null);
        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Equal("Kinako Mochi", r.Match!.Players.Single(p => p.Self).Name);
    }

    [Fact]
    public void ToleratesTypicalOcrQuirks()
    {
        var screen = FakeScreen.Build(opt: new FakeScreen.Options
        {
            KdaHeaders = false,          // 1文字の見出しを読み落とす
            SplitNumbersAtComma = true,  // 1,324,585 → "1,324" ",585"
            MisreadLongVowel = true,     // ダメージ → ダメ一ジ
            DotInClock = true,           // 6:55 → 6.55
            NoiseFromJobIcons = true,    // ジョブアイコンが記号として読まれる
        });
        var r = Parse(screen);

        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Empty(r.Warnings);
        var expected = FakeScreen.RealMatch();
        Assert.Equal(expected.Select(e => (e.Name, e.K, e.D, e.A, e.Dmg, e.Crystal)),
            r.Match!.Players.Select(p => (p.Name, p.K, p.D, p.A, p.Dmg, p.Crystal ?? "")));
    }

    /// <summary>
    /// 実際に起きた読み落とし：チーム欄の K/D/A 合計が読めず、WIN も読めず、LOSE だけ読めた画面。
    /// 合計が読めないだけで勝敗まで捨ててしまうと、試合ごと保存できなくなる。
    /// </summary>
    [Fact]
    public void DecidesWinLoseFromTheLoseWordAloneWhenTeamTotalsAreUnreadable()
    {
        var screen = FakeScreen.Build(opt: new FakeScreen.Options
        {
            NoWinWord = true,
            Headline = null, // 見出しも読めないので、LOSE の語だけが手がかり
            AstraKda = "ロ:ロ ロ:ロ ロ:ロ",
            UmbraKda = "ロ:ロ ロ:ロ ロ:ロ",
        });
        var r = Parse(screen);

        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Equal("win", r.Match!.Teams["astra"].Result);
        Assert.Equal("lose", r.Match.Teams["umbra"].Result);
        // 合計は読めなかったので、各プレイヤーの合計で埋める
        Assert.Contains(r.Warnings, w => w.Contains("astra のチーム合計が読めなかった"));
        Assert.Equal((18, 16, 54), (r.Match.Teams["astra"].K, r.Match.Teams["astra"].D, r.Match.Teams["astra"].A));
    }

    /// <summary>
    /// 実際に起きた読み落とし：K と D の列が1桁ずつ離れて並ぶため、画面ぜんぶを1枚で渡すと
    /// 10行ぜんぶ落ちる。SmallNumberArea が返す範囲を拡大して読み直せば、元どおりになる。
    /// </summary>
    [Fact]
    public void RereadingTheSmallNumberAreaRecoversDroppedKAndD()
    {
        var screen = FakeScreen.Build(opt: new FakeScreen.Options { DropKdValues = true });

        var first = Parse(screen);
        Assert.True(first.Success, string.Join("\n", first.Errors));
        Assert.Contains(first.Warnings, w => w.Contains("の K を読めませんでした"));
        Assert.Contains(first.Warnings, w => w.Contains("の D を読めませんでした"));

        var area = ResultParser.SmallNumberArea(screen.Words);
        Assert.NotNull(area);
        // K〜A の3列だけを指していて、となりの階級・総与ダメージ量の文字は入らない
        Assert.All(screen.Words.Where(w => area!.Value.Holds(w)), w => Assert.Contains(w.Text, new[] { "K", "D", "A" }.Concat(
            FakeScreen.RealMatch().Select(p => p.A.ToString())).ToArray()));

        var reread = ResultParser.ReplaceArea(screen.Words, area!.Value, FakeScreen.KdaWords());
        var second = ResultParser.Parse(reread, screen, new ParseOptions { SelfName = "Kinako Mochi", CapturedAt = At });

        Assert.True(second.Success, string.Join("\n", second.Errors));
        Assert.Empty(second.Warnings);
        Assert.Equal(FakeScreen.RealMatch().Select(p => (p.Name, p.K, p.D, p.A)),
            second.Match!.Players.Select(p => (p.Name, p.K, p.D, p.A)));
    }

    /// <summary>
    /// 実際に起きた読み違い：日本語で読ませているので、名前とワールドの中の r が「 になり、
    /// そこで語まで切れる（Kura → "Ku" "「" "a"）。切れたままだと自分の行も見つからない。
    /// </summary>
    [Fact]
    public void PutsNamesBackTogetherWhenRIsMisreadAsABracket()
    {
        var r = Parse(FakeScreen.Build(opt: new FakeScreen.Options { MisreadR = true }));

        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Empty(r.Warnings); // 自分の行も名前で見つかる（ハイライトに頼らない）
        var expected = FakeScreen.RealMatch();
        Assert.Equal(expected.Select(e => (e.Name, e.World)), r.Match!.Players.Select(p => (p.Name, p.World)));
        Assert.True(r.Match.Players.Single(p => p.Self).Name == "Kinako Mochi");
    }

    /// <summary>
    /// FF14 の名前で大文字になるのは先頭と ' - の次だけ、という決まりを手がかりに直す。
    /// 全部大文字の語は読み違い（Ko → KO）、小文字のあとの大文字は空白の読み落とし。
    /// </summary>
    [Fact]
    public void FixesNamesThatWereReadInAllCapitalsOrLostTheirSpace()
    {
        var players = FakeScreen.RealMatch();
        players[0] = players[0] with { Name = "Maple CUSTARD" };  // Custard → CUSTARD
        players[1] = players[1] with { Name = "YuzuPon" };        // Yuzu Pon → YuzuPon
        var r = Parse(FakeScreen.Build(players));

        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Equal("Maple Custard", r.Match!.Players[0].Name);
        Assert.Equal("Yuzu Pon", r.Match.Players[1].Name);
    }

    /// <summary>
    /// 実際に起きた取り違え：画面の左のチャットが表の行と同じ高さにあると、
    /// 名前の欄に入り込んで、名前が「(3St-valkyrieAi Tiamat Uni Ku」のようになっていた。
    /// </summary>
    [Fact]
    public void IgnoresTheChatNextToTheTable()
    {
        var r = Parse(FakeScreen.Build(opt: new FakeScreen.Options { ChatOnTheLeft = true }));

        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Empty(r.Warnings);
        Assert.Equal(FakeScreen.RealMatch().Select(e => (e.Name, e.World)),
            r.Match!.Players.Select(p => (p.Name, p.World)));
    }

    /// <summary>
    /// 実際の画面ではアストラが赤・アンブラが青だった。青をアストラと決め打ちしていたため、
    /// 2つの隊が丸ごと入れ替わり、自分の勝敗まで反対に記録されていた。
    /// どちらの配色でも、チーム欄の下地の色に合わせて正しく振り分けられること。
    /// </summary>
    [Theory]
    [InlineData(false)] // アストラが青
    [InlineData(true)]  // アストラが赤
    public void TellsTheTeamsApartWhicheverSideIsRed(bool astraIsRed)
    {
        var r = Parse(FakeScreen.Build(opt: new FakeScreen.Options { AstraIsRed = astraIsRed }));

        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Empty(r.Warnings);
        Assert.Equal(FakeScreen.RealMatch().Select(e => (e.Name, e.Team)),
            r.Match!.Players.Select(p => (p.Name, p.Team)));
        // 自分はアストラなので、勝ちとして記録される
        Assert.Equal("astra", r.Match.Players.Single(p => p.Self).Team);
        Assert.Equal("win", r.Match.Teams["astra"].Result);
    }

    /// <summary>
    /// l（小文字のエル）は I と読み違えられる（Salim → SaIim）。
    /// 「小文字のあとの大文字は空白の読み落とし」の規則をそのまま当てると、Sa Iim と割れてしまう。
    /// </summary>
    [Fact]
    public void KeepsNamesTogetherWhenLIsMisreadAsCapitalI()
    {
        var r = Parse(FakeScreen.Build(opt: new FakeScreen.Options { MisreadL = true }));

        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Equal(FakeScreen.RealMatch().Select(e => e.Name), r.Match!.Players.Select(p => p.Name));
    }

    /// <summary>
    /// 実際に起きた悪循環：K / D / A が読めない行は「数字の列が5つ以上」の条件を満たさず、
    /// 行として数えられない → 拡大して読み直す帯もそこまで届かない → いつまでも読めない。
    /// 10 行そろうこと、読み直しの帯が最後の行まで届くこと。
    /// </summary>
    [Fact]
    public void CountsRowsEvenWhenTheirKdaWasNotRead()
    {
        var screen = FakeScreen.Build(opt: new FakeScreen.Options { DropKdaInLaterRows = true });
        var r = Parse(screen);

        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Equal(10, r.Match!.Players.Count);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("プレイヤーの行が"));

        var area = ResultParser.SmallNumberArea(screen.Words);
        Assert.NotNull(area);
        Assert.True(area!.Value.Bottom >= 202 + 9 * 32, $"読み直しの帯が最後の行まで届いていない（下端 {area.Value.Bottom}）");
    }

    /// <summary>
    /// 表の上の「チーム・○○の勝利！」は大きい字なので、欄の中の小さな WIN / LOSE より確実に読める。
    /// 隊の名前が1文字読み違えられていても通ること（アンブラ → アンフラ）。
    /// </summary>
    [Fact]
    public void ReadsTheWinnerFromTheHeadlineWhenNothingElseCanBeRead()
    {
        var screen = FakeScreen.Build(opt: new FakeScreen.Options
        {
            Headline = "アンフラ", // アンブラの読み違い
            NoWinWord = true,
            NoLoseWord = true,
            AstraKda = "ロ:ロ ロ:ロ ロ:ロ",
            UmbraKda = "ロ:ロ ロ:ロ ロ:ロ",
        });
        var r = Parse(screen);

        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Equal("lose", r.Match!.Teams["astra"].Result);
        Assert.Equal("win", r.Match.Teams["umbra"].Result);
    }

    [Fact]
    public void WarnsWhenTeamTotalsDoNotAddUp()
    {
        var players = FakeScreen.RealMatch();
        players[0] = players[0] with { K = 7 }; // 読み違えた想定
        var r = Parse(FakeScreen.Build(players));

        Assert.True(r.Success);
        Assert.Contains(r.Warnings, w => w.Contains("astra の K 合計が合いません"));
        Assert.Contains(r.Match!.Warnings!, w => w.Contains("astra の K 合計が合いません"));
    }

    [Fact]
    public void RejectsScreensThatAreNotTheResult()
    {
        var r = ResultParser.Parse(
            new[] { new OcrWord("ファイナルファンタジー", 10, 10, 200, 20), new OcrWord("123", 10, 40, 30, 20) },
            null, new ParseOptions());
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Contains("見つかりませんでした"));
    }

    [Fact]
    public void FallsBackToHighlightWithAWarningWhenTheSettingsNameIsNotInTheTable()
    {
        var r = Parse(FakeScreen.Build(), selfName: "Someone Else");
        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Equal("Kinako Mochi", r.Match!.Players.Single(p => p.Self).Name);
        Assert.Contains(r.Warnings, w => w.Contains("Someone Else"));
    }

    [Fact]
    public void ReportsWhenSelfIsNotFoundAtAll()
    {
        var screen = FakeScreen.Build();
        // 画素なし（ハイライトで探せない）で、名前も合わない
        var r = ResultParser.Parse(screen.Words, new NoHighlight(screen), new ParseOptions { SelfName = "Someone Else" });
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Contains("Someone Else"));
    }

    /// <summary>名前の色は見えるが、行のハイライトがない画面。</summary>
    private sealed class NoHighlight(FakeScreen inner) : IPixelSource
    {
        public int Width => inner.Width;
        public int Height => inner.Height;
        public (byte R, byte G, byte B) GetPixel(int x, int y)
        {
            var c = inner.GetPixel(x, y);
            return c == (246, 228, 188) ? ((byte)228, (byte)222, (byte)210) : c;
        }
    }

    [Fact]
    public void IdDependsOnContentNotOnCaptureTime()
    {
        var a = Parse(FakeScreen.Build()).Match!;
        var b = ResultParser.Parse(FakeScreen.Build().Words, FakeScreen.Build(), new ParseOptions
        {
            SelfName = "Kinako Mochi",
            CapturedAt = At.AddMinutes(1),
        }).Match!;
        Assert.Equal(a.Id, b.Id);

        var players = FakeScreen.RealMatch();
        players[3] = players[3] with { Dmg = 1 };
        var c = Parse(FakeScreen.Build(players)).Match!;
        Assert.NotEqual(a.Id, c.Id);
    }

    [Fact]
    public void SerializesToTheViewerSchema()
    {
        var json = JsonlStore.Serialize(Parse(FakeScreen.Build(), selfJob: "SGE").Match!);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("win", root.GetProperty("teams").GetProperty("astra").GetProperty("result").GetString());
        var self = root.GetProperty("players")[2];
        Assert.True(self.GetProperty("self").GetBoolean());
        Assert.Equal("SGE", self.GetProperty("job").GetString());
        Assert.False(root.GetProperty("players")[0].TryGetProperty("self", out _)); // 自分以外には書かない
        Assert.Contains("ダイヤモンド", json); // 日本語はエスケープしない
        Assert.DoesNotContain("\n", json);
    }
}

public class JsonlStoreTests
{
    [Fact]
    public void AppendsToDailyFilesAndSkipsKnownIds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "unicr-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonlStore(dir);
            var day1 = new DateTimeOffset(2026, 9, 22, 23, 50, 0, TimeSpan.FromHours(9));
            var day2 = day1.AddMinutes(20); // 日付が変わる

            var m1 = new MatchRecord { Id = "aaa" };
            var m2 = new MatchRecord { Id = "bbb" };
            Assert.True(store.Append(m1, day1));
            Assert.False(store.Append(m1, day1)); // 同じ id は書かない
            Assert.True(store.Append(m2, day2));

            Assert.Single(File.ReadAllLines(store.PathFor(day1)));
            Assert.Single(File.ReadAllLines(store.PathFor(day2)));
            Assert.EndsWith("matches-2026-09-22.jsonl", store.PathFor(day1));
            Assert.EndsWith("matches-2026-09-23.jsonl", store.PathFor(day2));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
