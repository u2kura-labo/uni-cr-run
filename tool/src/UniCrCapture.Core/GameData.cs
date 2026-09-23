namespace UniCrCapture.Core;

/// <summary>メニューに出すジョブとマップ。ジョブの略称はビューアの js/jobs.js と同じ。</summary>
public static class GameData
{
    public sealed record Job(string Code, string Name, string Role);

    public static readonly IReadOnlyList<Job> Jobs = new Job[]
    {
        new("PLD", "ナイト", "タンク"), new("WAR", "戦士", "タンク"), new("DRK", "暗黒騎士", "タンク"), new("GNB", "ガンブレイカー", "タンク"),
        new("WHM", "白魔道士", "ヒーラー"), new("SCH", "学者", "ヒーラー"), new("AST", "占星術師", "ヒーラー"), new("SGE", "賢者", "ヒーラー"),
        new("MNK", "モンク", "メレー"), new("DRG", "竜騎士", "メレー"), new("NIN", "忍者", "メレー"), new("SAM", "侍", "メレー"),
        new("RPR", "リーパー", "メレー"), new("VPR", "ヴァイパー", "メレー"),
        new("BRD", "吟遊詩人", "レンジ"), new("MCH", "機工士", "レンジ"), new("DNC", "踊り子", "レンジ"),
        new("BLM", "黒魔道士", "キャスター"), new("SMN", "召喚士", "キャスター"), new("RDM", "赤魔道士", "キャスター"), new("PCT", "ピクトマンサー", "キャスター"),
    };

    /// <summary>
    /// クリスタルコンフリクトのマップ。1 時間ごとに、この 7 つをこの順で回る。
    /// リザルト画面には出ないので、試合が始まった時刻から決める。
    /// </summary>
    public static readonly IReadOnlyList<string> Maps = new[]
    {
        "ハルモニア戦争図書館",
        "レッド・サンズ",
        "パライストラ",
        "ヴォルカニック・ハート",
        "ベイサイド・バトルグラウンド",
        "クラウドナイン",
        "東方絡繰御殿",
    };

    /// <summary>
    /// 回り方の基準。2026-09-23 10:00（日本時間）＝ 01:00 UTC が「ハルモニア戦争図書館」だった。
    /// 切り替わりは毎正時なので、UTC で数えれば時差のある場所でもずれない。
    /// </summary>
    private static readonly DateTimeOffset RotationStart = new(2026, 9, 23, 1, 0, 0, TimeSpan.Zero);

    /// <summary>その時刻に遊べたマップ。</summary>
    public static string MapAt(DateTimeOffset at)
    {
        var hours = (long)Math.Floor((at - RotationStart).TotalHours);
        var index = (int)(((hours % Maps.Count) + Maps.Count) % Maps.Count);
        return Maps[index];
    }

    /// <summary>その時刻が、マップが切り替わってから何分たったところか。</summary>
    public static double MinutesIntoMap(DateTimeOffset at)
    {
        var hours = (at - RotationStart).TotalHours;
        return (hours - Math.Floor(hours)) * 60;
    }

    public static string JobName(string? code) => Jobs.FirstOrDefault(j => j.Code == code)?.Name ?? "未設定";
}
