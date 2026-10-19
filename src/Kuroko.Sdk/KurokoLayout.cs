namespace Kuroko;

// Kuroko（黒子・.krk）コンテナ内のパス規約。パスは先頭スラッシュ付きの絶対パスで返す（ZIP のエントリ名は先頭の
// スラッシュを除いたもの。KurokoContainerReader・KurokoZipWriter がそろえる）。レイアウトの一次仕様は kuroko-spec（spec/kuroko.ja.md）。
public static class KurokoLayout
{
    public const string Format = "kuroko";

    // fields dual（2映像トラック）の bottom フィールド映像に与える正準トラック番号。音声/字幕の小さな番号と衝突しない
    // 大きな固定値。top フィールドは正準0（従来どおり映像）、bottom はこの番号。再生側が2つ目のデコードワーカーへ振り分ける。
    public const int BottomVideoCanonical = 1_000_000;
    public const int Version = 2; // v2＝セグメント/プレイリスト（docs §13）。v1＝単一 av.mkv。
    // 書き手が出すレイアウトの版。5＝番号・パレット・時刻・メニューの種類・セルの指し方・言語の書き方をそろえた形
    //（公開仕様 kuroko-spec 1.0）。読み手は 3・4（旧 clean 形式）も読む（KurokoTitleSource が内部表現へそろえる）。
    public const int CleanLayoutVersion = 5;

    // controller 種別（マニフェストの controller 値）。
    public const string ControllerDvdVideoVm = "dvd-video-vm";
    // プレーン再生の枠組み（ナビゲーション無し）。放送/配信TS 等の素の動画を運ぶ土台。
    public const string ControllerNone = "none";
    // メニュー・ボタン・動作を JSON で宣言的に書くコントローラ（KurokoTinyModel.cs）。タイトルの再生は none と同じ。
    public const string ControllerKurokoTiny = "kuroko-tiny";

    // メニュードメイン識別子。
    public const string MenuDomainVmgm = "VMGM";
    public static string MenuDomainVts(int vts) => $"VTS{vts:00}";

    public const string ManifestFile = "/KUROKO.MANIFEST";

    public const string ControlDir = "/CONTROL";
    public const string DiscJson = "/CONTROL/disc.json";
    // VM命令は JSON 埋め込み（disc.json/title.json/buttons.json の hex＋asm）。別 .vmbin は持たない。

    public const string VideoDir = "/VIDEO";
    public const string MenuDir = "/MENU";

    // タイトル（ディスク通し番号 1..n）配下。
    public static string TitleDir(int titleId) => $"/VIDEO/T{titleId:00}";
    public static string TitleAvMkv(int titleId) => $"{TitleDir(titleId)}/av.mkv"; // v1（v2 では未使用）
    public static string TitleJson(int titleId) => $"{TitleDir(titleId)}/title.json";

    // v2（docs §13）：セグメント（共有run単位）配下。実体は1回だけ格納。
    public const string SegmentsDir = "/SEGMENTS";
    public static string SegmentDir(string id) => $"/SEGMENTS/{id}";
    public static string SegmentAvMkv(string id) => $"{SegmentDir(id)}/av.mkv";
    public static string SegmentJson(string id) => $"{SegmentDir(id)}/seg.json";
    // セグメント内メディアの解像度パーツ（§8 fields×film 混在）。seg.json の parts[].file がこの名前を指す。
    // パーツ間で解像度/ケイデンスが変わる（24p=フル高 weave／29.97i=半高 fields）。demux はパーツを連結する。
    public static string SegmentPartMkv(string id, int index) => $"{SegmentDir(id)}/av.p{index:00}.mkv";

    // 音声トラックのラウドネス・ゲインエンベロープ（§9.1）。点数が多くなり得るので seg.json には書かず
    // ここへ置き、seg.json の audio[].tracks[].loudness.file がこの相対名を指す。<source>＝物理サブストリームキー。
    public static string SegmentLoudnessName(int source) => $"loudness/{source}.json";
    public static string SegmentLoudness(string id, int source) => $"{SegmentDir(id)}/{SegmentLoudnessName(source)}";

    // 字幕の物理実体（§11）。av.mkv には字幕を入れず、ここへ復号済みで置く。<source>＝物理サブストリームキー。
    public static string SubsDir(string id) => $"{SegmentDir(id)}/subs";
    public static string SubsTextJson(string id, int source) => $"{SubsDir(id)}/text/{source}.json";
    public static string SubsImageDir(string id, int source) => $"{SubsDir(id)}/img/{source}";
    public static string SubsImageIndex(string id, int source) => $"{SubsImageDir(id, source)}/index.json";
    // cue の実体ファイル（<n>.png / <n>.spu / <n>.pgs）。index.json の cue が相対名で指す。
    public static string SubsImagePng(string id, int source, string name) => $"{SubsImageDir(id, source)}/{name}";

    // メニュー（ドメイン＋PGC番号で一意）配下。
    public static string MenuPgcDir(string domain, int pgc) => $"/MENU/{domain}/PGC{pgc:00}";
    public static string MenuVideoMkv(string domain, int pgc) => $"{MenuPgcDir(domain, pgc)}/video.mkv";
    public static string MenuAudioMka(string domain, int pgc) => $"{MenuPgcDir(domain, pgc)}/audio.mka";
    public static string MenuButtonsJson(string domain, int pgc) => $"{MenuPgcDir(domain, pgc)}/buttons.json";
    public static string MenuPaletteJson(string domain, int pgc) => $"{MenuPgcDir(domain, pgc)}/palette.json";

    // v3 メニュー（セル単位ファイル）。menu.json＋各セルの C<nn>.mkv（映像+音声）／C<nn>.spu（SPU図柄・任意）。
    public static string MenuJson(string domain, int pgc) => $"{MenuPgcDir(domain, pgc)}/menu.json";
    public static string MenuCellAv(string domain, int pgc, int cell) => $"{MenuPgcDir(domain, pgc)}/C{cell:00}.mkv";
    public static string MenuCellSpu(string domain, int pgc, int cell) => $"{MenuPgcDir(domain, pgc)}/C{cell:00}.spu";

    // kuroko-tiny：ナビゲーションと、メニュー 1 つずつのフォルダ（背景・BGM・ボタンの画像は menu.json と同じ所）。
    public const string TinyNavJson = "/CONTROL/nav.json";
    public static string TinyMenuDir(string menuId) => $"/MENU/{menuId}";
    public static string TinyMenuJson(string menuId) => $"{TinyMenuDir(menuId)}/menu.json";
    public static string TinyMenuFile(string menuId, string name) => $"{TinyMenuDir(menuId)}/{name}";
}
