using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Kuroko.Authoring.Export;
using Kuroko.Authoring.Project;

namespace Kuroko.Authoring.Editors;

// メニュー 1 つの編集。上：メニューの設定（識別子・背景・BGM・最初のボタン・選択を覚えるか・時間切れ）。
// 中：プレビュー（背景の上にボタンを置く。ボタンを掴んで移動、右下の角を掴んで大きさを変える）。
// 下：選んだボタンの設定（識別子・位置と大きさ・状態ごとの画像・上下左右の移動先・押したときの動作）。
public sealed class MenuEditor : UserControl
{
    private readonly AuthoringProject _project;
    private readonly AuthoringMenu _menu;
    private readonly Func<string?> _ffmpegPath;
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly Image _bgImage = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _bgNote = new() { Foreground = Brushes.White, FontSize = 40, Margin = new Thickness(40) };
    private readonly StackPanel _buttonPanel = new() { Spacing = 6 };
    private readonly ComboBox _buttonList = new() { MinWidth = 200 };
    private readonly ComboBox _defaultButton = new() { MinWidth = 160 };
    private readonly Dictionary<string, Bitmap?> _bitmaps = new(StringComparer.Ordinal);
    private int _selected = -1;
    private bool _loading;

    // ボタンのドラッグ（移動・大きさ変更）。
    private enum DragMode { None, Move, Resize }
    private DragMode _drag;
    private Point _dragStart;
    private KurokoTinyRect _dragRect = new();

    public event EventHandler? MenuRenamed;

    public MenuEditor(AuthoringProject project, AuthoringMenu menu, Func<string?> ffmpegPath)
    {
        _project = project;
        _menu = menu;
        _ffmpegPath = ffmpegPath;

        var root = new StackPanel { Spacing = 6 };
        root.Children.Add(BuildMenuSettings());
        root.Children.Add(NavEditor.Heading("プレビュー（ボタンを掴んで移動、右下の角を掴んで大きさを変える）"));
        root.Children.Add(BuildPreview());
        root.Children.Add(BuildButtonToolbar());
        root.Children.Add(_buttonPanel);
        Content = new ScrollViewer { Content = root };

        LoadBackground();
        _selected = menu.Menu.Buttons.Count > 0 ? 0 : -1;
        RefreshButtons();
    }

    private KurokoTinyMenu M => _menu.Menu;

    // ── メニューの設定 ──────────────────────────────────────────────
    private Control BuildMenuSettings()
    {
        var p = new StackPanel { Spacing = 4 };
        p.Children.Add(NavEditor.Heading("メニュー"));

        var id = new TextBox { Text = M.Id, Width = 160 };
        id.LostFocus += (_, _) =>
        {
            string v = (id.Text ?? "").Trim();
            if (v == M.Id) return;
            if (!KurokoTinyPackage.IsValidMenuId(v) || _project.Menus.Any(x => x.Menu.Id == v)) { id.Text = M.Id; return; }
            _project.RenameMenu(M.Id, v);
            MenuRenamed?.Invoke(this, EventArgs.Empty);
        };
        p.Children.Add(TitleEditor.Labeled("識別子（英数字・-・_）", id));

        var cw = new NumericUpDown { Value = M.Canvas.Width, Minimum = 16, Maximum = 7680, Increment = 1, FormatString = "0", Width = 130 };
        var ch = new NumericUpDown { Value = M.Canvas.Height, Minimum = 16, Maximum = 4320, Increment = 1, FormatString = "0", Width = 130 };
        cw.ValueChanged += (_, _) => { M.Canvas.Width = (int)(cw.Value ?? 1920); RefreshCanvasSize(); };
        ch.ValueChanged += (_, _) => { M.Canvas.Height = (int)(ch.Value ?? 1080); RefreshCanvasSize(); };
        var size = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        size.Children.Add(cw); size.Children.Add(new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center }); size.Children.Add(ch);
        p.Children.Add(TitleEditor.Labeled("座標の大きさ（canvas）", size));

        var bgName = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Width = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        var bgPick = new Button { Content = "変更…" };
        var bgLoop = new CheckBox { Content = "繰り返す" };
        void ShowBg()
        {
            bool video = !string.IsNullOrEmpty(M.Background.Video);
            string? name = video ? M.Background.Video : M.Background.Image;
            bgName.Text = name is not null && _menu.Assets.TryGetValue(name, out var src) ? System.IO.Path.GetFileName(src) : "（なし）";
            bgLoop.IsVisible = video;
            bgLoop.IsChecked = M.Background.Loop;
        }
        bgPick.Click += async (_, _) =>
        {
            var path = await PickFile("背景（画像か動画）を選ぶ", new FilePickerFileType("背景") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.mkv", "*.mp4", "*.mov", "*.webm" } });
            if (path is null) return;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            bool image = ext is ".png" or ".jpg" or ".jpeg";
            M.Background = image ? new KurokoTinyBackground { Image = "background.png" } : new KurokoTinyBackground { Video = "background.mkv", Loop = true };
            _menu.Assets[image ? "background.png" : "background.mkv"] = path;
            PruneAssets();
            ShowBg();
            LoadBackground();
        };
        bgLoop.IsCheckedChanged += (_, _) => { if (!_loading) M.Background.Loop = bgLoop.IsChecked == true; };
        var bgRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        bgRow.Children.Add(bgName); bgRow.Children.Add(bgPick); bgRow.Children.Add(bgLoop);
        p.Children.Add(TitleEditor.Labeled("背景", bgRow));

        var bgmName = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Width = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        var bgmPick = new Button { Content = "選ぶ…" };
        var bgmClear = new Button { Content = "外す" };
        var bgmLoop = new CheckBox { Content = "繰り返す" };
        void ShowBgm()
        {
            bgmName.Text = M.Audio is { } a && _menu.Assets.TryGetValue(a.File, out var src) ? System.IO.Path.GetFileName(src) : "（なし）";
            bgmLoop.IsChecked = M.Audio?.Loop == true;
            bgmLoop.IsEnabled = bgmClear.IsEnabled = M.Audio is not null;
        }
        bgmPick.Click += async (_, _) =>
        {
            var path = await PickFile("BGM を選ぶ", new FilePickerFileType("音声") { Patterns = new[] { "*.flac", "*.wav", "*.mka", "*.m4a", "*.mp3", "*.opus", "*.ogg", "*.aac" } });
            if (path is null) return;
            M.Audio = new KurokoTinyAudio { File = "bgm.mka", Loop = M.Audio?.Loop ?? true };
            _menu.Assets["bgm.mka"] = path;
            ShowBgm();
        };
        bgmClear.Click += (_, _) => { M.Audio = null; PruneAssets(); ShowBgm(); };
        bgmLoop.IsCheckedChanged += (_, _) => { if (!_loading && M.Audio is not null) M.Audio.Loop = bgmLoop.IsChecked == true; };
        var bgmRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        bgmRow.Children.Add(bgmName); bgmRow.Children.Add(bgmPick); bgmRow.Children.Add(bgmClear); bgmRow.Children.Add(bgmLoop);
        p.Children.Add(TitleEditor.Labeled("BGM（背景が画像のとき）", bgmRow));

        _defaultButton.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            M.DefaultButton = _defaultButton.SelectedIndex <= 0 ? null : _defaultButton.SelectedItem as string;
        };
        p.Children.Add(TitleEditor.Labeled("最初に選ぶボタン", _defaultButton));

        var remember = new CheckBox { Content = "このメニューへ戻ったとき、前に選んでいたボタンを選び直す", IsChecked = M.RememberSelection };
        remember.IsCheckedChanged += (_, _) => M.RememberSelection = remember.IsChecked == true;
        p.Children.Add(remember);

        var toOn = new CheckBox { Content = "操作が無いまま", IsChecked = M.Timeout is not null };
        var toSec = new NumericUpDown { Value = (decimal)((M.Timeout?.AfterMs ?? 60000) / 1000), Minimum = 1, Maximum = 3600, Increment = 1, FormatString = "0", Width = 120 };
        var toAction = new ActionEditor(_project, M.Timeout?.Action);
        void ApplyTimeout()
        {
            if (_loading) return;
            M.Timeout = toOn.IsChecked == true && toAction.Action is { } a
                ? new KurokoTinyTimeout { AfterMs = (double)(toSec.Value ?? 60) * 1000, Action = a } : null;
        }
        toOn.IsCheckedChanged += (_, _) => ApplyTimeout();
        toSec.ValueChanged += (_, _) => ApplyTimeout();
        toAction.Changed += (_, _) => ApplyTimeout();
        var toRow = new WrapPanel();
        toRow.Children.Add(toOn); toRow.Children.Add(toSec);
        toRow.Children.Add(new TextBlock { Text = "秒たったら", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) });
        toRow.Children.Add(toAction);
        p.Children.Add(toRow);

        _loading = true;
        ShowBg();
        ShowBgm();
        _loading = false;
        return p;
    }

    // ── プレビュー ──────────────────────────────────────────────────
    private Control BuildPreview()
    {
        _canvas.Background = Brushes.Black;
        _canvas.PointerPressed += OnCanvasPressed;
        _canvas.PointerMoved += OnCanvasMoved;
        _canvas.PointerReleased += OnCanvasReleased;
        RefreshCanvasSize();
        return new Border
        {
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Height = 380,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas },
        };
    }

    private void RefreshCanvasSize()
    {
        _canvas.Width = Math.Max(16, M.Canvas.Width);
        _canvas.Height = Math.Max(16, M.Canvas.Height);
        RefreshPreview();
    }

    // 背景：画像はそのまま、動画は ffmpeg で最初の絵を取り出して出す。
    private async void LoadBackground()
    {
        _bgImage.Source = null;
        _bgNote.Text = "";
        string? name = M.Background.Image ?? M.Background.Video;
        if (name is null || !_menu.Assets.TryGetValue(name, out var src) || !File.Exists(src)) { _bgNote.Text = "背景がありません"; RefreshPreview(); return; }
        string? png = src;
        if (M.Background.Video is not null)
        {
            _bgNote.Text = "動画の最初の絵を取り出しています…";
            RefreshPreview();
            string? ffmpeg = _ffmpegPath(); // 画面の欄は UI スレッドでしか読めないので、先に読んでおく
            png = await Task.Run(() => ExtractFirstFrame(src, ffmpeg));
            if (png is null) { _bgNote.Text = "動画の背景（プレビューには ffmpeg が要る）"; RefreshPreview(); return; }
            _bgNote.Text = "";
        }
        try { _bgImage.Source = new Bitmap(png); }
        catch { _bgNote.Text = "背景を読めません"; }
        RefreshPreview();
    }

    private static string? ExtractFirstFrame(string video, string? ffmpegPath)
    {
        try
        {
            var tools = FfmpegTools.Locate(ffmpegPath);
            string png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kuroko_preview_{Guid.NewGuid():N}.png");
            tools.RunFfmpeg(new[] { "-i", video, "-frames:v", "1", "-update", "1", png }, 0, null, CancellationToken.None);
            return File.Exists(png) ? png : null;
        }
        catch { return null; }
    }

    private Bitmap? AssetBitmap(string? name)
    {
        if (string.IsNullOrEmpty(name) || !_menu.Assets.TryGetValue(name, out var src)) return null;
        if (_bitmaps.TryGetValue(src, out var b)) return b;
        try { b = File.Exists(src) ? new Bitmap(src) : null; } catch { b = null; }
        _bitmaps[src] = b;
        return b;
    }

    private void RefreshPreview()
    {
        _canvas.Children.Clear();
        _bgImage.Width = _canvas.Width; _bgImage.Height = _canvas.Height;
        _canvas.Children.Add(_bgImage);
        _canvas.Children.Add(_bgNote);
        double line = Math.Max(2, _canvas.Width / 400);
        for (int i = 0; i < M.Buttons.Count; i++)
        {
            var b = M.Buttons[i];
            bool sel = i == _selected;
            var img = AssetBitmap(sel ? b.Images.Selected ?? b.Images.Normal : b.Images.Normal);
            var box = new Border
            {
                Width = Math.Max(1, b.Rect.W), Height = Math.Max(1, b.Rect.H),
                BorderBrush = sel ? Brushes.DeepSkyBlue : new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                BorderThickness = new Thickness(sel ? line * 2 : line),
                Background = img is null ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) : null,
                Child = img is null
                    ? new TextBlock { Text = b.Id, Foreground = Brushes.White, FontSize = Math.Max(12, b.Rect.H * 0.35), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                    : new Image { Source = img, Stretch = Stretch.Fill },
            };
            Canvas.SetLeft(box, b.Rect.X); Canvas.SetTop(box, b.Rect.Y);
            _canvas.Children.Add(box);
            if (sel)
            {
                double hs = HandleSize;
                var handle = new Rectangle { Width = hs, Height = hs, Fill = Brushes.DeepSkyBlue };
                Canvas.SetLeft(handle, b.Rect.X + b.Rect.W - hs); Canvas.SetTop(handle, b.Rect.Y + b.Rect.H - hs);
                _canvas.Children.Add(handle);
            }
        }
    }

    private double HandleSize => Math.Max(10, M.Canvas.Width * 0.015);

    private void OnCanvasPressed(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        int hit = -1;
        for (int i = M.Buttons.Count - 1; i >= 0; i--)
        {
            var r = M.Buttons[i].Rect;
            if (p.X >= r.X && p.X < r.X + r.W && p.Y >= r.Y && p.Y < r.Y + r.H) { hit = i; break; }
        }
        if (hit < 0) return;
        if (hit != _selected) { _selected = hit; RefreshButtons(); }
        var rect = M.Buttons[hit].Rect;
        bool corner = p.X >= rect.X + rect.W - HandleSize * 1.5 && p.Y >= rect.Y + rect.H - HandleSize * 1.5;
        _drag = corner ? DragMode.Resize : DragMode.Move;
        _dragStart = p;
        _dragRect = new KurokoTinyRect { X = rect.X, Y = rect.Y, W = rect.W, H = rect.H };
        e.Pointer.Capture(_canvas);
        e.Handled = true;
    }

    private void OnCanvasMoved(object? sender, PointerEventArgs e)
    {
        if (_drag == DragMode.None || _selected < 0) return;
        var p = e.GetPosition(_canvas);
        int dx = (int)Math.Round(p.X - _dragStart.X), dy = (int)Math.Round(p.Y - _dragStart.Y);
        var r = M.Buttons[_selected].Rect;
        int cw = M.Canvas.Width, ch = M.Canvas.Height;
        if (_drag == DragMode.Move)
        {
            r.X = Math.Clamp(_dragRect.X + dx, 0, Math.Max(0, cw - r.W));
            r.Y = Math.Clamp(_dragRect.Y + dy, 0, Math.Max(0, ch - r.H));
        }
        else
        {
            r.W = Math.Clamp(_dragRect.W + dx, 8, cw - r.X);
            r.H = Math.Clamp(_dragRect.H + dy, 8, ch - r.Y);
        }
        RefreshPreview();
        _rectChanged?.Invoke();
    }

    private void OnCanvasReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        e.Pointer.Capture(null);
    }

    // ── ボタン ────────────────────────────────────────────────────
    private Action? _rectChanged; // 選んだボタンの位置・大きさの欄を、ドラッグに合わせて書き直す

    private Control BuildButtonToolbar()
    {
        _buttonList.SelectionChanged += (_, _) =>
        {
            if (_loading || _buttonList.SelectedIndex == _selected) return;
            _selected = _buttonList.SelectedIndex;
            RefreshButtons();
        };
        var add = new Button { Content = "ボタンを追加" };
        add.Click += (_, _) =>
        {
            int n = 1;
            while (M.Buttons.Any(b => b.Id == $"button{n}")) n++;
            int w = Math.Min(400, M.Canvas.Width / 3), h = Math.Max(16, M.Canvas.Height / 12);
            M.Buttons.Add(new KurokoTinyButton
            {
                Id = $"button{n}",
                Rect = new KurokoTinyRect { X = (M.Canvas.Width - w) / 2, Y = Math.Min(M.Canvas.Height - h, M.Canvas.Height / 2 + (M.Buttons.Count * h * 6 / 5)), W = w, H = h },
                Action = _project.Titles.Count > 0 ? KurokoTinyAction.PlayTitleAction(_project.Titles[0].Id) : new KurokoTinyAction(),
            });
            _selected = M.Buttons.Count - 1;
            RefreshButtons();
        };
        var del = new Button { Content = "選んだボタンを削除" };
        del.Click += (_, _) =>
        {
            if (_selected < 0 || _selected >= M.Buttons.Count) return;
            string id = M.Buttons[_selected].Id;
            M.Buttons.RemoveAt(_selected);
            foreach (var b in M.Buttons)
            {
                if (b.Nav.Up == id) b.Nav.Up = null;
                if (b.Nav.Down == id) b.Nav.Down = null;
                if (b.Nav.Left == id) b.Nav.Left = null;
                if (b.Nav.Right == id) b.Nav.Right = null;
            }
            if (M.DefaultButton == id) M.DefaultButton = null;
            PruneAssets();
            _selected = Math.Min(_selected, M.Buttons.Count - 1);
            RefreshButtons();
        };
        var autoNav = new Button { Content = "移動先を位置から決める" };
        ToolTip.SetTip(autoNav, "全ボタンの上下左右の移動先を、画面上でその方向にいちばん近いボタンにする");
        autoNav.Click += (_, _) => { AutoNavigation(); RefreshButtons(); };
        var row = new WrapPanel();
        foreach (var c in new Control[] { new TextBlock { Text = "ボタン", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, _buttonList, add, del, autoNav })
        {
            c.Margin = new Thickness(0, 8, 8, 0);
            row.Children.Add(c);
        }
        return row;
    }

    // 一覧・既定のボタン・プレビュー・選んだボタンの欄を作り直す。
    private void RefreshButtons()
    {
        _loading = true;
        var ids = M.Buttons.Select(b => b.Id).ToList();
        _buttonList.ItemsSource = ids;
        _buttonList.SelectedIndex = _selected;
        var defs = new List<string> { "（先頭のボタン）" };
        defs.AddRange(ids);
        _defaultButton.ItemsSource = defs;
        _defaultButton.SelectedItem = M.DefaultButton is { } d && ids.Contains(d) ? d : defs[0];
        _loading = false;
        RefreshPreview();
        BuildButtonPanel();
    }

    private void BuildButtonPanel()
    {
        _buttonPanel.Children.Clear();
        _rectChanged = null;
        if (_selected < 0 || _selected >= M.Buttons.Count)
        {
            _buttonPanel.Children.Add(NavEditor.Note("ボタンがありません。「ボタンを追加」で足す。"));
            return;
        }
        var b = M.Buttons[_selected];

        var id = new TextBox { Text = b.Id, Width = 160 };
        id.LostFocus += (_, _) =>
        {
            string v = (id.Text ?? "").Trim();
            if (v == b.Id) return;
            if (v.Length == 0 || M.Buttons.Any(x => x.Id == v)) { id.Text = b.Id; return; }
            string old = b.Id;
            b.Id = v;
            foreach (var x in M.Buttons)
            {
                if (x.Nav.Up == old) x.Nav.Up = v;
                if (x.Nav.Down == old) x.Nav.Down = v;
                if (x.Nav.Left == old) x.Nav.Left = v;
                if (x.Nav.Right == old) x.Nav.Right = v;
            }
            if (M.DefaultButton == old) M.DefaultButton = v;
            RefreshButtons();
        };
        _buttonPanel.Children.Add(TitleEditor.Labeled("識別子", id));

        // 位置と大きさ（canvas の座標）。
        NumericUpDown Num(int v) => new() { Value = v, Minimum = 0, Maximum = 10000, Increment = 1, FormatString = "0", Width = 120 };
        var x = Num(b.Rect.X); var y = Num(b.Rect.Y); var w = Num(b.Rect.W); var h = Num(b.Rect.H);
        bool syncing = false;
        void FromBoxes()
        {
            if (syncing) return;
            b.Rect.X = (int)(x.Value ?? 0); b.Rect.Y = (int)(y.Value ?? 0);
            b.Rect.W = Math.Max(1, (int)(w.Value ?? 1)); b.Rect.H = Math.Max(1, (int)(h.Value ?? 1));
            RefreshPreview();
        }
        foreach (var n in new[] { x, y, w, h }) n.ValueChanged += (_, _) => FromBoxes();
        _rectChanged = () =>
        {
            syncing = true;
            x.Value = b.Rect.X; y.Value = b.Rect.Y; w.Value = b.Rect.W; h.Value = b.Rect.H;
            syncing = false;
        };
        var fit = new Button { Content = "画像の大きさにする" };
        fit.Click += (_, _) =>
        {
            if (AssetBitmap(b.Images.Normal ?? b.Images.Selected) is not { } bmp) return;
            b.Rect.W = Math.Min(bmp.PixelSize.Width, M.Canvas.Width - b.Rect.X);
            b.Rect.H = Math.Min(bmp.PixelSize.Height, M.Canvas.Height - b.Rect.Y);
            _rectChanged?.Invoke();
            RefreshPreview();
        };
        var rectRow = new WrapPanel();
        foreach (var (label, c) in new (string, Control)[] { ("x", x), ("y", y), ("幅", w), ("高さ", h) })
        {
            rectRow.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            c.Margin = new Thickness(0, 0, 10, 0);
            rectRow.Children.Add(c);
        }
        rectRow.Children.Add(fit);
        _buttonPanel.Children.Add(TitleEditor.Labeled("位置と大きさ", rectRow));

        // 状態ごとの画像。
        _buttonPanel.Children.Add(ImageRow("通常の画像", b, "n", () => b.Images.Normal, v => b.Images.Normal = v));
        _buttonPanel.Children.Add(ImageRow("選ばれているとき", b, "s", () => b.Images.Selected, v => b.Images.Selected = v));
        _buttonPanel.Children.Add(ImageRow("押した瞬間（任意）", b, "a", () => b.Images.Activated, v => b.Images.Activated = v));
        _buttonPanel.Children.Add(NavEditor.Note("画像は PNG・透明度あり。文字は画像に入れる。無い状態は何も重ねない（背景に描いた図柄の上に、選択時の枠だけ重ねる作り方もできる）。"));

        // 上下左右の移動先。
        var navRow = new WrapPanel();
        foreach (var (label, get, set) in new (string, Func<string?>, Action<string?>)[]
                 {
                     ("上", () => b.Nav.Up, v => b.Nav.Up = v), ("下", () => b.Nav.Down, v => b.Nav.Down = v),
                     ("左", () => b.Nav.Left, v => b.Nav.Left = v), ("右", () => b.Nav.Right, v => b.Nav.Right = v),
                 })
        {
            var items = new List<string> { "（動かない）" };
            items.AddRange(M.Buttons.Where(o => !ReferenceEquals(o, b)).Select(o => o.Id));
            var combo = new ComboBox { ItemsSource = items, MinWidth = 130, Margin = new Thickness(0, 0, 10, 0) };
            combo.SelectedItem = get() is { } cur && items.Contains(cur) ? cur : items[0];
            combo.SelectionChanged += (_, _) => set(combo.SelectedIndex <= 0 ? null : combo.SelectedItem as string);
            navRow.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            navRow.Children.Add(combo);
        }
        _buttonPanel.Children.Add(TitleEditor.Labeled("移動先", navRow));

        var act = new ActionEditor(_project, b.Action);
        act.Changed += (_, _) => b.Action = act.Action ?? new KurokoTinyAction();
        _buttonPanel.Children.Add(TitleEditor.Labeled("押したとき", act));
    }

    private Control ImageRow(string label, KurokoTinyButton b, string state, Func<string?> get, Action<string?> set)
    {
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Width = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        void Show() => name.Text = get() is { } n && _menu.Assets.TryGetValue(n, out var src) ? System.IO.Path.GetFileName(src) : "（なし）";
        var pick = new Button { Content = "選ぶ…" };
        pick.Click += async (_, _) =>
        {
            var path = await PickFile($"{label}を選ぶ", new FilePickerFileType("画像") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg" } });
            if (path is null) return;
            string asset = get() ?? UniqueAssetName($"{b.Id}_{state}.png");
            _menu.Assets[asset] = path;
            set(asset);
            Show();
            RefreshPreview();
        };
        var clear = new Button { Content = "外す" };
        clear.Click += (_, _) => { set(null); PruneAssets(); Show(); RefreshPreview(); };
        Show();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(name); row.Children.Add(pick); row.Children.Add(clear);
        return TitleEditor.Labeled(label, row);
    }

    private string UniqueAssetName(string name)
    {
        if (!_menu.Assets.ContainsKey(name)) return name;
        string stem = System.IO.Path.GetFileNameWithoutExtension(name), ext = System.IO.Path.GetExtension(name);
        for (int i = 2; ; i++)
            if (!_menu.Assets.ContainsKey($"{stem}{i}{ext}")) return $"{stem}{i}{ext}";
    }

    // どこからも指されなくなった素材を消す。
    private void PruneAssets()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Use(string? n) { if (!string.IsNullOrEmpty(n)) used.Add(n); }
        Use(M.Background.Image); Use(M.Background.Video); Use(M.Audio?.File);
        foreach (var b in M.Buttons) { Use(b.Images.Normal); Use(b.Images.Selected); Use(b.Images.Activated); }
        foreach (var k in _menu.Assets.Keys.Where(k => !used.Contains(k)).ToList()) _menu.Assets.Remove(k);
    }

    // 上下左右の移動先を、ボタンの中心どうしの位置から決める（その方向にあるボタンのうち、主方向の距離＋横ずれ×2 が最小のもの）。
    private void AutoNavigation()
    {
        static (double X, double Y) C(KurokoTinyButton b) => (b.Rect.X + b.Rect.W / 2.0, b.Rect.Y + b.Rect.H / 2.0);
        foreach (var b in M.Buttons)
        {
            var (bx, by) = C(b);
            string? Best(Func<double, double, bool> inDir, Func<double, double, double> score)
                => M.Buttons.Where(o => !ReferenceEquals(o, b))
                    .Select(o => (o, d: (dx: C(o).X - bx, dy: C(o).Y - by)))
                    .Where(t => inDir(t.d.dx, t.d.dy))
                    .OrderBy(t => score(t.d.dx, t.d.dy))
                    .Select(t => t.o.Id).FirstOrDefault();
            b.Nav.Up = Best((dx, dy) => dy < 0 && Math.Abs(dy) >= Math.Abs(dx) * 0.5, (dx, dy) => -dy + Math.Abs(dx) * 2);
            b.Nav.Down = Best((dx, dy) => dy > 0 && Math.Abs(dy) >= Math.Abs(dx) * 0.5, (dx, dy) => dy + Math.Abs(dx) * 2);
            b.Nav.Left = Best((dx, dy) => dx < 0 && Math.Abs(dx) >= Math.Abs(dy) * 0.5, (dx, dy) => -dx + Math.Abs(dy) * 2);
            b.Nav.Right = Best((dx, dy) => dx > 0 && Math.Abs(dx) >= Math.Abs(dy) * 0.5, (dx, dy) => dx + Math.Abs(dy) * 2);
        }
    }

    private async Task<string?> PickFile(string title, FilePickerFileType type)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, FileTypeFilter = new[] { type } });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
}
