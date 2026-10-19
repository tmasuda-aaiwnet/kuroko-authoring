using System.Text.RegularExpressions;

namespace Kuroko;

// kuroko-tiny の krk の中身（ナビゲーションとメニュー）をまとめて持つ。読み込み（Read）と、
// 書く前・読んだ後の検査（Validate）を受け持つ。タイトルの中身（title.json・セグメント）は none と同じなので扱わない。
public sealed partial class KurokoTinyPackage
{
    public KurokoNoneDisc Disc { get; set; } = new();
    public KurokoTinyNav Nav { get; set; } = new();
    // menuId → menu.json の中身。
    public Dictionary<string, KurokoTinyMenu> Menus { get; set; } = new(StringComparer.Ordinal);

    // krk（ZIP）から読む。マニフェストの controller が kuroko-tiny でなければ例外。
    public static KurokoTinyPackage Read(KurokoContainerReader container)
    {
        var manifest = ReadJson<KurokoManifest>(container, KurokoLayout.ManifestFile);
        if (!string.Equals(manifest.Controller, KurokoLayout.ControllerKurokoTiny, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"controller が {KurokoLayout.ControllerKurokoTiny} ではありません（{manifest.Controller}）。");
        var pkg = new KurokoTinyPackage
        {
            Disc = ReadJson<KurokoNoneDisc>(container, KurokoLayout.DiscJson),
            Nav = ReadJson<KurokoTinyNav>(container, KurokoLayout.TinyNavJson),
        };
        foreach (var id in pkg.Nav.Menus)
        {
            if (!IsValidMenuId(id) || pkg.Menus.ContainsKey(id)) continue;
            pkg.Menus[id] = ReadJson<KurokoTinyMenu>(container, KurokoLayout.TinyMenuJson(id));
        }
        return pkg;
    }

    private static T ReadJson<T>(KurokoContainerReader container, string path)
    {
        var e = container.FindEntry(path) ?? throw new InvalidDataException($"{path} がありません。");
        return KurokoJson.Deserialize<T>(container.ReadAllBytes(e)) ?? throw new InvalidDataException($"{path} を読めません。");
    }

    // メニューの識別子はフォルダ名になるので、英数字・ハイフン・下線だけにする。
    public static bool IsValidMenuId(string? id) => id is not null && MenuIdPattern().IsMatch(id);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex MenuIdPattern();

    // 参照の食い違いを調べる。fileExists：メニューのフォルダ内のファイル（menuId, 相対名）があるか。
    // null なら、ファイルの有無は調べない。戻り値は問題の一覧（空＝問題なし）。
    public List<string> Validate(Func<string, string, bool>? fileExists = null)
    {
        var problems = new List<string>();
        var titleIds = new HashSet<int>(Disc.Titles.Select(t => t.Id));
        var menuIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in Nav.Menus)
        {
            if (!IsValidMenuId(id)) problems.Add($"nav.json：メニューの識別子「{id}」に使えない文字があります（英数字・-・_ だけ）。");
            else if (!menuIds.Add(id)) problems.Add($"nav.json：メニュー「{id}」が重複しています。");
            else if (!Menus.ContainsKey(id)) problems.Add($"nav.json：メニュー「{id}」の menu.json がありません。");
        }

        void CheckMenuRef(string where, string? id)
        {
            if (id is not null && !menuIds.Contains(id)) problems.Add($"{where}：メニュー「{id}」がありません。");
        }

        void CheckAction(string where, KurokoTinyAction? action)
        {
            if (action is null) return;
            if (action.KeyCount != 1)
            {
                problems.Add($"{where}：動作はキーを 1 つだけ持ちます（{action.KeyCount} 個あります）。");
                return;
            }
            switch (action.Kind)
            {
                case KurokoTinyActionKind.PlayTitle:
                    if (!titleIds.Contains(action.PlayTitle!.Title)) problems.Add($"{where}：タイトル {action.PlayTitle.Title} がありません。");
                    if (action.PlayTitle.Chapter is < 1) problems.Add($"{where}：章の番号は 1 から始まります。");
                    break;
                case KurokoTinyActionKind.ShowMenu:
                    CheckMenuRef(where, action.ShowMenu);
                    break;
                case KurokoTinyActionKind.SetAudio:
                    if (action.SetAudio < 1) problems.Add($"{where}：音声の番号は 1 から始まります。");
                    break;
                case KurokoTinyActionKind.SetSubtitle:
                    if (action.SetSubtitle < 0) problems.Add($"{where}：字幕の番号は 0（なし）か 1 以上です。");
                    break;
            }
        }

        CheckAction("nav.json の firstPlay", Nav.FirstPlay);
        CheckMenuRef("nav.json の keys.titleMenu", Nav.Keys?.TitleMenu);
        CheckMenuRef("nav.json の keys.menu", Nav.Keys?.Menu);
        foreach (var t in Nav.Titles)
        {
            if (!titleIds.Contains(t.Title)) problems.Add($"nav.json の titles：タイトル {t.Title} がありません。");
            CheckAction($"nav.json のタイトル {t.Title} の onEnd", t.OnEnd);
        }

        foreach (var (id, menu) in Menus)
        {
            string at = $"メニュー「{id}」";
            if (menu.Id != id) problems.Add($"{at}：menu.json の id が「{menu.Id}」になっています。");
            if (menu.Canvas.Width <= 0 || menu.Canvas.Height <= 0) problems.Add($"{at}：canvas の大きさが正しくありません。");
            bool hasImage = !string.IsNullOrEmpty(menu.Background.Image), hasVideo = !string.IsNullOrEmpty(menu.Background.Video);
            if (hasImage == hasVideo) problems.Add($"{at}：背景は image か video のどちらか一方を持ちます。");

            void CheckFile(string what, string? name)
            {
                if (string.IsNullOrEmpty(name)) return;
                if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) problems.Add($"{at}：{what}「{name}」はフォルダを含められません。");
                else if (fileExists is not null && !fileExists(id, name)) problems.Add($"{at}：{what}「{name}」がありません。");
            }

            // 画像は PNG だけ（どの再生器でも復号できるように。JPEG は書き手が PNG にして入れる）。
            void CheckPng(string what, string? name)
            {
                if (!string.IsNullOrEmpty(name) && !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{at}：{what}「{name}」は PNG にしてください。");
            }

            CheckPng("背景の画像", menu.Background.Image);
            CheckFile("背景の画像", menu.Background.Image);
            CheckFile("背景の動画", menu.Background.Video);
            if (menu.Audio is not null)
            {
                if (string.IsNullOrEmpty(menu.Audio.File)) problems.Add($"{at}：audio の file がありません。");
                CheckFile("背景の音声", menu.Audio.File);
            }
            if (menu.Timeout is not null)
            {
                if (menu.Timeout.AfterMs <= 0) problems.Add($"{at}：timeout の afterMs は正の値にします。");
                CheckAction($"{at}の timeout", menu.Timeout.Action);
            }

            var buttonIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in menu.Buttons)
                if (string.IsNullOrEmpty(b.Id) || !buttonIds.Add(b.Id)) problems.Add($"{at}：ボタンの id「{b.Id}」が空か重複しています。");
            if (menu.DefaultButton is not null && !buttonIds.Contains(menu.DefaultButton))
                problems.Add($"{at}：defaultButton「{menu.DefaultButton}」がありません。");

            foreach (var b in menu.Buttons)
            {
                string bat = $"{at}のボタン「{b.Id}」";
                var r = b.Rect;
                if (r.W <= 0 || r.H <= 0 || r.X < 0 || r.Y < 0 || r.X + r.W > menu.Canvas.Width || r.Y + r.H > menu.Canvas.Height)
                    problems.Add($"{bat}：rect が canvas の外にはみ出しているか、大きさが 0 です。");
                CheckPng("ボタンの画像", b.Images.Normal);
                CheckPng("ボタンの画像", b.Images.Selected);
                CheckPng("ボタンの画像", b.Images.Activated);
                CheckFile("ボタンの画像", b.Images.Normal);
                CheckFile("ボタンの画像", b.Images.Selected);
                CheckFile("ボタンの画像", b.Images.Activated);
                foreach (var (dir, to) in new[] { ("up", b.Nav.Up), ("down", b.Nav.Down), ("left", b.Nav.Left), ("right", b.Nav.Right) })
                    if (to is not null && !buttonIds.Contains(to)) problems.Add($"{bat}：nav.{dir} の「{to}」がありません。");
                CheckAction(bat, b.Action);
            }
        }
        return problems;
    }
}
