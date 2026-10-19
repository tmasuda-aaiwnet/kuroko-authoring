using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Kuroko.Authoring.Project;

namespace Kuroko.Authoring.Editors;

// タイトル 1 つの編集：名前・映像の設定・音声と字幕（元のファイルの中のものと外部のファイル。並べ替え・有効/無効・
// 言語・名前・音声のビットレート）・章。
public sealed class TitleEditor : UserControl
{
    private static readonly FilePickerFileType AudioFileType = new("音声") { Patterns = new[] { "*.flac", "*.wav", "*.mka", "*.m4a", "*.mp3", "*.opus", "*.ogg", "*.ac3", "*.aac" } };
    private static readonly FilePickerFileType SrtFileType = new("SRT の字幕") { Patterns = new[] { "*.srt" } };

    private readonly AuthoringTitle _title;
    private readonly AuthoringEncodeSettings _defaults;
    private readonly StackPanel _audioRows = new() { Spacing = 4 };
    private readonly StackPanel _subRows = new() { Spacing = 4 };
    private readonly StackPanel _chapterRows = new() { Spacing = 4 };

    public event EventHandler? NameChanged;

    // defaults：書き出しの設定（足した音声のビットレートの既定値）。
    public TitleEditor(AuthoringTitle title, AuthoringEncodeSettings defaults)
    {
        _title = title;
        _defaults = defaults;
        var root = new StackPanel { Spacing = 6 };

        root.Children.Add(NavEditor.Heading($"タイトル {title.Id}"));
        var name = new TextBox { Text = title.Name ?? "", MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
        name.TextChanged += (_, _) => { title.Name = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text; NameChanged?.Invoke(this, EventArgs.Empty); };
        root.Children.Add(Labeled("名前", name));
        root.Children.Add(Labeled("映像", new SelectableTextBlock { Text = title.VideoFile, TextWrapping = Avalonia.Media.TextWrapping.Wrap }));

        root.Children.Add(NavEditor.Heading("映像の設定"));
        root.Children.Add(new VideoSettingsPanel(title.Video ??= new AuthoringVideoSettings()));

        root.Children.Add(HeadingWithButton("音声", "追加…", "外部の音声のファイルを足す", AddAudioAsync));
        root.Children.Add(NavEditor.Note("上から順に krk の音声 1、2…になる。元のファイルの中の音声は、チェックを外すと入れない。"));
        root.Children.Add(_audioRows);
        root.Children.Add(HeadingWithButton("字幕", "追加…", "SRT の字幕を足す", AddSubtitleAsync));
        root.Children.Add(NavEditor.Note("上から順に krk の字幕 1、2…になる。元のファイルの中の字幕は、今は TS の ARIB 字幕だけを並べる。"));
        root.Children.Add(_subRows);

        root.Children.Add(NavEditor.Heading("章"));
        root.Children.Add(NavEditor.Note("時刻は 時:分:秒.ミリ秒（例 0:12:34.500）。章が無ければ、元のファイルの章を使う。"));
        root.Children.Add(_chapterRows);
        var add = new Button { Content = "章を追加" };
        add.Click += (_, _) =>
        {
            double last = title.Chapters.Count > 0 ? title.Chapters.Max(c => c.StartMs) + 60000 : 0;
            title.Chapters.Add(new KurokoNoneChapter { StartMs = last });
            RebuildChapters();
        };
        root.Children.Add(add);

        RebuildTracks();
        RebuildChapters();
        Content = new ScrollViewer { Content = root };
    }

    // 見出しと、その右の追加のボタン。
    private static Control HeadingWithButton(string heading, string button, string tip, Func<Task> onClick)
    {
        var h = NavEditor.Heading(heading);
        h.VerticalAlignment = VerticalAlignment.Center;
        var b = new Button { Content = button, Margin = new Avalonia.Thickness(0, 10, 0, 2), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(b, tip);
        b.Click += async (_, _) => await onClick();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(h);
        row.Children.Add(b);
        return row;
    }

    private async Task<IEnumerable<string>> PickFilesAsync(string title, FilePickerFileType type)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return Array.Empty<string>();
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = new[] { type },
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    // 外部の音声を一覧の末尾へ（ビットレートは書き出しの設定の値）。
    private async Task AddAudioAsync()
    {
        foreach (var path in await PickFilesAsync($"タイトル {_title.Id} に足す音声を選ぶ", AudioFileType))
            _title.Audio.Add(new AuthoringAudioTrack { Path = path, Name = Path.GetFileNameWithoutExtension(path), BitrateKbps = _defaults.AudioBitrateKbps });
        RebuildTracks();
    }

    private async Task AddSubtitleAsync()
    {
        foreach (var path in await PickFilesAsync($"タイトル {_title.Id} に足す字幕（SRT）を選ぶ", SrtFileType))
            _title.Subtitles.Add(new AuthoringSubtitleTrack { Kind = AuthoringSubtitleTrack.KindSrt, Path = path, Name = Path.GetFileNameWithoutExtension(path) });
        RebuildTracks();
    }

    // 音声・字幕の行。
    private void RebuildTracks()
    {
        _audioRows.Children.Clear();
        if (!_title.SourceTracksListed)
            _audioRows.Children.Add(NavEditor.Note("（元のファイルの中の音声・字幕はまだ調べていません。ffmpeg の場所を確かめてください。書き出しのときにも調べます）"));
        int srcNo = 0;
        foreach (var a in _title.Audio.ToList())
        {
            string label = a.IsSource
                ? $"元の音声 {++srcNo}（{a.Codec ?? "?"}・{(a.Channels > 0 ? $"{a.Channels}ch" : "?")}）"
                : Path.GetFileName(a.Path ?? "");
            var kbps = new NumericUpDown { Value = a.BitrateKbps, Minimum = 32, Maximum = 512, Increment = 16, FormatString = "0", Width = 110 };
            kbps.ValueChanged += (_, _) => a.BitrateKbps = (int)(kbps.Value ?? a.BitrateKbps);
            var extra = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            extra.Children.Add(kbps);
            extra.Children.Add(new TextBlock { Text = "kbps", VerticalAlignment = VerticalAlignment.Center });
            _audioRows.Children.Add(TrackRow(_title.Audio, a, label, a.IsSource, a.Enabled, v => a.Enabled = v,
                a.Language, a.Name, v => a.Language = v, v => a.Name = v, a.Path, extra, RebuildTracks));
        }
        if (_title.Audio.Count == 0) _audioRows.Children.Add(NavEditor.Note("（なし）"));

        _subRows.Children.Clear();
        foreach (var s in _title.Subtitles.ToList())
        {
            string label = s.IsSource
                ? $"元の ARIB 字幕（PID 0x{s.Pid ?? 0:X4}）"
                : Path.GetFileName(s.Path ?? "");
            _subRows.Children.Add(TrackRow(_title.Subtitles, s, label, s.IsSource, s.Enabled, v => s.Enabled = v,
                s.Language, s.Name, v => s.Language = v, v => s.Name = v, s.Path, null, RebuildTracks));
        }
        if (_title.Subtitles.Count == 0) _subRows.Children.Add(NavEditor.Note("（なし）"));
    }

    // 1 行：有効（元のファイルの中のもの）・上へ・下へ・名前・言語・表示名・（音声はビットレート）・削除（外部のファイル）。
    private static Control TrackRow<T>(List<T> list, T item, string label, bool isSource, bool enabled, Action<bool> setEnabled,
        string? lang, string? name, Action<string?> setLang, Action<string?> setName, string? path, Control? extra, Action rebuild)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        if (isSource)
        {
            var on = new CheckBox { IsChecked = enabled, Width = 70, Content = "有効" };
            on.IsCheckedChanged += (_, _) => setEnabled(on.IsChecked == true);
            row.Children.Add(on);
        }
        else
            row.Children.Add(new Border { Width = 70 });

        var up = new Button { Content = "↑", IsEnabled = list.IndexOf(item) > 0 };
        up.Click += (_, _) => { Move(list, item, -1); rebuild(); };
        var down = new Button { Content = "↓", IsEnabled = list.IndexOf(item) < list.Count - 1 };
        down.Click += (_, _) => { Move(list, item, +1); rebuild(); };
        ToolTip.SetTip(up, "上へ");
        ToolTip.SetTip(down, "下へ");
        row.Children.Add(up);
        row.Children.Add(down);

        row.Children.Add(new TextBlock { Text = label, Width = 220, VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
        var langBox = new TextBox { Text = lang ?? "", Width = 80, PlaceholderText = "jpn" };
        langBox.TextChanged += (_, _) => setLang(string.IsNullOrWhiteSpace(langBox.Text) ? null : langBox.Text.Trim());
        var nameBox = new TextBox { Text = name ?? "", Width = 200, PlaceholderText = "表示名" };
        nameBox.TextChanged += (_, _) => setName(string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text);
        row.Children.Add(new TextBlock { Text = "言語", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(langBox);
        row.Children.Add(nameBox);
        if (extra is not null) row.Children.Add(extra);

        if (!isSource)
        {
            var del = new Button { Content = "削除" };
            del.Click += (_, _) => { list.Remove(item); rebuild(); };
            row.Children.Add(del);
        }
        if (path is not null) ToolTip.SetTip(row, path);
        return row;
    }

    private static void Move<T>(List<T> list, T item, int delta)
    {
        int i = list.IndexOf(item), j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
    }

    private void RebuildChapters()
    {
        _chapterRows.Children.Clear();
        int n = 1;
        foreach (var c in _title.Chapters.OrderBy(c => c.StartMs).ToList())
        {
            var time = new TextBox { Text = FormatTime(c.StartMs), Width = 130 };
            time.LostFocus += (_, _) =>
            {
                if (TryParseTime(time.Text, out double ms)) { c.StartMs = ms; RebuildChapters(); }
                else time.Text = FormatTime(c.StartMs);
            };
            var name = new TextBox { Text = c.Name ?? "", Width = 240, PlaceholderText = "章の名前（任意）" };
            name.TextChanged += (_, _) => c.Name = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text;
            var del = new Button { Content = "削除" };
            del.Click += (_, _) => { _title.Chapters.Remove(c); RebuildChapters(); };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock { Text = $"{n++}", Width = 24, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(time);
            row.Children.Add(name);
            row.Children.Add(del);
            _chapterRows.Children.Add(row);
        }
    }

    internal static Control Labeled(string label, Control c)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = label, Width = 190, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(c);
        return row;
    }

    public static string FormatTime(double ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    // 「時:分:秒.ミリ秒」「分:秒」「秒」を受ける。
    public static bool TryParseTime(string? s, out double ms)
    {
        ms = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Trim().Split(':');
        if (parts.Length > 3) return false;
        double total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v < 0) return false;
            total = total * 60 + v;
        }
        ms = Math.Round(total * 1000, 3);
        return true;
    }
}
