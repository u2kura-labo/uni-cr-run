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

    /// <summary>クリスタルコンフリクトのマップ。リザルト画面に出ないので、メニューで選んでもらう。</summary>
    public static readonly IReadOnlyList<string> Maps = new[]
    {
        "パライストラ", "ヴォルカニック・ハート", "クラウドナイン", "東方絡繰御殿", "レッド・サンズ", "ベイサイド・バトルグラウンド",
    };

    public static string JobName(string? code) => Jobs.FirstOrDefault(j => j.Code == code)?.Name ?? "未設定";
}
