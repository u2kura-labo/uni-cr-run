using System.Windows;
using UniCrCapture.Core;

namespace UniCrCapture;

/// <summary>
/// 保存する前に、時刻から決めたマップでいいかを確かめる窓。
/// マップはリザルト画面に出ないので、撮った時刻と経過時間から逆算している。
/// 撮るまでに間があると 1 つ前のマップになることがあるため、目で確かめられるようにする。
/// </summary>
public partial class MapConfirmWindow : Window
{
    internal MapConfirmWindow(MatchRecord match, string? note)
    {
        InitializeComponent();

        var self = match.Players.FirstOrDefault(p => p.Self);
        Question.Text = $"マップは「{match.Map}」でいいですか？";
        Summary.Text = self is null
            ? "この試合を登録します。"
            : $"{(match.Teams[self.Team].Result == "win" ? "勝ち" : "負け")}・" +
              $"{self.K}/{self.D}/{self.A}・与ダメ {self.Dmg:N0}" +
              (string.IsNullOrEmpty(match.Duration) ? "" : $"・経過 {match.Duration}");
        if (!string.IsNullOrEmpty(note))
        {
            Note.Text = note;
            Note.Visibility = Visibility.Visible;
        }

        foreach (var name in GameData.Maps) MapBox.Items.Add(name);
        MapBox.SelectedItem = GameData.Maps.Contains(match.Map) ? match.Map : GameData.Maps[0];
    }

    /// <summary>選ばれたマップ。</summary>
    public string SelectedMap => (string)MapBox.SelectedItem;

    /// <summary>次から確認しないか。</summary>
    public bool StopAsking => DontAsk.IsChecked == true;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
