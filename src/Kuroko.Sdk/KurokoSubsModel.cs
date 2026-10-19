namespace Kuroko;

// 字幕の物理実体（SEGMENTS/S####/subs/）の JSON モデル。一次仕様は kuroko-spec（spec/kuroko.ja.md） の字幕の章。
// DVD-Video の字幕（サブピクチャ）は**画像型**：物理サブストリーム（表示モード変種ごとに別物理ID）を SPU デコーダで
// 復号し、subs/img/<source>/index.json（このファイル）＋ cue 毎の PNG（パレット/コントラスト適用済み RGBA）で持つ。
// title.json（論理）→ seg.json（物理一覧）→ subs/<type>/<source> の 2 段解決で引く（av.mkv には字幕を入れない）。

// subs/img/<source>/index.json
public sealed class KurokoSubsImage
{
    public string Type { get; set; } = "image";   // "image"（DVD SPU）
    public string? Lang { get; set; }
    public KurokoSubsCanvas Canvas { get; set; } = new(); // 字幕座標の基準キャンバス（映像の表示画素）
    public System.Collections.Generic.List<KurokoSubsCue> Cues { get; set; } = new();
}

public sealed class KurokoSubsCanvas
{
    public int Width { get; set; }
    public int Height { get; set; }
}

// 1 表示区間（DVD の 1 SPU）。startMs..endMs は SPU の DCSQ（STA_DSP/STP_DSP）由来のセグメント内 0 基準時刻。
//
// 表現は3通り（いずれか、または併記）。再生器は **Pgs → Spu → Steps** の順に優先する。
//  - **Pgs**：Blu-ray の Presentation Graphic Stream の生データ（同ディレクトリの *.pgs）。BD からの変換はこちら。
//  - **Spu**：DVD のサブピクチャ生データ（同ディレクトリの *.spu）。1ファイルで**時間変化（フェード・
//    CHG_COLCON による行範囲ごとの出し分け）を完全に含む**ため、状態ごとの PNG を持たずに済み小さい。
//    DVD からの変換はこちらを使う。
//  - **Steps**：PNG と不透明度による汎用表現。DVD/BD 以外の入力や、SPU/PGS を解釈できない実装向け。
public sealed class KurokoSubsCue
{
    public double StartMs { get; set; }
    public double EndMs { get; set; }
    // 同ディレクトリの SPU ファイル名（例 "0001.spu"）。無ければ null。
    public string? Spu { get; set; }
    // 同ディレクトリの PGS ファイル名（例 "0001.pgs"）。Blu-ray 由来。無ければ null。
    // SPU と同じ考え方で、表示開始の Display Set から消去の直前までを生のまま 1 ファイルに入れる
    // （パレット更新の Display Set がフェード・色替えを担うので、PNG へ展開すると再現度が落ちる）。
    // DVD の SPU と違い**パレットはストリーム内（PDS）**にあるので、外部の palette は要らない。
    public string? Pgs { get; set; }
    // 表示ステップ（フェード/途中変化）。DVD の単純な表示は要素1個（開始時に画像を出す）。
    public System.Collections.Generic.List<KurokoSubsStep> Steps { get; set; } = new();
}

public sealed class KurokoSubsStep
{
    public double AtMs { get; set; }              // cue 開始からの相対時刻（0＝cue 先頭）
    public int X { get; set; }                    // キャンバス上の左上座標
    public int Y { get; set; }
    public string Image { get; set; } = "";       // 同ディレクトリの PNG ファイル名（例 "0001.png"）
    public double Opacity { get; set; } = 1.0;
}

// subs/text/<source>.json（テキスト型。§11・§11.1）。放送 TS の ARIB 字幕は charset "arib" で持つ。
public sealed class KurokoSubsText
{
    public string Type { get; set; } = "text";
    public string? Lang { get; set; }
    public string? Name { get; set; }
    // 文字コード体系。null＝"unicode"。"arib"＝ARIB 字幕（ARIB 外字入りのフォントで描き、DRCS は Drcs の形を使う）。
    public string? Charset { get; set; }
    public KurokoSubsCanvas? Canvas { get; set; }                       // Lines の座標系
    public System.Collections.Generic.List<KurokoSubsDrcs>? Drcs { get; set; }
    public System.Collections.Generic.List<KurokoSubsTextCue> Cues { get; set; } = new();
}

// DRCS（番組ごとに字幕データで形を送ってくる外字）の形。Text の中では補助私用面の文字 Char で表す。
public sealed class KurokoSubsDrcs
{
    public string Char { get; set; } = "";        // "U+F0000" の形
    public int Width { get; set; }
    public int Height { get; set; }
    public int Depth { get; set; }                // 1 画素のビット数（1＝2 階調／2＝4 階調）
    public string Pattern { get; set; } = "";     // ARIB の DRCS パターンデータそのまま（base64）
}

public sealed class KurokoSubsTextCue
{
    public double StartMs { get; set; }
    public double EndMs { get; set; }
    public string? Writing { get; set; }          // null＝横書き。"vertical"＝縦書き
    public System.Collections.Generic.List<KurokoSubsTextLine>? Lines { get; set; }
    // lines を使わない簡単な形（§11）：表示する範囲と、文字の並び。SRT などの普通のテキスト字幕はこちらで書く。
    // 文字の中の改行（\n）は改行として描く。
    public KurokoSubsRegion? Region { get; set; }
    public System.Collections.Generic.List<KurokoSubsTextRun>? Runs { get; set; }
}

// 簡単な形の表示範囲。x/y/w/h は canvas の座標（canvas が無ければ映像の表示画素）。anchor は範囲の中のどこへ寄せるか
//（"bottom-center" など。縦は top/middle/bottom、横は left/center/right）。
public sealed class KurokoSubsRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public string Anchor { get; set; } = "bottom-center";
}

// 1 行：位置の連続する同じ高さの文字の並び。X/Y は先頭の文字の枠（セル）の左上（canvas 座標）。
public sealed class KurokoSubsTextLine
{
    public int X { get; set; }
    public int Y { get; set; }
    public System.Collections.Generic.List<KurokoSubsTextRun> Runs { get; set; } = new();
}

// 同じ書式の文字の並び。省略した項目は同じ行の直前の run の値を引き継ぐ（§11.1）。
public sealed class KurokoSubsTextRun
{
    public string Text { get; set; } = "";
    public int? W { get; set; }
    public int? H { get; set; }
    public int? Hs { get; set; }
    public int? Vs { get; set; }
    public string? Size { get; set; }             // normal / medium / small / tall / wide / double
    public string? Color { get; set; }
    public string? Bg { get; set; }
    public string? Edge { get; set; }
    public System.Collections.Generic.List<string>? Deco { get; set; } // underline / bold / italic
    public string? Box { get; set; }              // t b l r の組み合わせ
    public string? Flash { get; set; }            // normal / inverted
}

