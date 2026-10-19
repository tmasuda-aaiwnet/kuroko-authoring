namespace Kuroko;

// Kuroko v3 メニュー形式のモデル。一次仕様は kuroko-spec（spec/kuroko.ja.md）。
// レイアウト：/MENU/<domain>/PGC<nn>/{ menu.json, C<nn>.mkv（映像+音声）, C<nn>.spu（SPU図柄・任意） }
// 目標＝DVD を直接読む再生とのメニュー再生パリティ（DvdPgc/DvdCell/DvdNavPci/SPST_CTL/palette/SPU を漏れなく運ぶ）。
// v2 の mono-mkv＋buttons.json はメニューページ着地が時刻シークの丸めで1ページずれ・SPU多重化前後でハイライトずれ
// を起こした。v3 はセル単位にファイルを割り、シーク/丸め/SPU多重化を構造的に排除する（静止ページ=Cell=VOBU=1フレーム、
// 連続モーション=複数VOBU/セルは playlist 連結で継ぎ目なし）。
public sealed class KurokoMenuV3
{
    // menu.json の版。レイアウト version 5 からはレイアウトと同じ値（5）を書く。読み手の解釈はマニフェストの version で決める。
    public int Version { get; set; } = KurokoLayout.CleanLayoutVersion;
    public string Domain { get; set; } = "";        // "VMGM" / "VTS01" ...
    public int Pgc { get; set; }
    // DvdMenuType（Title=2/Root=3/Subpicture=4/Audio=5/Angle=6/Chapter=7）。JSON は名前の文字列（version 5。旧は数値も読む）。
    [System.Text.Json.Serialization.JsonConverter(typeof(KurokoMenuTypeConverter))]
    public int MenuType { get; set; }
    public uint UopControl { get; set; }            // PGC_UOP_CTL（禁止操作。再生側は VOBU UOP と OR）
    public int NextPgc { get; set; }
    public int PrevPgc { get; set; }
    public int GoUpPgc { get; set; }
    // SPU用 YUV パレット（16色）。JSON は 6 桁の 16 進の文字列（version 5。旧は数値も読む）。
    [System.Text.Json.Serialization.JsonConverter(typeof(KurokoHexPaletteListConverter))]
    public List<uint> Palette { get; set; } = new();
    public KurokoMenuV3Video Video { get; set; } = new();
    public List<KurokoMenuV3Audio> Audio { get; set; } = new();     // AST_CTL（利用可否＋論理番号）
    public List<KurokoMenuV3SpstCtl> SpstCtl { get; set; } = new(); // SPST_CTL（論理→物理×表示4モード）
    public KurokoMenuV3Commands Commands { get; set; } = new();     // PGC の PRE/POST/CELL コマンド
    // program→開始セル。**JSON は 0 始まり（version 5）**、旧（version 3・4）は 1 始まり。読み出し時に
    // KurokoTitleSource.ReadMenuV3 が内部表現（1 始まり＝DVD の IFO と同じ。エンジンが -1 して使う）へそろえる。
    public List<int> ProgramMap { get; set; } = new();
    public List<KurokoMenuV3Cell> Cells { get; set; } = new();
}

public sealed class KurokoMenuV3Video
{
    public string Codec { get; set; } = "h264";     // "h264" / "h265"
    public int Width { get; set; }
    public int Height { get; set; }
    public string Aspect { get; set; } = "4:3";     // "4:3" / "16:9"
    public bool Interlaced { get; set; }
    public bool Tff { get; set; }
}

public sealed class KurokoMenuV3Audio
{
    public bool Available { get; set; }
    public int Number { get; set; }                 // 論理音声番号
    public string Codec { get; set; } = "opus";     // "opus" / "ac3"
}

// SPST_CTL：論理サブピクチャ番号ごとに、表示モード別（4:3/ワイド/レターボックス/パン&スキャン）の物理番号。
public sealed class KurokoMenuV3SpstCtl
{
    public bool Available { get; set; }
    public int N43 { get; set; }
    public int Wide { get; set; }
    public int Lb { get; set; }
    public int Ps { get; set; }
}

public sealed class KurokoMenuV3Commands
{
    // §20「VM 命令の JSON 表現」に従い各命令は { hex, asm }（KurokoVmCommand）で持つ。
    public List<KurokoVmCommand> Pre { get; set; } = new();
    public List<KurokoVmCommand> Post { get; set; } = new();
    public List<KurokoVmCommand> Cell { get; set; } = new();
}

public sealed class KurokoMenuV3Cell
{
    public int Index { get; set; }
    public int Still { get; set; }                  // 0=なし / 1..254=有限秒 / -1=無限（0xFF）
    public int CellCommand { get; set; }            // セル終端で実行する CELL 命令番号（1-based、0=なし）
    public double DurationMs { get; set; }          // このセルの C<nn>.mkv 実尺(ms・焼込みPTS基準)。playlist offset とセル判定に使う
    public bool SeamlessNext { get; set; }          // C_CAT のシームレス連続フラグ（次セルと継ぎ目なし＝playlist連結対象）
    public bool Static { get; set; }                // 実フレーム1枚の静止セル（音声つき単フレーム含む）。再生側は bob せず weave（全解像度）

    // 映像ケイデンス（seg.json の video.cadence と同じ語彙）。人が読んで分かるための記録で、判定の実体は
    // InterlacedRanges。"interlaced"（全編実インターレース）/"film"（3:2 ソフトテレシネ）/"progressive"/"mixed"。
    public string? Cadence { get; set; }

    // **本当にインターレースな区間だけ**を bob するための区間列（タイトル＝seg.json の video.interlacedRanges と同じ
    // 考え方）。時刻は**このセル内のローカル ms**（C<nn>.mkv の PTS 基準）。再生側は playlist のオフセットを足す。
    //   ・null      ＝この情報を持たない**旧 krk**。再生側は従来どおり menu.video.interlaced で PGC 単位に判定する。
    //   ・空リスト  ＝bob する区間なし（film/progressive セル）。
    // メニューを常に全 bob していたため、フィルム由来のモーションメニューでプログレッシブのコマを半解像度の
    // フィールドへ分解してしまい、ちらついていた（市販ディスクのメニューで起きた）。
    public List<KurokoRange>? InterlacedRanges { get; set; }

    public string Av { get; set; } = "";            // このセルの映像+音声 mkv（例 "C01.mkv"）
    public string Spu { get; set; } = "";           // このセルの SPU 図柄ファイル（例 "C01.spu"。無ければ空）
    public List<KurokoMenuV3Pci> Pci { get; set; } = new(); // per-VOBU の時刻付き PCI（静止は要素1個）
}

// NAV PCI（1 VOBU 区間）のハイライト情報。fromMs..toMs の間だけ有効。
public sealed class KurokoMenuV3Pci
{
    public double FromMs { get; set; }
    public double ToMs { get; set; }
    public int HighlightStatus { get; set; }        // HLI_SS（0=ハイライト無し）
    public uint VobuUop { get; set; }               // このVOBU区間の禁止操作
    public int ForcedSelect { get; set; }           // FOSL_BTNN（0=なし）
    public int ForcedActivate { get; set; }         // FOAC_BTNN
    public List<KurokoMenuV3Button> Buttons { get; set; } = new();
    public List<KurokoMenuV3ColorGroup> ColorGroups { get; set; } = new(); // BTN_COLIT（最大3組）
}

public sealed class KurokoMenuV3Button
{
    public int Number { get; set; }                 // 1-based
    public int X0 { get; set; }
    public int Y0 { get; set; }
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int ColorGroup { get; set; }             // ColorGroups のどれを使うか（0..2）
    public int AutoAction { get; set; }             // 0以外なら選択即実行
    public int Up { get; set; }
    public int Down { get; set; }
    public int Left { get; set; }
    public int Right { get; set; }
    // ボタン命令。§20「VM 命令の JSON 表現」に従い { hex, asm }（KurokoVmCommand）で持つ。
    public KurokoVmCommand Command { get; set; } = new();
}

// ボタン色組（選択/動作の色＝パレット索引、コントラスト＝α。各4画素タイプ）。
public sealed class KurokoMenuV3ColorGroup
{
    public List<int> SelectColor { get; set; } = new();
    public List<int> SelectContrast { get; set; } = new();
    public List<int> ActionColor { get; set; } = new();
    public List<int> ActionContrast { get; set; } = new();
}
