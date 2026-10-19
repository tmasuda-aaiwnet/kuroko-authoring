using System.Text.Json.Serialization;

namespace Kuroko;

// controller="kuroko-tiny" の JSON モデル。仕様は kuroko-spec（spec/kuroko.ja.md）第III部。
//
// メニュー・ボタン・動作を JSON で宣言的に書くコントローラ。命令の列（VM）も状態の変数も持たない。
// タイトルの再生は none と同じ（CONTROL/disc.json・VIDEO/Tnn/title.json・SEGMENTS/ は KurokoNoneModel のまま使う）。
// このファイルが足すのは CONTROL/nav.json と MENU/<menuId>/menu.json だけ。

// ── CONTROL/nav.json ─────────────────────────────────────────────────────
public sealed class KurokoTinyNav
{
    // ファイルを開いたときの動作。null＝先頭のタイトルを再生する。
    public KurokoTinyAction? FirstPlay { get; set; }
    // メニューの識別子の一覧（MENU/<menuId>/）。
    public List<string> Menus { get; set; } = new();
    // 再生器の「タイトルメニュー」「メニュー」の操作で出すメニュー。
    public KurokoTinyKeys? Keys { get; set; }
    // タイトルごとの設定（終わったときの動作）。載っていないタイトルは既定の動作。
    public List<KurokoTinyTitle> Titles { get; set; } = new();
}

public sealed class KurokoTinyKeys
{
    public string? TitleMenu { get; set; }
    public string? Menu { get; set; }
}

public sealed class KurokoTinyTitle
{
    public int Title { get; set; }                 // disc.json の titles[].id
    // タイトルが終わったときの動作。null＝firstPlay のメニュー（無ければ再生を終える）。
    public KurokoTinyAction? OnEnd { get; set; }
}

// ── MENU/<menuId>/menu.json ──────────────────────────────────────────────
public sealed class KurokoTinyMenu
{
    public string Id { get; set; } = "";
    // 座標系。再生器は canvas 全体を映像の表示範囲へ拡大縮小して重ねる。
    public KurokoTinySize Canvas { get; set; } = new();
    public KurokoTinyBackground Background { get; set; } = new();
    // 背景の音声（任意。静止画の背景に BGM を付けるとき）。
    public KurokoTinyAudio? Audio { get; set; }
    // 最初に選ばれているボタン。null＝先頭のボタン。
    public string? DefaultButton { get; set; }
    // このメニューに戻ったとき、前に選んでいたボタンを選び直すか。
    public bool RememberSelection { get; set; }
    // 操作が無いまま一定時間たったときの動作（任意）。
    public KurokoTinyTimeout? Timeout { get; set; }
    public List<KurokoTinyButton> Buttons { get; set; } = new();
}

public sealed class KurokoTinySize
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
}

// 背景。image（静止画。PNG）か video（mkv。映像＋BGM の音声を含められる）のどちらか一方。
// ファイル名は MENU/<menuId>/ からの相対名。
public sealed class KurokoTinyBackground
{
    public string? Image { get; set; }
    public string? Video { get; set; }
    public bool Loop { get; set; }                 // 動画の背景を繰り返すか
}

public sealed class KurokoTinyAudio
{
    public string File { get; set; } = "";          // MENU/<menuId>/ からの相対名（.mka など）
    public bool Loop { get; set; }
}

public sealed class KurokoTinyTimeout
{
    public double AfterMs { get; set; }
    public KurokoTinyAction Action { get; set; } = new();
}

public sealed class KurokoTinyButton
{
    public string Id { get; set; } = "";
    // ボタンの範囲（canvas の座標）。マウス・タッチの当たり判定に使う。画像もこの範囲へ描く。
    public KurokoTinyRect Rect { get; set; } = new();
    public KurokoTinyButtonImages Images { get; set; } = new();
    public KurokoTinyButtonNav Nav { get; set; } = new();
    public KurokoTinyAction Action { get; set; } = new();
}

public sealed class KurokoTinyRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

// 状態ごとの画像（PNG・透明度あり。MENU/<menuId>/ からの相対名）。無い状態は何も重ねない。
public sealed class KurokoTinyButtonImages
{
    public string? Normal { get; set; }
    public string? Selected { get; set; }
    public string? Activated { get; set; }        // 押した瞬間に短く出す
}

// 上下左右の移動先のボタン（同じメニューのボタンの id）。無い方向は動かない。
public sealed class KurokoTinyButtonNav
{
    public string? Up { get; set; }
    public string? Down { get; set; }
    public string? Left { get; set; }
    public string? Right { get; set; }
}

// ── 動作（action） ──────────────────────────────────────────────────────
// 次のどれか 1 つのキーを持つオブジェクト。知らないキーだけの動作は読み手が何もしない（Kind==Unknown）。
public sealed class KurokoTinyAction
{
    public KurokoTinyPlayTitle? PlayTitle { get; set; }
    public string? ShowMenu { get; set; }
    public KurokoTinyEmpty? Resume { get; set; }
    public int? SetAudio { get; set; }             // title.json の音声の番号（1 始まり）
    public int? SetSubtitle { get; set; }          // title.json の字幕の番号（1 始まり。0＝なし）
    public KurokoTinyEmpty? Stop { get; set; }

    // 持っているキーの数（1 のはず。検査に使う）。
    [JsonIgnore]
    public int KeyCount => (PlayTitle is null ? 0 : 1) + (ShowMenu is null ? 0 : 1) + (Resume is null ? 0 : 1)
        + (SetAudio is null ? 0 : 1) + (SetSubtitle is null ? 0 : 1) + (Stop is null ? 0 : 1);

    [JsonIgnore]
    public KurokoTinyActionKind Kind =>
        KeyCount != 1 ? KurokoTinyActionKind.Unknown
        : PlayTitle is not null ? KurokoTinyActionKind.PlayTitle
        : ShowMenu is not null ? KurokoTinyActionKind.ShowMenu
        : Resume is not null ? KurokoTinyActionKind.Resume
        : SetAudio is not null ? KurokoTinyActionKind.SetAudio
        : SetSubtitle is not null ? KurokoTinyActionKind.SetSubtitle
        : KurokoTinyActionKind.Stop;

    public static KurokoTinyAction PlayTitleAction(int title, int? chapter = null)
        => new() { PlayTitle = new KurokoTinyPlayTitle { Title = title, Chapter = chapter } };
    public static KurokoTinyAction ShowMenuAction(string menuId) => new() { ShowMenu = menuId };
    public static KurokoTinyAction ResumeAction() => new() { Resume = new KurokoTinyEmpty() };
    public static KurokoTinyAction SetAudioAction(int number) => new() { SetAudio = number };
    public static KurokoTinyAction SetSubtitleAction(int number) => new() { SetSubtitle = number };
    public static KurokoTinyAction StopAction() => new() { Stop = new KurokoTinyEmpty() };
}

public enum KurokoTinyActionKind { Unknown, PlayTitle, ShowMenu, Resume, SetAudio, SetSubtitle, Stop }

public sealed class KurokoTinyPlayTitle
{
    public int Title { get; set; }                 // disc.json の titles[].id
    public int? Chapter { get; set; }              // 章の番号（1 始まり。任意）
}

// 中身の無い動作（resume・stop）の値。JSON では {}。
public sealed class KurokoTinyEmpty
{
}
