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

    public static string DefaultSaveFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Conflict Record");

    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConflictRecord");

    /// <summary>名前の鍵のファイル。はじめて使うときに作る。消すと、それまでの JSONL の名前は戻せなくなる。</summary>
    public static string KeyPath => Path.Combine(Folder, "conflict-record.key");

    public static UniCrCapture.Core.NameCipher LoadOrCreateKey()
    {
        if (File.Exists(KeyPath)) return UniCrCapture.Core.NameCipher.Parse(File.ReadAllText(KeyPath));
        var cipher = UniCrCapture.Core.NameCipher.Create();
        Directory.CreateDirectory(Folder);
        File.WriteAllText(KeyPath, cipher.Export());
        return cipher;
    }

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
