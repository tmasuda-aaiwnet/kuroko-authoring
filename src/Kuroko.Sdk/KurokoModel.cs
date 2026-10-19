using System.Text.Json.Serialization;

namespace Kuroko;

// Kuroko（黒子・.krk）の JSON モデル。一次仕様は kuroko-spec（spec/kuroko.ja.md）。
// JSON 直列化を素直に往復させるため、各型は可変プロパティ＋既定値（変換器が組み立てやすい）にする。
// 時刻は TimeSpan を文字列（"c" 形式＝完全往復）で持つ（KurokoTime で変換）。

// ── ルートマニフェスト（/KUROKO.MANIFEST） ───────────────────────────────
public sealed class KurokoManifest
{
    public string Format { get; set; } = KurokoLayout.Format;
    public int Version { get; set; } = KurokoLayout.Version;
    // 再生意味論の種別。現状 "dvd-video-vm"。未知値は再生側が断る。
    public string Controller { get; set; } = KurokoLayout.ControllerDvdVideoVm;
    public string? CreatedBy { get; set; }
    // 任意の自由記述コメント。変換器は符号化パラメータ（インターレース方式・CRF・キーフレーム間隔・コーデック等）を記す。
    public string? Comment { get; set; }

    [JsonIgnore]
    public bool IsKuroko => string.Equals(Format, KurokoLayout.Format, System.StringComparison.OrdinalIgnoreCase);
}

// ── ディスク級（/CONTROL/disc.json。controller=dvd-video-vm） ──────────────
public sealed class KurokoDisc
{
    public int Region { get; set; }
    // ディスク投入時に実行する PGC（DVD の FirstPlay 相当）。
    public KurokoPgcRef? FirstPlay { get; set; }
    // FirstPlay PGC の命令列（FPC）。bytecode を hex＋ニーモニックで持つ（JSON埋め込み）。
    public List<KurokoVmCommand> FirstPlayCommands { get; set; } = new();
    public List<KurokoTitleEntry> Titles { get; set; } = new();
    // ファーストプレイ（§6.1）。メディアプレーヤーとして再生するときの開始状態。上の FirstPlay（VM）とは別物。
    public KurokoFirstPlayback? FirstPlayback { get; set; }
    public List<KurokoMenuDomain> MenuDomains { get; set; } = new();
    // タイトルドメイン（VTS_PGCIT）の PGC 表（PGC↔タイトル対応＋中間 PGC の命令）。旧 krk は空。
    public List<KurokoTitleDomain> TitleDomains { get; set; } = new();
}

// DVD VM 命令（8バイト）。再生は Hex を byte[] にして DvdVmInterpreter で実行、Asm は人間用の注釈。
public sealed class KurokoVmCommand
{
    public string Hex { get; set; } = "";   // 16桁hex（8バイト）
    public string Asm { get; set; } = "";   // ニーモニック（表示専用）
}

// PGC の命令群（PRE/POST／セル命令）。
public sealed class KurokoPgcCommands
{
    public List<KurokoVmCommand> Pre { get; set; } = new();
    public List<KurokoVmCommand> Post { get; set; } = new();
    public List<KurokoVmCommand> Cell { get; set; } = new(); // cell_command_nr 由来のセル命令
}

public sealed class KurokoPgcRef
{
    // "vmg" もしくはメニュードメイン識別子（"VMGM"/"VTS01"…）。
    public string Domain { get; set; } = "vmg";
    public int Pgc { get; set; }
}

public sealed class KurokoTitleEntry
{
    public int Id { get; set; }            // ディスク通し番号（/VIDEO/T{Id} に対応）
    public string? Name { get; set; }      // 表示名（controller=none のサービス名/番組名など。DVD は null）
    public int Vts { get; set; }
    public int VtsTitle { get; set; }
    public string? MenuDomain { get; set; } // 関連する VTSM ドメイン（"VTS01"…）
    // タイトルの長さ（ms）。レイアウト version 5 からはこちらだけを書く。
    public double DurationMs { get; set; }
    // 旧（version 4 以前）のタイトルの長さ（"c" 形式の文字列）。読み出しの後方互換のためだけに残す（書き手は null）。
    public string? Duration { get; set; }
    public string Video { get; set; } = ""; // 例 "VIDEO/T01"

    // 長さ（version 5 の durationMs を優先し、無ければ旧の文字列）。
    [JsonIgnore]
    public TimeSpan DurationValue => DurationMs > 0 ? TimeSpan.FromMilliseconds(DurationMs) : KurokoTime.Parse(Duration);
}

public sealed class KurokoMenuDomain
{
    public string Id { get; set; } = "";   // "VMGM" / "VTS01"…
    public List<KurokoMenuPgc> Pgcs { get; set; } = new();
}

// ── タイトルドメイン（VTS_PGCIT）。仕様 §「タイトルドメイン」 ────────────────────
// Link 系命令（LinkPGCN / Next/Prev/GoUp PGC）は「現ドメイン内」の PGC リンクで、タイトル再生中は
// その VTS のタイトルドメイン（VTS_PGCIT）で解決する。VTS_PGCIT にはタイトル番号を持たない中間 PGC
// （PRE で他所へ転送する通過専用など）が存在し得るため、PGC 番号→タイトル対応と中間 PGC の命令を
// disc.json に保持する（例：あるタイトルセットで PGC#1=タイトル7 / PGC#2=中間 / PGC#3=タイトル8 / PGC#4=中間）。
public sealed class KurokoTitleDomain
{
    public int Vts { get; set; }
    public List<KurokoTitleDomainPgc> Pgcs { get; set; } = new();
}

public sealed class KurokoTitleDomainPgc
{
    public int Pgc { get; set; }     // VTS_PGCIT 内の PGC 番号（1-based）
    public int Title { get; set; }   // 対応するディスク通しタイトル番号。0＝タイトル番号を持たない中間 PGC
    // 中間 PGC の命令（PRE/POST/CELL）。タイトル PGC の命令は title.json（pgcCommands）にあるため null。
    // 中間 PGC のセル映像は変換対象外（PRE が転送しないうえセルを持つ中間 PGC は再生不可＝制限事項）。
    public KurokoPgcCommands? Commands { get; set; }
}

// メニュー PGC 1つ。種別・命令群を持つ（背景/ボタンの実体は M1b 後段で MENU/<dom>/PGC<nn>/ に置く）。
public sealed class KurokoMenuPgc
{
    public int Pgc { get; set; }            // PGC 番号（1-based）
    public string? Type { get; set; }        // "Title"/"Root"/"Subpicture"/"Audio"/"Angle"/"Chapter"
    public string Path { get; set; } = "";   // 例 "MENU/VMGM/PGC01"
    public KurokoPgcCommands Commands { get; set; } = new();
    // PGC_UOP_CTL（禁止ユーザー操作、32bit。bitセット=禁止）。0=全許可。旧krkは 0 でフォールバック。
    public uint UopControl { get; set; }
}

// ── メニュー実体（/MENU/<domain>/PGC<nn>/buttons.json・palette.json） ───────
// buttons.json：当該 PGC の各セルの「セル境界・静止時間・代表PCI（ボタン）」。背景映像は同フォルダの
// video.mkv（音声は audio.mka）。ハイライト色は palette.json（YUV16色）と PCI の色組で描く。
public sealed class KurokoButtons
{
    public List<KurokoMenuCell> Cells { get; set; } = new();
    // ボタン図柄ハイライトSPUの、表示モード別 video.mkv トラック番号（0=映像, 1..=字幕）。DVDの PGC_SPST_CTL 由来。
    // 16:9アナモルフィックは Wide 枠、4:3は 43 枠を使う。null（旧krk）なら再生側は既定トラック(1)にフォールバック。
    public KurokoMenuHighlightSpu? HighlightSpu { get; set; }
    // メニュー背景映像 video.mkv がインターレース(29.97i)か。モーションメニュー再生時に bob デインターレースするか
    // の判定に使う（静止メニューは1フレームなので影響なし）。既定 true（旧krk・DVDメニューは通常インターレース）。
    public bool Interlaced { get; set; } = true;
    public bool Tff { get; set; } = true;   // bob のフィールド順
    // メニュー背景音声（BGM等）の video.mkv トラック番号（0=映像, 1..=字幕, その後=音声）。-1=音声なし。
    // 音声は映像とセットの背景AV（DVD仕様上ナビ連動の独立音声は無い）なので video.mkv に同梱する。
    public int AudioTrack { get; set; } = -1;
    // メニュー背景音声のコーデック（"opus"/"ac3"）。再生側の音声デコーダ選択に使う。既定 "ac3"（旧krk互換）。
    public string AudioCodec { get; set; } = "ac3";
    // メニュー背景 video.mkv の映像コーデック（"h264"/"h265"）。再生側のデコーダ選択に使う。既定 "h264"（旧krk互換）。
    public string VideoCodec { get; set; } = "h264";
    // メニュー映像の表示アスペクト比（"16:9"/"4:3"。メニューPGCのVideoAttr由来）。再生側の表示枠に使う
    // （タイトルの比を流用すると16:9メニューが4:3枠で圧縮表示される問題への対処）。空=未設定（旧krk）→
    // 再生側はタイトル比へフォールバック。
    public string Aspect { get; set; } = "";
    // メニュー背景 video.mkv の映像フレーム数。1＝単一フレーム（静止画）メニュー。再生側は 1 のとき、その1枚が
    // デコーダの並べ替えバッファに保持され EOF まで出ない問題を避けるため、映像投入直後に即 Drain して排出する。
    // 0/未設定（旧krk）は不明としてフォールバック（従来動作）。
    public int VideoFrames { get; set; }
}

// メニューのボタン図柄SPU（ハイライト）の表示モード別トラック解決。DVDの PGC_SPST_CTL に対応。
// 表示モード切替を将来実装する場合も、4枠が揃っているので選び直せる。MkvTrack はメニューのアスペクトに合う代表。
public sealed class KurokoMenuHighlightSpu
{
    public int DvdStream { get; set; }              // 論理サブピクチャ番号（SPST_CTL/SPRM[2] が参照）
    public int MkvTrack { get; set; } = 1;          // 既定（メニューのアスペクトに合う代表トラック）
    public int Track43 { get; set; } = -1;          // 4:3ノーマル枠
    public int TrackWide { get; set; } = -1;        // 16:9ワイド枠
    public int TrackLetterbox { get; set; } = -1;   // レターボックス枠
    public int TrackPanScan { get; set; } = -1;     // パン&スキャン枠
}

// メニューPGCの1セル。time は video.mkv の容器時刻（秒・"c"形式）。
public sealed class KurokoMenuCell
{
    public int Index { get; set; }                  // セル番号（0..）
    public string StartTime { get; set; } = "00:00:00"; // video.mkv 上の開始時刻
    public string Duration { get; set; } = "00:00:00";
    public int Still { get; set; }                  // 静止時間（秒）。0=なし、-1=無限（ユーザ入力待ち＝0xFF）
    public int CellCommand { get; set; }            // セル終端で実行するセル命令番号（1-based、0=なし）
    public KurokoPci? Pci { get; set; }             // 代表PCI（ボタンが無ければ null）
}

// NAV PCI のハイライト情報（ボタン群＋強制選択/実行）。
public sealed class KurokoPci
{
    public int ForcedSelect { get; set; }           // FOSL_BTNN（0=なし）
    public int ForcedActivate { get; set; }         // FOAC_BTNN
    public List<KurokoMenuButton> Buttons { get; set; } = new();
    public List<KurokoButtonColorGroup> ColorGroups { get; set; } = new();
    // このVOBU区間の禁止操作（PCI_GI の vobu_uop_ctl、32bit）。再生側は PGC_UOP_CTL と OR して判定。0=なし。
    public uint VobuUop { get; set; }
}

// メニューボタン1つ（領域矩形・上下左右遷移・色組・ボタン命令）。
public sealed class KurokoMenuButton
{
    public int Number { get; set; }                 // 1-based
    public int X0 { get; set; }
    public int Y0 { get; set; }
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int ColorGroup { get; set; }             // BTN_COLIT のどの色組（0..2）
    public int AutoAction { get; set; }             // 0以外なら選択即実行
    public int Up { get; set; }
    public int Down { get; set; }
    public int Left { get; set; }
    public int Right { get; set; }
    public KurokoVmCommand Command { get; set; } = new(); // ボタン命令（hex＋ニーモニック）
}

// ボタンの色組（選択/動作の色・コントラスト、各4画素タイプ）。
public sealed class KurokoButtonColorGroup
{
    public int[] SelectColor { get; set; } = new int[4];
    public int[] SelectContrast { get; set; } = new int[4];
    public int[] ActionColor { get; set; } = new int[4];
    public int[] ActionContrast { get; set; } = new int[4];
}

// palette.json：メニューPGCのサブピクチャ用 YUV パレット（16色。各要素 6桁hex）。
public sealed class KurokoMenuPalette
{
    public List<string> Palette { get; set; } = new();
}

// ── タイトル構造＋タイミング（/VIDEO/Tnn/title.json） ──────────────────────
public sealed class KurokoTitle
{
    public KurokoVideo Video { get; set; } = new();
    public List<KurokoAudio> Audio { get; set; } = new();
    public List<KurokoSubtitle> Subtitle { get; set; } = new();
    // サブピクチャ用 YUV パレット（16色。各要素は 24bit を 6桁hex 文字列で）。
    public List<string> Palette { get; set; } = new();
    public List<KurokoProgram> Programs { get; set; } = new();
    public List<KurokoCell> Cells { get; set; } = new();
    public List<KurokoChapter> Chapters { get; set; } = new();
    public KurokoTiming Timing { get; set; } = new();
    // タイトル PGC の命令（PRE/POST／セル命令）。VM の JumpTT 等の到達先で実行する。
    public KurokoPgcCommands? PgcCommands { get; set; }
    // タイトル PGC の PGC_UOP_CTL（禁止ユーザー操作、32bit。bitセット=禁止）。0=全許可。旧krkは 0 でフォールバック。
    public uint UopControl { get; set; }
    // v2（docs §13）：セグメント参照のプレイリスト。空なら v1（単一 av.mkv・Cells が av.mkv 内時刻）。
    // 非空なら各 Cell の Segment/SegCell が実体（/SEGMENTS/<id>/av.mkv）を指す。
    public List<KurokoSegmentRef> Segments { get; set; } = new();
    // アングル（§10.1 の angleType:"dvd"）。Angles が非空なら Segments は空で、アングルごとのプレイリストを持つ。
    // セル・章・尺・音声・字幕はアングル間で共通（アングルブロック内の各アングルの尺は規格上等しい）。
    public string? AngleType { get; set; }
    public List<KurokoTitleAngle> Angles { get; set; } = new();
}

// アングル1つ。違うのは Segments（プレイリスト）だけ。
public sealed class KurokoTitleAngle
{
    public int Number { get; set; }                        // 1 始まり
    public string? Name { get; set; }                      // 任意（DVD にアングル名のメタデータは無い）
    public List<KurokoSegmentRef> Segments { get; set; } = new();
}

// v2：タイトルのプレイリスト1要素（タイトルセル [titleCellStart..+cellCount) ＝ セグメント id の segCell [0..cellCount)）。
public sealed class KurokoSegmentRef
{
    public string Id { get; set; } = "";           // セグメントID（"S0001"）
    public int TitleCellStart { get; set; }         // タイトルのセル index（0基準）でのこのセグメント開始
    public int CellCount { get; set; }
    public string StartTime { get; set; } = "00:00:00"; // タイトル通し時刻でのこのセグメント開始
}

// v2：セグメント（/SEGMENTS/<id>/seg.json）。メディア（av.mkv）に帰属する 0基準タイミング・セル・トラック対応。
// 音声/字幕トラックの「存在」はセグメントごとに変わり得る（例：無音のリーダーセルだけのセグメント、字幕の無い
// オープニング）ため、論理↔mkvTrack の対応は**このセグメントの av.mkv 実体に合わせて seg.json 側に持つ**（再生側は
// 選択中セグメントの seg.json で解決）。title.json は VM/UI 用の論理集約（言語一覧・SPST_CTL 等）を持つ。
public sealed class KurokoSegment
{
    public KurokoVideo Video { get; set; } = new();
    public List<KurokoAudio> Audio { get; set; } = new();       // このセグメント av.mkv の音声トラック対応
    public List<KurokoSubtitle> Subtitle { get; set; } = new(); // このセグメント av.mkv の字幕トラック対応
    public List<KurokoSegCell> Cells { get; set; } = new();     // セグメント内0基準（segCell 0..）
    public KurokoTiming Timing { get; set; } = new();           // 0基準（cellBaseFrame/totalMs）
    // メディア解像度パーツ（§8 fields×film 混在）。null/空＝単一 av.mkv。設定時は各パーツを順に連結して再生する。
    public List<KurokoSegmentPart>? Parts { get; set; }
}

// メディア解像度パーツ（KurokoNonePart の内部像）。file＝SEGMENTS/<id>/ 配下の相対名。
public sealed class KurokoSegmentPart
{
    public string File { get; set; } = "";
    public double DurationMs { get; set; }
    public int PixelHeight { get; set; }                  // 参考：fields=半高／weave=フル高
    public string InterlaceMode { get; set; } = "weave";  // 参考："weave" | "fields"
    public string FieldLayout { get; set; } = "single";   // "dual"（fields 2トラック）| "single"。§8.1
    // 疎セル（静止画セル：映像1〜数枚を IFO 尺ぶん保持）を個別符号化したパーツ。true なら再生側は映像投入直後に
    // 即 Drain してデコーダ保持中の1枚を排出する（さもないと次パーツの映像が来るまで表示されない）。§8.2
    public bool Still { get; set; }
}

// v2：セグメント内の1セル（0基準の位置・尺のみ。still/commandNr 等 VM 意味論は title.json.cells に持つ）。
public sealed class KurokoSegCell
{
    public string StartInSegment { get; set; } = "00:00:00"; // セグメント av.mkv 内の開始時刻
    public string Duration { get; set; } = "00:00:00";
}

public sealed class KurokoVideo
{
    // av.mkv の映像コーデック（"h264" / "h265"）。旧krkは未記載＝h264。再生側のデコーダ選択に使う
    //（デムクサは堅牢性のため AVCodecParameters を覗かない設計なので、メタデータで持つ）。
    // h265 は実インターレースも weave フレームのプログレッシブ符号化（bob 解除は従来どおり
    // interlaced/tff/interlacedRanges で行う＝再生モデルはコーデック非依存）。
    public string Codec { get; set; } = "h264";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Aspect { get; set; } = "4:3"; // "4:3" / "16:9"
    public string Fps { get; set; } = "30000/1001";
    public bool Interlaced { get; set; }
    public bool Tff { get; set; } = true; // インターレース時のフィールド順（true=top field first）。bob 用。
    // インターレース維持の格納方式。旧krk未記載＝"weave"（後方互換）。
    //   "weave"  ：T/B フィールドを1枚(フル高)へ織り込んで符号化。再生時に1行おきに分離して bob（従来）。
    //   "fields" ：T/B フィールドを各々「高さ半分・倍フレームレート」の progressive フレームとして符号化
    //              （av.mkv 自体が半高さ・倍フレーム。interlaced=true・interlacedRanges＝全域）。動きのある
    //              インターレース素材で櫛が消え圧縮が効く。再生時は各半高フレームを1フィールドとして全高へ展開＋bob。
    // 判定は seg.json＝そのセグメント av.mkv の実体、title.json＝タイトル集約（fields セグメントを含むなら "fields"）。
    // 再生側の実際の「フィールド展開/従来bob」切替は、フレーム高が title.Video.Height（表示フル高）の半分か否かで行う
    //（混在タイトルにも自動追従。InterlaceMode は権威フラグ兼ドキュメント）。
    public string InterlaceMode { get; set; } = "weave";
    // fields の格納レイアウト。"single"＝1トラックに T,B,T,B と半高倍フレーム（旧・パリティはPTS由来で反転し得る）。
    //   "dual"＝**2映像トラック**：track0=topフィールド／track1=bottomフィールド（各29.97・半高、実フィールド時刻PTS）。
    //   パリティ＝トラック番号で確定（デコーダ並び替え/PTS推測に一切依存せず反転しない）。fields のとき有効。
    public string FieldLayout { get; set; } = "single";
    // bob する区間（progressive=film 以外＝本当のインターレース）。容器PTS(=実時間)の ms 範囲。film＋29.97i
    // 混在に対応するため per-frame ではなく区間で持つ。空/未設定なら全フレーム weave（純 film）。
    // タイミングは av.mkv の容器PTS に焼き込み済みなので、これは「weave か bob か」の判定だけに使う。
    public List<KurokoRange>? InterlacedRanges { get; set; }
}

// 時刻区間（ms）。bob 区間など。
public sealed class KurokoRange
{
    public double StartMs { get; set; }
    public double EndMs { get; set; }
}

public sealed class KurokoAudio
{
    // 形式上の番号（title.json の audio[].number。1 始まり）。ファーストプレイ（firstPlayback.audio）はこれで引く。
    // 読み出し時に KurokoTitleSource が埋める（旧 version 4 以前は「並びの何番目か」を番号とみなす）。JSON には出さない。
    [JsonIgnore] public int Number { get; set; }
    public int DvdStream { get; set; } = -1; // DVD 論理音声ストリーム番号（VM/AST_CTL が参照）。title.json 用。seg.json では -1
    public int MkvTrack { get; set; }    // title.json=正準トラック番号／seg.json=そのセグメント av.mkv 内の実トラック
    public string Codec { get; set; } = "ac3";
    public int Channels { get; set; } = 2;
    public string? Language { get; set; }
    public int AstCtl { get; set; } = -1; // PGC の AST_CTL 実ストリーム番号（title.json。取れなければ -1）
    // デュアルモノ（二か国語＝L主/R副）の区間（ms）。放送TS 等。空/未設定＝デュアルモノ無し。再生側はこの区間内で
    // のみ 主(L)/副(R) を出し分け、区間外は通常ステレオ。番組境界での デュアルモノ↔ステレオ の切替に対応する。
    public List<KurokoRange>? DualMonoRanges { get; set; }
    // ラウドネス・メタデータ（§9.1）。音声本体はフルレンジのまま格納し、再生時に任意適用する。
    // null＝そのトラックには無い＝フルレンジ。seg.json（物理トラック）側が権威。
    // Loudness=seg.json に載る参照（型・dialnorm・エンベロープのファイル名）。
    // LoudnessEnvelopes=そのエンベロープ実体（別ファイルへ書き出す。JSON には出さない）。
    public KurokoLoudness? Loudness { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public KurokoLoudnessFile? LoudnessEnvelopes { get; set; }
    // 元DVDの物理サブストリームID（AC-3 0x80+n / DTS 0x88+n / LPCM 0xA0+n / MPEG 0x1C0+n。ffmpeg mpegps 準拠）。
    // seg.json＝このトラックの実体、title.json＝この論理音声が指す物理。再生はこれで title→seg を写像する
    //（§13.3/13.4。論理番号は PGC ごとの表なので共有セグメントには書けない）。旧krkは -1（DvdStream 照合へフォールバック）。
    public int Substream { get; set; } = -1;
}

public sealed class KurokoSubtitle
{
    // 形式上の番号（title.json の subtitle[].number。1 始まり）。KurokoAudio.Number と同じ。JSON には出さない。
    [JsonIgnore] public int Number { get; set; }
    public int DvdStream { get; set; } = -1; // DVD 論理字幕ストリーム番号（VM/SPST_CTL が参照）。title.json 用。seg.json では -1
    public int MkvTrack { get; set; }    // title.json=代表の正準トラック／seg.json=そのセグメント av.mkv 内の実トラック
    public string? Language { get; set; }
    public int SpstCtl { get; set; } = -1;
    // 元DVDの物理サブストリームID（0x20+n）。seg.json 用（このトラックの実体）。旧krkは -1。
    public int Substream { get; set; } = -1;
    // 1論理字幕はアスペクト別に最大4つの物理サブピクチャ（4:3/ワイド/レターボックス/パン&スキャン）を持つ。
    // Track**＝各変種の正準トラック番号（その変種が無ければ -1）。再生側がタイトルのアスペクトに合うものを選び、
    // 無ければあるものへフォールバックする。Phys**＝各変種の物理サブストリームID（title.json 用。v2 の
    // title→seg 写像はこれで行う。旧krkは -1＝DvdStream 照合へフォールバック）。
    public int Track43 { get; set; } = -1;
    public int TrackWide { get; set; } = -1;
    public int TrackLetterbox { get; set; } = -1;
    public int TrackPanScan { get; set; } = -1;
    public int Phys43 { get; set; } = -1;
    public int PhysWide { get; set; } = -1;
    public int PhysLetterbox { get; set; } = -1;
    public int PhysPanScan { get; set; } = -1;
}

public sealed class KurokoProgram
{
    public int Pgn { get; set; }         // プログラム番号（1..）
    public int EntryCell { get; set; }   // 開始セル（1..）
}

public sealed class KurokoCell
{
    public int Index { get; set; }       // セル番号（0..）
    public string Duration { get; set; } = "00:00:00";
    public string StartTime { get; set; } = "00:00:00"; // タイトル通し時刻
    public int Still { get; set; }       // セル静止時間（秒）。0=なし、-1=無限（ユーザ入力待ち＝0xFF）
    public int CommandNr { get; set; }   // セル終端で実行するセル命令番号（1-based、PgcCommands.Cell への索引。0=なし）
    // v2（docs §13）：このセルの実体が属するセグメントID と、その中でのセル index（0基準）。空なら v1（単一 av.mkv）。
    public string Segment { get; set; } = "";
    public int SegCell { get; set; }
    // アングルブロック内での順番＝アングル番号（1始まり。§10.1 の angleType:"dvd"）。0＝ブロック外。
    public int Angle { get; set; }
}

public sealed class KurokoChapter
{
    public int Number { get; set; }
    public int StartCell { get; set; }   // 開始セル（0-based index）
    public string StartTime { get; set; } = "00:00:00";
}

// RFF 相当のタイミング。テレシネのフレーム表示時間・章/セル↔フレーム対応を保持。
// 詳細なフィールド数/アンカー列は再生配線時に確定（現状は枠のみ）。
public sealed class KurokoTiming
{
    public string FrameRate { get; set; } = "30000/1001";
    public int[] CellBaseFrame { get; set; } = System.Array.Empty<int>(); // [cellCount+1]、末尾=総フレーム
    public int[] ChapterFrame { get; set; } = System.Array.Empty<int>();  // 章先頭の表示順フレーム番号
    // クリーン総尺(ms)＝av.mkv に焼き込んだ容器PTSの実尺。Duration はこれを正とする（IFO宣言尺はBCD丸めで
    // 数秒ズレるため）。0/未設定なら disc.json の宣言尺へフォールバック。
    public double TotalMs { get; set; }
}
