using System.Text.Json;
using System.Windows.Media.Imaging;
using UniCrCapture.Core;

namespace UniCrCapture;

/// <summary>
/// 覚えたジョブのアイコンを、%APPDATA%\ConflictRecord\jobicons.json に置いておく。
///
/// 覚え方は2つ。
///   A. 画像を読み込む：PLD.png のようにジョブの略称を名前にした画像のフォルダを指定する
///   B. 自分の行から覚える：自分のジョブは設定で分かっているので、撮るたびに自動で覚える
/// どちらも覚えていないジョブは、ロール（tank / healer / dps）だけ記録される。
/// </summary>
internal sealed class JobIconStore
{
    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConflictRecord", "jobicons.json");

    private Dictionary<string, double[]> _icons = new();

    public IReadOnlyDictionary<string, double[]> Icons => _icons;

    public int Count => _icons.Count;

    public static JobIconStore Load()
    {
        var store = new JobIconStore();
        try
        {
            if (File.Exists(FilePath))
                store._icons = JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            // 壊れていたら、覚えていない状態から始める
        }
        return store;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_icons));
        }
        catch
        {
            // 覚えられなくても、撮影そのものは続ける
        }
    }

    /// <summary>1つ覚える。すでに同じものを覚えていれば何もしない。</summary>
    public bool Remember(string job, double[]? signature)
    {
        if (signature is null || string.IsNullOrWhiteSpace(job)) return false;
        if (_icons.TryGetValue(job, out var known) && JobIcons.Similarity(known, signature) >= JobIcons.SameJob) return false;
        _icons[job] = signature;
        Save();
        return true;
    }

    /// <summary>
    /// 画像のフォルダから覚える。ファイル名がジョブの略称（PLD.png、WHM.png など）。
    /// 覚えた数と、名前が分からなかったファイルを返す。
    /// </summary>
    public (int Learned, List<string> Skipped) Import(string folder)
    {
        var skipped = new List<string>();
        var learned = 0;
        foreach (var file in Directory.GetFiles(folder).OrderBy(f => f, StringComparer.Ordinal))
        {
            var job = Path.GetFileNameWithoutExtension(file).Trim().ToUpperInvariant();
            if (GameData.Jobs.All(j => j.Code != job))
            {
                skipped.Add($"{Path.GetFileName(file)}：ジョブの略称になっていません");
                continue;
            }
            try
            {
                var image = ScreenCapture.Load(file);
                var pixels = ScreenCapture.ToBgra(image);
                var signature = JobIcons.Signature(pixels, new PixelRect(0, 0, pixels.Width, pixels.Height));
                if (signature is null) skipped.Add($"{Path.GetFileName(file)}：絵柄が読み取れません");
                else if (Remember(job, signature)) learned++;
            }
            catch (Exception ex)
            {
                skipped.Add($"{Path.GetFileName(file)}：{ex.Message}");
            }
        }
        return (learned, skipped);
    }
}
