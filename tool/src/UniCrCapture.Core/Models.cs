using System.Text.Json.Serialization;

namespace UniCrCapture.Core;

/// <summary>OCR が返した1語と、その位置（画像のピクセル座標）。</summary>
public sealed record OcrWord(string Text, double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

/// <summary>画像の中の四角（ピクセル座標）。読み直したい範囲を指すのに使う。</summary>
public readonly record struct PixelRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Holds(OcrWord w) => w.CenterX >= X && w.CenterX < Right && w.CenterY >= Y && w.CenterY < Bottom;
}

/// <summary>キャプチャ画像の画素を読むためのもの。チームの色と自分の行のハイライトの判定に使う。</summary>
public interface IPixelSource
{
    int Width { get; }
    int Height { get; }
    (byte R, byte G, byte B) GetPixel(int x, int y);
}

public sealed class ParseOptions
{
    /// <summary>自分のキャラクター名。自分の行を見つけるのに使う（なければ行のハイライトで探す）。</summary>
    public string? SelfName { get; init; }
    /// <summary>自分のジョブ（略称）。リザルト画面のアイコンからはまだ読めないので、ツールで選んだものを入れる。</summary>
    public string? SelfJob { get; init; }
    public string? Map { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public string? SourceFile { get; init; }
    public string AppName { get; init; } = "conflict-record-capture";
}

public sealed class ParseResult
{
    public MatchRecord? Match { get; set; }
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool Success => Match is not null && Errors.Count == 0;
}

// ---------- JSONL の1行（ビューアの README「JSONL の形式（v1）」と同じ） ----------

public sealed class MatchRecord
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("ts")] public string Ts { get; set; } = "";
    [JsonPropertyName("map")] public string? Map { get; set; }
    [JsonPropertyName("duration")] public string? Duration { get; set; }
    [JsonPropertyName("rank")] public RankRecord? Rank { get; set; }
    [JsonPropertyName("teams")] public Dictionary<string, TeamRecord> Teams { get; set; } = new();
    [JsonPropertyName("players")] public List<PlayerRecord> Players { get; set; } = new();
    [JsonPropertyName("src")] public string? Src { get; set; }
    [JsonPropertyName("app")] public string? App { get; set; }
    [JsonPropertyName("warnings")] public List<string>? Warnings { get; set; }
    /// <summary>作成者の識別子（自分のキャラ名を Base64 にしたもの）。OwnerMark.For で作る。</summary>
    [JsonPropertyName("o")] public string? Owner { get; set; }
}

public sealed class RankRecord
{
    [JsonPropertyName("before")] public string? Before { get; set; }
    [JsonPropertyName("after")] public string? After { get; set; }
}

public sealed class TeamRecord
{
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("progress")] public double? Progress { get; set; }
    [JsonPropertyName("k")] public int K { get; set; }
    [JsonPropertyName("d")] public int D { get; set; }
    [JsonPropertyName("a")] public int A { get; set; }
}

public sealed class PlayerRecord
{
    [JsonPropertyName("team")] public string Team { get; set; } = "";
    [JsonPropertyName("job")] public string? Job { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("world")] public string World { get; set; } = "";
    [JsonPropertyName("tier")] public string Tier { get; set; } = "";
    [JsonPropertyName("k")] public int K { get; set; }
    [JsonPropertyName("d")] public int D { get; set; }
    [JsonPropertyName("a")] public int A { get; set; }
    [JsonPropertyName("dmg")] public long Dmg { get; set; }
    [JsonPropertyName("taken")] public long Taken { get; set; }
    [JsonPropertyName("heal")] public long Heal { get; set; }
    [JsonPropertyName("crystal")] public string? Crystal { get; set; }
    [JsonPropertyName("self")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Self { get; set; }
}
