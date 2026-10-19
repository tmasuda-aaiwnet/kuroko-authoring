using Avalonia.Controls;
using Avalonia.Layout;
using Kuroko.Authoring.Project;

namespace Kuroko.Authoring.Editors;

// 「再生の流れ」（nav.json）：ファイルを開いたときの動作・再生器のメニューキー・タイトルが終わったときの動作。
public sealed class NavEditor : UserControl
{
    private readonly AuthoringProject _project;

    public NavEditor(AuthoringProject project)
    {
        _project = project;
        var nav = project.Nav;
        var root = new StackPanel { Spacing = 6 };

        root.Children.Add(Heading("ファイルを開いたとき"));
        var fp = new ActionEditor(project, nav.FirstPlay, "なし（先頭のタイトルを再生）");
        fp.Changed += (_, _) => nav.FirstPlay = fp.Action;
        root.Children.Add(fp);

        root.Children.Add(Heading("再生器のメニューキー"));
        root.Children.Add(Note("再生中に、再生器の「メニュー」「タイトルメニュー」の操作で出すメニュー。"));
        root.Children.Add(KeyRow("メニュー", () => nav.Keys?.Menu, v => (nav.Keys ??= new()).Menu = v));
        root.Children.Add(KeyRow("タイトルメニュー", () => nav.Keys?.TitleMenu, v => (nav.Keys ??= new()).TitleMenu = v));

        root.Children.Add(Heading("タイトルが終わったとき"));
        root.Children.Add(Note("「なし」は、ファイルを開いたときのメニューへ戻る（それも無ければ再生を終える）。"));
        foreach (var t in project.Titles)
        {
            var entry = nav.Titles.FirstOrDefault(x => x.Title == t.Id);
            var ed = new ActionEditor(project, entry?.OnEnd);
            int id = t.Id;
            ed.Changed += (_, _) =>
            {
                var e = nav.Titles.FirstOrDefault(x => x.Title == id);
                if (ed.Action is null) { if (e is not null) nav.Titles.Remove(e); return; }
                if (e is null) nav.Titles.Add(e = new KurokoTinyTitle { Title = id });
                e.OnEnd = ed.Action;
            };
            root.Children.Add(new TextBlock { Text = $"{t.Id}. {t.Name}", Margin = new Avalonia.Thickness(0, 4, 0, 0) });
            root.Children.Add(ed);
        }
        // もう無いタイトルの設定は捨てる。
        nav.Titles.RemoveAll(x => project.Titles.All(t => t.Id != x.Title));

        Content = new ScrollViewer { Content = root };
    }

    private Control KeyRow(string label, Func<string?> get, Action<string?> set)
    {
        var items = new List<string> { "（なし）" };
        items.AddRange(_project.Menus.Select(m => m.Menu.Id));
        var combo = new ComboBox { ItemsSource = items, MinWidth = 160 };
        combo.SelectedItem = get() is { } cur && items.Contains(cur) ? cur : items[0];
        combo.SelectionChanged += (_, _) => set(combo.SelectedIndex <= 0 ? null : combo.SelectedItem as string);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = label, Width = 190, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(combo);
        return row;
    }

    internal static TextBlock Heading(string text)
        => new() { Text = text, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Avalonia.Thickness(0, 10, 0, 2) };

    internal static TextBlock Note(string text)
        => new() { Text = text, Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
}
