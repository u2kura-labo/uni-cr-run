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
