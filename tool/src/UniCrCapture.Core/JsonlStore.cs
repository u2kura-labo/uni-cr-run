using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniCrCapture.Core;

/// <summary>試合を日付ごとの JSONL（matches-2026-09-22.jsonl）に1行ずつ追記する。</summary>
public sealed class JsonlStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        // 日本語をそのまま書く（\uXXXX にしない）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Folder { get; }

    public JsonlStore(string folder)
    {
        Folder = folder;
    }

    public string PathFor(DateTimeOffset date) =>
        Path.Combine(Folder, $"matches-{date:yyyy-MM-dd}.jsonl");

    public static string Serialize(MatchRecord match) => JsonSerializer.Serialize(match, Json);

    /// <summary>追記する。同じ id がその日のファイルにもうあれば書かずに false を返す。</summary>
    public bool Append(MatchRecord match, DateTimeOffset date)
    {
        Directory.CreateDirectory(Folder);
        var path = PathFor(date);
        if (File.Exists(path) && ContainsId(path, match.Id)) return false;
        File.AppendAllText(path, Serialize(match) + "\n", new UTF8Encoding(false));
        return true;
    }

    private static bool ContainsId(string path, string id)
    {
        var needle = $"\"id\":\"{id}\"";
        foreach (var line in File.ReadLines(path))
            if (line.Contains(needle, StringComparison.Ordinal)) return true;
        return false;
    }
}
