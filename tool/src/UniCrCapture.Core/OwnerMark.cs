using System.Text;

namespace UniCrCapture.Core;

/// <summary>作成者の識別子（o）。自分のキャラ名を Base64 にしただけ（隠す目的ではなく、詳しい人が見れば分かる印）。</summary>
public static class OwnerMark
{
    public static string For(MatchRecord match)
    {
        var self = match.Players.FirstOrDefault(p => p.Self);
        var owner = self is null ? "" : $"{self.Name}@{self.World}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"owner={owner}"));
    }

    public static string? Read(string? mark)
    {
        if (string.IsNullOrEmpty(mark)) return null;
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(mark));
        return text.StartsWith("owner=", StringComparison.Ordinal) ? text["owner=".Length..] : null;
    }
}
