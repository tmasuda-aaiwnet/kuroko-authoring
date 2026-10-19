using Avalonia.Controls;
using Avalonia.Layout;
using Kuroko.Authoring.Project;

namespace Kuroko.Authoring.Editors;

// 動作（kuroko-tiny の action）を選ぶ部品。種類を選び、種類に応じてタイトル・章・メニュー・番号を入れる。
// 値は Action（null＝「なし」）。変わったら Changed。
public sealed class ActionEditor : UserControl
{
    private static readonly string[] KindLabels =
        { "なし", "タイトルを再生", "メニューを出す", "続きから（メニューの前の位置へ）", "音声を選ぶ", "字幕を選ぶ", "再生を終える" };

    private readonly AuthoringProject _project;
    private readonly ComboBox _kind = new() { ItemsSource = KindLabels, MinWidth = 220 };
    private readonly ComboBox _title = new() { MinWidth = 160 };
    private readonly NumericUpDown _chapter = new() { Minimum = 0, Maximum = 999, Increment = 1, FormatString = "0", Width = 130 };
    private readonly ComboBox _menu = new() { MinWidth = 140 };
    private readonly NumericUpDown _number = new() { Minimum = 0, Maximum = 99, Increment = 1, FormatString = "0", Width = 130 };
    private readonly TextBlock _chapterLabel = new() { Text = "章（0＝最初から）", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _numberLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private bool _loading;

    public event EventHandler? Changed;

    // noneLabel：「なし」の表示（例：firstPlay では「なし（先頭のタイトルを再生）」）。
    public ActionEditor(AuthoringProject project, KurokoTinyAction? action, string? noneLabel = null)
    {
        _project = project;
        if (noneLabel is not null)
        {
            var labels = (string[])KindLabels.Clone();
            labels[0] = noneLabel;
            _kind.ItemsSource = labels;
        }
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new Control[] { _kind, _title, _chapterLabel, _chapter, _menu, _numberLabel, _number })
        {
            c.Margin = new Avalonia.Thickness(0, 2, 8, 2);
            row.Children.Add(c);
        }
        Content = row;
        RefreshChoices();
        Load(action);
        _kind.SelectionChanged += (_, _) => { UpdateVisibility(); Raise(); };
        _title.SelectionChanged += (_, _) => Raise();
        _menu.SelectionChanged += (_, _) => Raise();
        _chapter.ValueChanged += (_, _) => Raise();
        _number.ValueChanged += (_, _) => Raise();
    }

    // タイトル・メニューの一覧を作り直す（プロジェクトが変わったとき）。
    public void RefreshChoices()
    {
        bool was = _loading;
        _loading = true;
        var t = SelectedTitleId; var m = _menu.SelectedItem as string;
        _title.ItemsSource = _project.Titles.Select(x => $"{x.Id}. {x.Name}").ToList();
        _menu.ItemsSource = _project.Menus.Select(x => x.Menu.Id).ToList();
        if (t is int ti) _title.SelectedIndex = _project.Titles.FindIndex(x => x.Id == ti);
        if (m is not null) _menu.SelectedItem = m;
        _loading = was;
    }

    private int? SelectedTitleId => _title.SelectedIndex >= 0 && _title.SelectedIndex < _project.Titles.Count
        ? _project.Titles[_title.SelectedIndex].Id : null;

    private void Load(KurokoTinyAction? a)
    {
        _loading = true;
        int kind = a?.Kind switch
        {
            KurokoTinyActionKind.PlayTitle => 1,
            KurokoTinyActionKind.ShowMenu => 2,
            KurokoTinyActionKind.Resume => 3,
            KurokoTinyActionKind.SetAudio => 4,
            KurokoTinyActionKind.SetSubtitle => 5,
            KurokoTinyActionKind.Stop => 6,
            _ => 0,
        };
        _kind.SelectedIndex = kind;
        if (a?.PlayTitle is { } pt)
        {
            _title.SelectedIndex = _project.Titles.FindIndex(x => x.Id == pt.Title);
            _chapter.Value = pt.Chapter ?? 0;
        }
        else if (_project.Titles.Count > 0) _title.SelectedIndex = 0;
        _menu.SelectedItem = a?.ShowMenu ?? _project.Menus.FirstOrDefault()?.Menu.Id;
        _number.Value = a?.SetAudio ?? a?.SetSubtitle ?? (kind == 4 ? 1 : 0);
        UpdateVisibility();
        _loading = false;
    }

    private void UpdateVisibility()
    {
        int k = _kind.SelectedIndex;
        _title.IsVisible = _chapter.IsVisible = _chapterLabel.IsVisible = k == 1;
        _menu.IsVisible = k == 2;
        _number.IsVisible = _numberLabel.IsVisible = k is 4 or 5;
        _numberLabel.Text = k == 4 ? "音声の番号（1〜）" : "字幕の番号（0＝なし）";
        _number.Minimum = k == 4 ? 1 : 0;
    }

    public KurokoTinyAction? Action => _kind.SelectedIndex switch
    {
        1 when SelectedTitleId is int tid => KurokoTinyAction.PlayTitleAction(tid, (int)(_chapter.Value ?? 0) is int c && c > 0 ? c : null),
        2 when _menu.SelectedItem is string mid => KurokoTinyAction.ShowMenuAction(mid),
        3 => KurokoTinyAction.ResumeAction(),
        4 => KurokoTinyAction.SetAudioAction(Math.Max(1, (int)(_number.Value ?? 1))),
        5 => KurokoTinyAction.SetSubtitleAction(Math.Max(0, (int)(_number.Value ?? 0))),
        6 => KurokoTinyAction.StopAction(),
        _ => null,
    };

    private void Raise()
    {
        if (!_loading) Changed?.Invoke(this, EventArgs.Empty);
    }
}
