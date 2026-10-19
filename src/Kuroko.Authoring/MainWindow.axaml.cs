using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kuroko.Authoring.Editors;
using Kuroko.Authoring.Export;
using Kuroko.Authoring.Project;

namespace Kuroko.Authoring;

// プロジェクトの新規・開く・保存、タイトル（映像・音声・字幕）とメニュー（背景）の追加・削除、検査、
// 書き出し（ffmpeg で AV1＋Opus に符号化して krk にまとめる）、書き出した krk を既定のプレーヤーで開く（プレビュー）。
// 右側は選んだものの編集画面：タイトル（TitleEditor）・メニュー（MenuEditor）・再生の流れ（NavEditor）。
public partial class MainWindow : Window
{
    private AuthoringProject _project = new();
    private string? _projectPath;
    private string? _lastOutput;
    private CancellationTokenSource? _exportCts;
    private bool _refreshing; // 一覧を作り直している間は選択の変化で編集画面を作り直さない

    private static readonly FilePickerFileType ProjectFileType = new("Kuroko のプロジェクト") { Patterns = new[] { "*" + AuthoringProject.FileExtension } };
    private static readonly FilePickerFileType VideoFileType = new("映像") { Patterns = new[] { "*.mkv", "*.mp4", "*.mov", "*.webm", "*.ts", "*.m2ts", "*.avi" } };
    private static readonly FilePickerFileType KrkFileType = new("Kuroko") { Patterns = new[] { "*.krk" } };
    private static readonly FilePickerFileType BackgroundFileType = new("背景（画像・動画）") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.mkv", "*.mp4", "*.mov", "*.webm" } };

    public MainWindow() : this(null) { } // 画面の設計ツール用（引数なしの公開コンストラクタが要る）

    // projectPath：起動の引数で渡されたプロジェクト（.kap。ファイルの関連付けから開くとき）。
    public MainWindow(string? projectPath)
    {
        InitializeComponent();
        Refresh();
        if (projectPath is not null && File.Exists(projectPath)) OpenProject(projectPath);
#if DEBUG
        // 画面の確かめ用：KUROKO_AUTHORING_SHOW=menu:<id>／title:<番号>／nav で、起動直後にその編集画面を出す。
        if (Environment.GetEnvironmentVariable("KUROKO_AUTHORING_SHOW") is { } show)
        {
            if (show == "nav") OnShowNav(null, new RoutedEventArgs());
            else if (show.StartsWith("menu:")) MenuList.SelectedIndex = _project.Menus.FindIndex(m => m.Menu.Id == show[5..]);
            else if (show.StartsWith("title:") && int.TryParse(show[6..], out int tid)) TitleList.SelectedIndex = _project.Titles.FindIndex(t => t.Id == tid);
        }
#endif
    }

    private void Refresh()
    {
        RefreshLists();
        Title = $"{(_projectPath is null ? _project.Name : Path.GetFileName(_projectPath))} - Kuroko Authoring";
        EditorHost.Content = null;
        FfmpegPathBox.Text = _project.Encode.FfmpegPath ?? "";
    }

    // 一覧の表示だけを作り直す（選択はそのまま。編集画面は作り直さない）。
    private void RefreshLists()
    {
        _refreshing = true;
        int ts = TitleList.SelectedIndex, ms = MenuList.SelectedIndex;
        TitleList.ItemsSource = _project.Titles.Select(t => $"{t.Id}. {t.Name ?? Path.GetFileName(t.VideoFile)}").ToList();
        MenuList.ItemsSource = _project.Menus.Select(m => m.Menu.Id).ToList();
        TitleList.SelectedIndex = ts < _project.Titles.Count ? ts : -1;
        MenuList.SelectedIndex = ms < _project.Menus.Count ? ms : -1;
        _refreshing = false;
    }

    private void OnShowNav(object? sender, RoutedEventArgs e)
    {
        _refreshing = true;
        TitleList.SelectedIndex = -1;
        MenuList.SelectedIndex = -1;
        _refreshing = false;
        EditorHost.Content = new NavEditor(_project);
    }

    // ffmpeg の実行ファイルを選ぶ（ffprobe は同じフォルダのものを使う）。
    private async void OnBrowseFfmpeg(object? sender, RoutedEventArgs e)
    {
        var type = OperatingSystem.IsWindows()
            ? new FilePickerFileType("ffmpeg") { Patterns = new[] { "ffmpeg.exe", "*.exe" } }
            : new FilePickerFileType("ffmpeg") { Patterns = new[] { "*" } };
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "ffmpeg を選ぶ",
            FileTypeFilter = new[] { type },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        FfmpegPathBox.Text = path;
        _project.Encode.FfmpegPath = path;
    }

    private void OnShowEncode(object? sender, RoutedEventArgs e)
    {
        _refreshing = true;
        TitleList.SelectedIndex = -1;
        MenuList.SelectedIndex = -1;
        _refreshing = false;
        EditorHost.Content = new EncodeEditor(_project);
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void OnNew(object? sender, RoutedEventArgs e)
    {
        _project = new AuthoringProject();
        _projectPath = null;
        ProblemList.ItemsSource = null;
        Refresh();
        SetStatus("新しいプロジェクトを作りました。");
    }

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "プロジェクトを開く",
            FileTypeFilter = new[] { ProjectFileType },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        OpenProject(path);
    }

    private void OpenProject(string path)
    {
        try
        {
            _project = AuthoringProject.Load(path);
            _projectPath = path;
            ProblemList.ItemsSource = null;
            Refresh();
            SetStatus($"開きました：{path}");
            _ = ListSourceTracksAsync(_project.Titles.ToList());
        }
        catch (Exception ex)
        {
            SetStatus($"開けませんでした：{ex.Message}");
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_projectPath is null) { await SaveAsAsync(); return; }
        SaveTo(_projectPath);
    }

    private async void OnSaveAs(object? sender, RoutedEventArgs e) => await SaveAsAsync();

    private async Task SaveAsAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "プロジェクトを保存",
            SuggestedFileName = _project.Name + AuthoringProject.FileExtension,
            DefaultExtension = AuthoringProject.FileExtension,
            FileTypeChoices = new[] { ProjectFileType },
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;
        SaveTo(path);
    }

    private void SaveTo(string path)
    {
        try
        {
            _project.Encode.FfmpegPath = NullIfEmpty(FfmpegPathBox.Text);
            _project.Save(path);
            _projectPath = path;
            Refresh();
            SetStatus($"保存しました：{path}");
        }
        catch (Exception ex)
        {
            SetStatus($"保存できませんでした：{ex.Message}");
        }
    }

    private void OnValidate(object? sender, RoutedEventArgs e)
    {
        var problems = _project.Validate();
        ProblemList.ItemsSource = problems.Count == 0 ? new[] { "問題はありません。" } : problems;
        SetStatus(problems.Count == 0 ? "検査：問題はありません。" : $"検査：{problems.Count} 件の問題があります。");
    }

    private async void OnAddTitle(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "タイトルにする映像を選ぶ",
            AllowMultiple = true,
            FileTypeFilter = new[] { VideoFileType },
        });
        var added = new List<AuthoringTitle>();
        foreach (var path in files.Select(f => f.TryGetLocalPath()).OfType<string>())
        {
            var t = new AuthoringTitle
            {
                Id = _project.NextTitleId(),
                Name = Path.GetFileNameWithoutExtension(path),
                VideoFile = path,
                Video = _project.Encode.CopyVideo(), // 書き出しの設定の値を写す
            };
            _project.Titles.Add(t);
            added.Add(t);
        }
        Refresh();
        await ListSourceTracksAsync(added);
    }

    // 元のファイルの中の音声・字幕を ffprobe で調べて、タイトルの一覧に入れる（足したとき・開いたとき）。
    // ffmpeg が見つからなければ、書き出しのときに入れる。
    private async Task ListSourceTracksAsync(List<AuthoringTitle> titles)
    {
        var todo = titles.Where(t => !t.SourceTracksListed && File.Exists(t.VideoFile)).ToList();
        if (todo.Count == 0) return;
        FfmpegTools tools;
        try { tools = FfmpegTools.Locate(NullIfEmpty(FfmpegPathBox.Text)); }
        catch (Exception ex)
        {
            SetStatus($"元のファイルの中の音声・字幕を調べられません（{ex.Message}）。書き出しのときに調べます。");
            return;
        }
        var project = _project;
        foreach (var t in todo)
        {
            try
            {
                var info = await Task.Run(() => MediaInfo.Probe(tools, t.VideoFile));
                t.ListSourceTracks(info.SourceAudio(), info.SourceSubtitles(), project.Encode);
            }
            catch (Exception ex)
            {
                SetStatus($"調べられませんでした：{t.VideoFile}（{ex.Message}）");
            }
        }
        if (SelectedTitle is { } sel && todo.Contains(sel)) ShowTitle(sel);
    }

    private void OnRemoveTitle(object? sender, RoutedEventArgs e)
    {
        if (TitleList.SelectedIndex < 0) return;
        _project.Titles.RemoveAt(TitleList.SelectedIndex);
        Refresh();
    }

    private async void OnAddMenu(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "メニューの背景（画像か動画）を選ぶ",
            FileTypeFilter = new[] { BackgroundFileType },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;

        string id = _project.NewMenuId();
        string ext = Path.GetExtension(path).ToLowerInvariant();
        bool isImage = ext is ".png" or ".jpg" or ".jpeg";
        // krk の中では、画像は PNG（JPEG は書き出しのときに PNG へ変換）、動画は符号化して mkv にする。
        string name = isImage ? "background.png" : "background.mkv";
        var menu = new AuthoringMenu
        {
            Menu = new KurokoTinyMenu
            {
                Id = id,
                Background = isImage ? new KurokoTinyBackground { Image = name } : new KurokoTinyBackground { Video = name, Loop = true },
            },
        };
        menu.Assets[name] = path;
        _project.Menus.Add(menu);
        _project.Nav.FirstPlay ??= KurokoTinyAction.ShowMenuAction(id);
        Refresh();
        MenuList.SelectedIndex = _project.Menus.Count - 1;
    }

    private void OnRemoveMenu(object? sender, RoutedEventArgs e)
    {
        if (MenuList.SelectedIndex < 0) return;
        _project.Menus.RemoveAt(MenuList.SelectedIndex);
        Refresh();
    }

    private void OnTitleSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || TitleList.SelectedIndex < 0 || TitleList.SelectedIndex >= _project.Titles.Count) return;
        _refreshing = true;
        MenuList.SelectedIndex = -1;
        _refreshing = false;
        ShowTitle(_project.Titles[TitleList.SelectedIndex]);
    }

    private void ShowTitle(AuthoringTitle t)
    {
        var ed = new TitleEditor(t, _project.Encode);
        // 名前の一覧への反映は、入力のたびではなく編集画面を離れるとき（一覧を作り直すと入力中の欄が切れるため）。
        ed.LostFocus += (_, _) => RefreshLists();
        EditorHost.Content = ed;
    }

    private void OnMenuSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || MenuList.SelectedIndex < 0 || MenuList.SelectedIndex >= _project.Menus.Count) return;
        _refreshing = true;
        TitleList.SelectedIndex = -1;
        _refreshing = false;
        var ed = new MenuEditor(_project, _project.Menus[MenuList.SelectedIndex], () => NullIfEmpty(FfmpegPathBox.Text));
        ed.MenuRenamed += (_, _) => RefreshLists();
        EditorHost.Content = ed;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private AuthoringTitle? SelectedTitle
        => TitleList.SelectedIndex >= 0 && TitleList.SelectedIndex < _project.Titles.Count ? _project.Titles[TitleList.SelectedIndex] : null;

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        _project.Encode.FfmpegPath = NullIfEmpty(FfmpegPathBox.Text);
        var problems = _project.Validate();
        if (problems.Count > 0)
        {
            ProblemList.ItemsSource = problems;
            SetStatus($"検査で {problems.Count} 件の問題があるので書き出せません。");
            return;
        }
        FfmpegTools tools;
        try { tools = FfmpegTools.Locate(_project.Encode.FfmpegPath); }
        catch (Exception ex) { SetStatus(ex.Message); return; }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "krk を書き出す",
            SuggestedFileName = _project.Name + ".krk",
            DefaultExtension = ".krk",
            FileTypeChoices = new[] { KrkFileType },
        });
        var outPath = file?.TryGetLocalPath();
        if (outPath is null) return;

        _exportCts = new CancellationTokenSource();
        var ct = _exportCts.Token;
        SetExporting(true);
        var exporter = new KrkExporter(_project, tools);
        var progress = new Progress<KrkExporter.Progress>(p =>
        {
            ExportProgress.Value = p.Fraction;
            SetStatus($"{p.Fraction:P0}　{p.Phase}");
        });
        try
        {
            await Task.Run(() => exporter.Export(outPath, progress, ct), ct);
            _lastOutput = outPath;
            OpenOutputButton.IsEnabled = true;
            ProblemList.ItemsSource = exporter.Log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            SetStatus($"書き出しました：{outPath}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("書き出しを中止しました。");
        }
        catch (Exception ex)
        {
            ProblemList.ItemsSource = ex.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            SetStatus("書き出しに失敗しました。");
        }
        finally
        {
            SetExporting(false);
            _exportCts.Dispose();
            _exportCts = null;
        }
    }

    private void OnCancelExport(object? sender, RoutedEventArgs e) => _exportCts?.Cancel();

    private void SetExporting(bool on)
    {
        ExportButton.IsEnabled = !on;
        CancelButton.IsVisible = on;
        ExportProgress.IsVisible = on;
        ExportProgress.Value = 0;
    }

    // プレビュー：書き出した krk を OS の既定のアプリ（krk に関連付けたプレーヤー）で開く。
    private void OnOpenOutput(object? sender, RoutedEventArgs e)
    {
        if (_lastOutput is null || !File.Exists(_lastOutput)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_lastOutput) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"開けませんでした（krk を開くアプリが関連付けられていないかもしれません）：{ex.Message}");
        }
    }
}
