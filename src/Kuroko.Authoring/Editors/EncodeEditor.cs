using Avalonia.Controls;
using Avalonia.Layout;
using Kuroko.Authoring.Project;

namespace Kuroko.Authoring.Editors;

// 書き出しの設定：映像（コーデック・画質・速さ・インターレースの扱い）と音声のビットレートの既定値。
// タイトル・音声を足したときに、この値を写す（足した後はタイトル・音声ごとに変えられる）。
// 「全タイトルへ適用」で、足してあるタイトル・音声もこの値にそろえる。メニューの背景の動画・BGM はこの値で符号化する。
public sealed class EncodeEditor : UserControl
{
    public event EventHandler? AppliedToAll;

    public EncodeEditor(AuthoringProject project)
    {
        var enc = project.Encode;
        var root = new StackPanel { Spacing = 6 };
        root.Children.Add(NavEditor.Note("ここの値は既定値です。タイトル・音声を足したときに写し、足した後はタイトルの編集で個別に変えられます。"));

        root.Children.Add(NavEditor.Heading("映像"));
        root.Children.Add(new VideoSettingsPanel(enc));
        root.Children.Add(NavEditor.Note("メニューの背景の動画も、このコーデック・画質・速さで符号化する（インターレースは bwdif で解除）。"));

        root.Children.Add(NavEditor.Heading("音声（Opus）"));
        var abr = new NumericUpDown { Value = enc.AudioBitrateKbps, Minimum = 32, Maximum = 512, Increment = 16, FormatString = "0", Width = 130 };
        abr.ValueChanged += (_, _) => enc.AudioBitrateKbps = (int)(abr.Value ?? enc.AudioBitrateKbps);
        var abrRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        abrRow.Children.Add(abr);
        abrRow.Children.Add(NavEditor.Note("kbps（2ch あたり。5.1ch はこの 3 倍）"));
        root.Children.Add(TitleEditor.Labeled("ビットレート", abrRow));

        // 全タイトルへ適用（上書きするので、その場で確かめてから）。
        root.Children.Add(NavEditor.Heading("足してあるタイトル"));
        var apply = new Button { Content = "全タイトルへ適用…" };
        var confirm = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
        var question = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var yes = new Button { Content = "置き換える" };
        var no = new Button { Content = "やめる" };
        confirm.Children.Add(question);
        confirm.Children.Add(yes);
        confirm.Children.Add(no);
        var done = NavEditor.Note("");
        apply.Click += (_, _) =>
        {
            question.Text = $"全タイトル（{project.Titles.Count}）の映像の設定と、全音声のビットレートを、この値で置き換えます。";
            confirm.IsVisible = true;
            apply.IsVisible = false;
            done.Text = "";
        };
        no.Click += (_, _) => { confirm.IsVisible = false; apply.IsVisible = true; };
        yes.Click += (_, _) =>
        {
            enc.ApplyToAll(project);
            confirm.IsVisible = false;
            apply.IsVisible = true;
            done.Text = $"全タイトル（{project.Titles.Count}）へ適用しました。";
            AppliedToAll?.Invoke(this, EventArgs.Empty);
        };
        root.Children.Add(apply);
        root.Children.Add(confirm);
        root.Children.Add(done);

        Content = new ScrollViewer { Content = root };
    }
}
