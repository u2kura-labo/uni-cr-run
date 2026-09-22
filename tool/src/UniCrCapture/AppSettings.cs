using System.Text.Encodings.Web;
using System.Text.Json;

namespace UniCrCapture;

/// <summary>設定。%APPDATA%\ConflictRecord\settings.json に保存する。</summary>
internal sealed class AppSettings
{
    public string SelfName { get; set; } = "";
    public string? SelfJob { get; set; }
    public string SaveFolder { get; set; } = DefaultSaveFolder;
    public string Hotkey { get; set; } = "Ctrl+Shift+F12";
    public bool KeepImages { get; set; } = true;
    public double? ButtonLeft { get; set; }
    public double? ButtonTop { get; set; }

    // 保存フォルダの中の置き場所。どれをビューアに入れるのか、フォルダ名だけで分かるようにする。
    public string MatchesFolder => Path.Combine(SaveFolder, "1_戦績_ビューアに入れる");
    public string ScreenshotsFolder => Path.Combine(SaveFolder, "2_スクリーンショット");
    public string OcrLogFolder => Path.Combine(SaveFolder, "3_読み取りログ");

    /// <summary>アプリのバージョン（exe のプロパティに出るものと同じ）。</summary>
    public static string Version =>
        typeof(AppSettings).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string DefaultSaveFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Conflict Record");

    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConflictRecord");

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConflictRecord", "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch
        {
            // 壊れていたら初期値で起動する（次に保存したときに直る）
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
