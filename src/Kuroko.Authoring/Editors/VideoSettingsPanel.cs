using Avalonia.Controls;
using Avalonia.Layout;
using Kuroko.Authoring.Project;

namespace Kuroko.Authoring.Editors;

// 映像の符号化の設定の欄（コーデック・画質（CRF）・速さ・インターレースの扱い）。
// 書き出しの設定（既定値）とタイトルの編集で同じものを使う。
public sealed class VideoSettingsPanel : UserControl
{
    private static readonly (string Codec, string Label)[] Codecs =
    {
        (AuthoringVideoSettings.Av1, "AV1（小さい）"),
        (AuthoringVideoSettings.H265, "H.265 / HEVC"),
        (AuthoringVideoSettings.H264, "H.264 / AVC（どこでも再生しやすい・大きめ）"),
    };

    private static readonly string[] InterlaceLabels =
    {
        "フィールド分離（半分の高さ・2 本。再生器が元の表示に戻す）",
        "Weave（1 枚に織り込んだまま）",
        "yadif で解除（倍のフレームレート）",
        "bwdif で解除（倍のフレームレート）",
    };

    public VideoSettingsPanel(AuthoringVideoSettings vs)
    {
        var root = new StackPanel { Spacing = 6 };

        var codec = new ComboBox { ItemsSource = Codecs.Select(c => c.Label).ToList(), MinWidth = 320 };
        codec.SelectedIndex = Math.Max(0, Array.FindIndex(Codecs, c => c.Codec == vs.VideoCodec));
        root.Children.Add(TitleEditor.Labeled("コーデック", codec));

        var crf = new NumericUpDown { Value = vs.Crf, Minimum = 0, Maximum = 63, Increment = 1, FormatString = "0", Width = 130 };
        crf.ValueChanged += (_, _) => vs.Crf = (int)(crf.Value ?? vs.Crf);
        var crfNote = NavEditor.Note("");
        var crfRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        crfRow.Children.Add(crf);
        crfRow.Children.Add(crfNote);
        root.Children.Add(TitleEditor.Labeled("画質（CRF）", crfRow));

        var svt = new NumericUpDown { Value = vs.Preset, Minimum = 0, Maximum = 13, Increment = 1, FormatString = "0", Width = 130 };
        svt.ValueChanged += (_, _) => vs.Preset = (int)(svt.Value ?? vs.Preset);
        var svtRow = TitleEditor.Labeled("速さ（0〜13・大きいほど速い）", svt);
        root.Children.Add(svtRow);

        var x26x = new ComboBox { ItemsSource = AuthoringVideoSettings.X26xPresets, MinWidth = 160 };
        x26x.SelectedItem = AuthoringVideoSettings.X26xPresets.Contains(vs.X26xPreset) ? vs.X26xPreset : "medium";
        x26x.SelectionChanged += (_, _) => { if (x26x.SelectedItem is string s) vs.X26xPreset = s; };
        var x26xRow = TitleEditor.Labeled("速さ（preset）", x26x);
        root.Children.Add(x26xRow);

        void Show()
        {
            bool av1 = vs.VideoCodec == AuthoringVideoSettings.Av1;
            svtRow.IsVisible = av1;
            x26xRow.IsVisible = !av1;
            crfNote.Text = $"小さいほど高画質で大きい（{Codecs.First(c => c.Codec == vs.VideoCodec).Label.Split('（')[0].Trim()} の目安は {AuthoringVideoSettings.DefaultCrf(vs.VideoCodec)}）";
        }
        codec.SelectionChanged += (_, _) =>
        {
            if (codec.SelectedIndex < 0) return;
            string next = Codecs[codec.SelectedIndex].Codec;
            if (next == vs.VideoCodec) return;
            // コーデックを変えたら、CRF を目安の値へ（コーデックごとに尺度が違う）。
            vs.VideoCodec = next;
            crf.Value = AuthoringVideoSettings.DefaultCrf(next);
            Show();
        };
        Show();

        var interlace = new ComboBox { ItemsSource = InterlaceLabels, MinWidth = 360 };
        interlace.SelectedIndex = Math.Max(0, Array.IndexOf(AuthoringVideoSettings.InterlaceModes, AuthoringVideoSettings.NormalizeInterlace(vs.Interlace)));
        interlace.SelectionChanged += (_, _) =>
        {
            if (interlace.SelectedIndex >= 0) vs.Interlace = AuthoringVideoSettings.InterlaceModes[interlace.SelectedIndex];
        };
        root.Children.Add(TitleEditor.Labeled("インターレースの扱い", interlace));
        root.Children.Add(NavEditor.Note("インターレースの扱いは、映像がインターレースのときだけ効く（プログレッシブの映像はそのまま）。"
            + "フィールド分離と Weave はインターレースのまま持ち、再生器が設定に従って表示する。yadif・bwdif は書き出しの時に解除する。"));

        Content = root;
    }
}
