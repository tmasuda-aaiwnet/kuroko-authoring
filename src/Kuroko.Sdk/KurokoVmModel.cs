namespace Kuroko;

// controller="dvd-video-vm" の **clean 形式**（version>=3）JSON モデル。一次仕様は kuroko-spec（spec/kuroko.ja.md）。
//
// 設計方針（none clean モデルと同型）：
//  ・title.json は**純粋に論理**（番号・言語・章・プレイリスト・論理↔物理の写像キー source のみ）。
//    メディア属性（コーデック・チャンネル数・解像度・ケイデンス）は持たない。
//  ・seg.json が**メディア属性の権威**。DVD は構成一定なので seg.json は none の KurokoNoneSegment を流用する
//    （映像＋音声スパン＋字幕。単一スパン）。
//  ・論理↔物理は 2 段解決：title.audio[number].source → seg.audio[source 一致].track。
//    DVD の物理サブストリーム（旧 KurokoAudio.Substream）が none の source に相当する。
//    非恒等 AST_CTL（AstCtl）は論理音声側に保持する。
//  ・時刻は ms（double）。読み出し時に KurokoTitle/KurokoSegment（エンジン内部モデル）へアダプトする際、
//    "c" 形式 TimeSpan 文字列へ変換する（AdaptVmTitle 参照）。
//
// ナビ非依存の既存クラス（KurokoVmCommand/KurokoPgcRef/KurokoMenuDomain/KurokoPgcCommands/KurokoProgram/
// KurokoRange）は KurokoModel.cs のものをそのまま流用する（ここでは再定義しない）。

// ── CONTROL/disc.json（dvd-video-vm・clean） ───────────────────────────────
public sealed class KurokoVmDisc
{
    public int Region { get; set; }
    // ディスク投入時に実行する PGC（DVD の FirstPlay 相当）。
    public KurokoPgcRef? FirstPlay { get; set; }
    // FirstPlay PGC の命令列（FPC）。bytecode を hex＋ニーモニックで持つ。
    public List<KurokoVmCommand> FirstPlayCommands { get; set; } = new();
    // ファーストプレイ（§6.1）。メディアプレーヤーとして再生するときの開始状態。上の FirstPlay（VM）とは別物。
    public KurokoFirstPlayback? FirstPlayback { get; set; }
    public List<KurokoVmTitleEntry> Titles { get; set; } = new();
    public List<KurokoMenuDomain> MenuDomains { get; set; } = new();
    // タイトルドメイン（VTS_PGCIT）の PGC 表（KurokoDisc と同キー。§「タイトルドメイン」）。旧 krk は空。
    public List<KurokoTitleDomain> TitleDomains { get; set; } = new();
}

public sealed class KurokoVmTitleEntry
{
    public int Id { get; set; }             // ディスク通し番号（/VIDEO/T{Id} に対応）
    public string? Name { get; set; }
    public int Vts { get; set; }
    public int VtsTitle { get; set; }
    public string? MenuDomain { get; set; } // 関連する VTSM ドメイン（"VTS01"…）
    public double DurationMs { get; set; }  // タイトルの長さ（version 5）
    public string? Duration { get; set; }   // 旧（version 4 以前）の長さ（"c" 形式）。読み出しのみ
    public string Video { get; set; } = ""; // 例 "VIDEO/T01"
}

// ── VIDEO/Tnn/title.json（dvd-video-vm・clean・論理） ──────────────────────
public sealed class KurokoVmTitle
{
    public List<KurokoVmAudioLogical> Audio { get; set; } = new();
    public List<KurokoVmSubtitleLogical> Subtitle { get; set; } = new();
    public List<KurokoVmChapter> Chapters { get; set; } = new();
    public List<KurokoVmPlaylistItem> Playlist { get; set; } = new();

    // ── アングル（§10.1 の angleType:"dvd"）────────────────────────────────
    // DVD-Video のマルチアングル。**アングルブロックのセルは cells[] に全アングルぶん載ったまま**で
    //（LinkCN・章・プログラムが参照するセル番号を PGC と 1:1 に保つため）、アングルごとに違うのは
    // playlist だけ。Angles があるときは Playlist は空。尺・章・音声・字幕はアングル間で共通
    //（規格上ブロック内の各アングルの尺は等しい）。
    public string? AngleType { get; set; }                  // "dvd"。未知の値ならアングル1のみ再生する
    public List<KurokoVmAngle> Angles { get; set; } = new();

    // このタイトルが実際に再生するプレイリスト。アングル番号（1始まり）を解決する。
    // アングルを持たない／種別が未知／番号が範囲外なら、既定（Playlist もしくはアングル1）へ落とす。
    public List<KurokoVmPlaylistItem> PlaylistForAngle(int angleNumber)
    {
        if (Angles.Count == 0 || !IsKnownAngleType) return Playlist;
        var a = Angles.FirstOrDefault(x => x.Number == angleNumber) ?? Angles[0];
        return a.Playlist;
    }

    // 選択できるアングル数（1＝アングル無し扱い）。
    // JsonIgnore：読み取り側の導出値で、ファイルに書くデータではない。
    [System.Text.Json.Serialization.JsonIgnore]
    public int AngleCount => Angles.Count > 0 && IsKnownAngleType ? Angles.Count : 1;

    // 再生器が解釈できるアングル種別か。未知の種別は「アングル無し」として扱う（前方互換の受け皿）。
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsKnownAngleType => string.Equals(AngleType, "dvd", System.StringComparison.OrdinalIgnoreCase);
    public List<KurokoProgram> Programs { get; set; } = new();  // 流用
    public List<KurokoVmCell> Cells { get; set; } = new();
    // サブピクチャ用 YUV パレット（16色。各要素 6桁hex）。
    public List<string> Palette { get; set; } = new();
    // PGC_UOP_CTL（禁止ユーザー操作、32bit）。
    public uint UopControl { get; set; }
    // タイトル PGC の命令（PRE/POST／セル命令）。
    public KurokoPgcCommands? PgcCommands { get; set; }
    public KurokoVmTiming Timing { get; set; } = new();
}

// 論理音声トラック（メディア属性は持たない。source で seg.audio へ解決）。
public sealed class KurokoVmAudioLogical
{
    // 論理番号。**version 5 は 1 始まり**（DVD の論理音声番号＋1）、旧（version 3・4）は DVD の論理番号そのもの（0 始まり）。
    // KurokoTitleSource.AdaptVmTitle が DVD の論理番号（KurokoAudio.DvdStream）へそろえる。
    public int Number { get; set; }
    public int Source { get; set; }         // 物理サブストリームID（seg.audio[].source と対応する安定キー）
    public string? Language { get; set; }
    public int AstCtl { get; set; } = -1;   // PGC の AST_CTL 実ストリーム番号（非恒等あり。取れなければ -1）
}

// 論理字幕トラック。表示モード別の物理サブピクチャIDを Modes に持つ（DVD の SPST_CTL 由来）。
public sealed class KurokoVmSubtitleLogical
{
    // 論理番号。version 5 は 1 始まり（DVD の論理サブピクチャ番号＋1）、旧は 0 始まり。音声と同じ。
    public int Number { get; set; }
    public string? Language { get; set; }
    public int Source { get; set; }         // 代表の物理サブストリームID
    public int SpstCtl { get; set; } = -1;
    public KurokoVmSubModes Modes { get; set; } = new();
}

// 表示モード別の**物理サブストリームID**（4:3ノーマル/16:9ワイド/レターボックス/パン&スキャン）。無ければ -1。
public sealed class KurokoVmSubModes
{
    public int Normal { get; set; } = -1;
    public int Wide { get; set; } = -1;
    public int Letterbox { get; set; } = -1;
    public int PanScan { get; set; } = -1;
}

public sealed class KurokoVmChapter
{
    public int Number { get; set; }
    public int StartCell { get; set; }      // 開始セル（0-based index）
    public double StartMs { get; set; }
}

// アングル1つ。違うのは playlist だけ（尺・章・音声・字幕・cells はタイトル側で共通）。
public sealed class KurokoVmAngle
{
    public int Number { get; set; }                         // 1 始まり
    public string? Name { get; set; }                       // 任意（DVD にアングル名のメタデータは無い）
    public List<KurokoVmPlaylistItem> Playlist { get; set; } = new();
}

// プレイリスト1要素（タイトルセル [titleCellStart..+cellCount) ＝ セグメント seg のセル [0..cellCount)）。
public sealed class KurokoVmPlaylistItem
{
    public string Seg { get; set; } = "";   // セグメントID（"S0001"）
    public int TitleCellStart { get; set; } // タイトルのセル index（0基準）でのこのセグメント開始
    public int CellCount { get; set; }
    public double StartMs { get; set; }     // タイトル通し時刻でのこのセグメント開始
}

public sealed class KurokoVmCell
{
    public int Index { get; set; }          // セル番号（0..）
    public double StartMs { get; set; }     // タイトル通し時刻での開始
    public int Still { get; set; }          // セル静止時間（秒）。0=なし、-1=無限（ユーザ入力待ち）
    public int CommandNr { get; set; }      // セル終端で実行するセル命令番号（1-based、0=なし）
    public string Seg { get; set; } = "";   // このセルの実体が属するセグメントID
    public int SegCell { get; set; }        // セグメント内でのセル index（0基準）
    // アングルブロック内での順番＝アングル番号（1始まり）。0＝ブロック外（全アングルで再生する）。
    // 同じブロックのセルは StartMs を共有する（どのアングルでも同じ区間なので）。
    public int Angle { get; set; }
}

public sealed class KurokoVmTiming
{
    public double TotalMs { get; set; }
    public string FrameRate { get; set; } = "30000/1001";
    public int[] CellBaseFrame { get; set; } = System.Array.Empty<int>(); // [cellCount+1]、末尾=総フレーム
    public int[] ChapterFrame { get; set; } = System.Array.Empty<int>();  // 章先頭の表示順フレーム番号
}
