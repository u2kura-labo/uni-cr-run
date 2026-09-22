using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace UniCrCapture;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    internal SettingsWindow(AppSettings settings, bool firstRun)
    {
        _settings = settings;
        InitializeComponent();
        SelfName.Text = settings.SelfName;
        SaveFolder.Text = settings.SaveFolder;
        HotkeyText.Text = settings.Hotkey;
        KeepImages.IsChecked = settings.KeepImages;
        KeyIdText.Text = $"鍵の番号：{AppSettings.LoadOrCreateKey().KeyId}";
        if (firstRun) Intro.Text = "はじめに自分のキャラクター名を入れてください。\n" + Intro.Text;
        if (!WindowsOcr.HasJapanese)
            ShowError("この PC には日本語の文字認識が入っていないようです。Windows の「設定 → 時刻と言語 → 言語と地域」で日本語を追加してください。");
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "保存フォルダを選ぶ", InitialDirectory = SaveFolder.Text };
        if (dialog.ShowDialog(this) == true) SaveFolder.Text = dialog.FolderName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelfName.Text))
        {
            ShowError("自分のキャラクター名を入れてください。");
            return;
        }
        if (!Hotkey.TryParse(HotkeyText.Text, out _, out _))
        {
            ShowError("ホットキーの書き方が読めません（例：Ctrl+Shift+F12）。");
            return;
        }
        _settings.SelfName = SelfName.Text.Trim();
        _settings.SaveFolder = string.IsNullOrWhiteSpace(SaveFolder.Text) ? AppSettings.DefaultSaveFolder : SaveFolder.Text.Trim();
        _settings.Hotkey = HotkeyText.Text.Trim();
        _settings.KeepImages = KeepImages.IsChecked == true;
        DialogResult = true;
    }

    private void OnCopyKey(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(AppSettings.LoadOrCreateKey().Export());
        KeyStatus.Text = "鍵をコピーしました。ビューアの「名前の鍵」に貼り付けてください。";
    }

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "鍵のファイルを保存", FileName = "conflict-record.key", Filter = "鍵 (*.key)|*.key" };
        if (dialog.ShowDialog(this) != true) return;
        File.Copy(AppSettings.KeyPath, dialog.FileName, overwrite: true);
        KeyStatus.Text = $"保存しました：{dialog.FileName}";
    }

    private void OnOpenViewer(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(OverlayWindow.ViewerUrl) { UseShellExecute = true });

    private void ShowError(string message)
    {
        Error.Text = message;
        Error.Visibility = Visibility.Visible;
    }
}
