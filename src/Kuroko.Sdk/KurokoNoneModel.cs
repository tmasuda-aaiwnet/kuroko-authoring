namespace Kuroko;

// controller="none"（プレーン再生）のクリーンな JSON モデル。一次仕様は kuroko-spec（spec/kuroko.ja.md） 第I部。
//
// 設計方針：
//  ・title.json は**純粋に論理**（番号・言語・章・番組・プレイリスト・論理↔物理の写像キー source のみ）。
//    メディア属性（コーデック・チャンネル数・dualMono・解像度・ケイデンス）は一切持たない。
//  ・seg.json が**メディア属性の権威**。各セグメントは単一構成（構成変化の境界で分割済み）。
//  ・論理↔物理は 2 段解決：title.audio[number].source → 選択中セグメントの seg.audio[source 一致].track。
//    source は全セグメントで不変な安定キー（TS 由来なら元ストリーム＝PID 相当。恒等なら number と同値）。
//
// DVD-video-vm 経路の DVD 由来モデル（KurokoModel.cs の KurokoTitle/KurokoSegment 等）とは独立に定義する。
// 時刻は ms（double）で持つ（DVD 側の "c" 形式 TimeSpan とは別。none は放送/配信寄りで ms 直が素直）。

// ── CONTROL/disc.json（none） ───────────────────────────────────────────
public sealed class KurokoNoneDisc
{
    public List<KurokoNoneTitleEntry> Titles { get; set; } = new();
    // ファーストプレイ（§6.1）。null＝未指定＝既定（先頭タイトル/アングル1/音声1/字幕なし）で始める。
    public KurokoFirstPlayback? FirstPlayback { get; set; }
}

// ── ファーストプレイ（§6.1・disc.json の firstPlayback） ────────────────
// **メディアプレーヤーとして開いたときの再生開始状態**。ナビゲーションを持たない再生の既定であって、
// §16 の firstPlay（VM の FirstPlay PGC＝起動ナビ）とは**別物**。
// 各項目は実体と食い違えばその項目だけ既定へ落とす（再生を断らない）。
public sealed class KurokoFirstPlayback
{
    public int Title { get; set; }          // titles[].id。0/未指定＝先頭のタイトル
    public int Angle { get; set; } = 1;     // アングル番号（1始まり。§10.1）
    public int Audio { get; set; } = 1;     // 論理音声番号（1始まり＝title.json の audio 並び）
    public int Subtitle { get; set; }       // 論理字幕番号（1始まり）。0＝字幕なし
}

public sealed class KurokoNoneTitleEntry
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public double DurationMs { get; set; }    // タイトルの長さ（version 5）
    public string Video { get; set; } = "";   // 例 "VIDEO/T01"
}

// ── VIDEO/Tnn/title.json（none・論理） ──────────────────────────────────
public sealed class KurokoNoneTitle
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public KurokoNoneTiming Timing { get; set; } = new();
    public List<KurokoNoneAudioLogical> Audio { get; set; } = new();
    public List<KurokoNoneSubtitleLogical> Subtitle { get; set; } = new();
    public List<KurokoNoneChapter> Chapters { get; set; } = new();
    public List<KurokoNoneEvent> Events { get; set; } = new();
    public List<KurokoNonePlaylistItem> Playlist { get; set; } = new();

    // ── アングル（§10.1）──────────────────────────────────────────────
    // 同じ内容の別バージョンを 1 タイトルに並べる。**Angles と Playlist は排他**で、
    // アングルを持つタイトルは Playlist が空。尺・章・音声・字幕はタイトルで共通（アングル間で同じ前提）。
    public string? AngleType { get; set; }                    // "bd"。未知の値ならアングル1のみ再生する
    public List<KurokoNoneAngle> Angles { get; set; } = new();

    // このタイトルが実際に再生するプレイリスト。アングル番号（1始まり）を解決する。
    // アングルを持たない／種別が未知／番号が範囲外なら、既定（Playlist もしくはアングル1）へ落とす。
    public List<KurokoNonePlaylistItem> PlaylistForAngle(int angleNumber)
    {
        if (Angles.Count == 0 || !IsKnownAngleType) return Playlist;
        var a = Angles.FirstOrDefault(x => x.Number == angleNumber) ?? Angles[0];
        return a.Playlist;
    }

    // 選択できるアングル数（1＝アングル無し扱い）。
    // JsonIgnore：これは読み取り側の**導出値**で、ファイルに書くデータではない
    //（書くと仕様に無いキーが title.json に混ざる）。
    [System.Text.Json.Serialization.JsonIgnore]
    public int AngleCount => Angles.Count > 0 && IsKnownAngleType ? Angles.Count : 1;

    // 再生器が解釈できるアングル種別か。未知の種別は「アングル無し」として扱う（前方互換の受け皿）。
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsKnownAngleType => string.Equals(AngleType, "bd", StringComparison.OrdinalIgnoreCase);
}

// アングル1つ。違うのは playlist だけ（尺・章・音声・字幕はタイトル側で共通）。
public sealed class KurokoNoneAngle
{
    public int Number { get; set; }                           // 1 始まり
    public string? Name { get; set; }                         // 任意（BD にアングル名のメタデータは無い）
    public List<KurokoNonePlaylistItem> Playlist { get; set; } = new();
}

// 論理音声トラック（メディア属性は持たない。source で seg.audio へ解決）。
public sealed class KurokoNoneAudioLogical
{
    public int Number { get; set; }             // 論理番号（1..）
    public string? Language { get; set; }       // 言語（ISO 639。EIT/PMT 由来。取れなければ null）
    public int Source { get; set; }             // 論理↔物理の安定キー（seg.audio[].source と対応）
}

// 論理字幕トラック。
public sealed class KurokoNoneSubtitleLogical
{
    public int Number { get; set; }
    public string Type { get; set; } = "text";  // "text" | "image"
    public string? Language { get; set; }
    public string? Name { get; set; }
    public int Source { get; set; }
}

public sealed class KurokoNoneChapter
{
    public int Number { get; set; }
    public double StartMs { get; set; }
    public string? Name { get; set; }
}

// 時刻付き番組メタ（EPG 相当）。再生位置に応じて表示名が変わる（放送/配信 TS の番組切替）。
public sealed class KurokoNoneEvent
{
    public double StartMs { get; set; }
    public double EndMs { get; set; }
    public string? Name { get; set; }
    public int EventId { get; set; }
    public string? StartTime { get; set; }      // 放送絶対時刻（ISO 8601。任意）
    public string? Desc { get; set; }
}

// プレイリスト1要素（タイトル通し時刻 startMs でセグメントを連結）。
public sealed class KurokoNonePlaylistItem
{
    public string Seg { get; set; } = "";       // セグメントID（"S0001"）
    public double StartMs { get; set; }         // タイトル通し時刻でのこのセグメント開始
}

public sealed class KurokoNoneTiming
{
    public double TotalMs { get; set; }
}

// ── SEGMENTS/S####/seg.json（none・メディア） ──────────────────────────
public sealed class KurokoNoneSegment
{
    public KurokoNoneVideo Video { get; set; } = new();
    // メディアの解像度パーツ（§8：1セグメント内で 24p=フル高 weave と 29.97i=半高 fields が混在する場合）。
    // 未設定/空＝単一 av.mkv（従来）。設定時は av.mkv ではなく各 parts[].file を順に連結して再生する。
    // 各パーツは自 av.mkv 内0基準 PTS。DurationMs はセグメント通し時刻での配置（連結オフセット）に使う。
    public List<KurokoNonePart>? Parts { get; set; }
    // 音声は「時刻スパンの時系列」。各スパンがその区間の音声メニュー全体（トラック集合）。ファイル途中で本数・名称・
    // デュアルモノが変わる（放送の番組切替等）のを表現する。再生側は現在位置を含むスパンの tracks をメニューにする。
    public List<KurokoNoneAudioSpan> Audio { get; set; } = new();
    public List<KurokoNoneSubtitle> Subtitle { get; set; } = new();
    public KurokoNoneTiming Timing { get; set; } = new();
}

// メディア解像度パーツ1つ（seg.json の parts[]）。file＝SEGMENTS/<id>/ 配下の相対名（例 "av.p00.mkv"）。
public sealed class KurokoNonePart
{
    public string File { get; set; } = "";        // 相対名（KurokoLayout.SegmentPartMkv 由来）
    public double DurationMs { get; set; }         // このパーツの尺（セグメント通し時刻での配置に使う）
    public int PixelHeight { get; set; }           // 参考（デコーダは自 mkv から読む）。fields=半高
    public string InterlaceMode { get; set; } = "weave"; // 参考："weave" | "fields"
    // このパーツの格納レイアウト（§8.1）。"dual"＝fields 2映像トラック(top/bottom)＋音声track2+／"single"＝映像1トラック＋音声track1+。
    // demux はこれでパーツ毎の映像トラック数・音声トラック基点を決める（混在の film=single / interlace=dual）。
    public string FieldLayout { get; set; } = "single";
    // 疎セル（静止画セル）を個別符号化したパーツ。true なら再生側は映像投入直後に即 Drain（§8.2）。
    public bool Still { get; set; }
}

// 音声構成スパン（seg 内ローカル ms 区間）。境界＝どれかのトラックの構成（本数・デュアルモノ・名称）が変わる点。
public sealed class KurokoNoneAudioSpan
{
    public double StartMs { get; set; }
    public double EndMs { get; set; }
    public List<KurokoNoneAudioTrack> Tracks { get; set; } = new();
}

// 映像メディア属性。ケイデンス（progressive/interlaced/soft-telecine）を明示。タイミングは av.mkv の容器 PTS に
// 焼き込み済みなので、cadence/interlacedRanges/tff は「weave か bob か・原状の表示尺」の判定にのみ使う。
public sealed class KurokoNoneVideo
{
    public string Codec { get; set; } = "h265";       // "h264" | "h265"
    public string Mode { get; set; } = "transcode";   // "transcode" | "copy"
    public int PixelWidth { get; set; }               // 符号化画素（SAR 前）
    public int PixelHeight { get; set; }
    public int DisplayWidth { get; set; }             // 表示画素（DAR 適用後）
    public int DisplayHeight { get; set; }
    public string Aspect { get; set; } = "16:9";      // "16:9" | "4:3"
    public string FrameRate { get; set; } = "30000/1001";
    public string Cadence { get; set; } = "progressive"; // "progressive" | "interlaced" | "soft-telecine"
    public bool Tff { get; set; } = true;             // フィールド順（bob 用）
    // インターレース維持の格納方式。旧krk未記載＝"weave"（後方互換）。"fields"＝T/B を各々「半高さ・倍フレーム」の
    // progressive フレームとして格納（この av.mkv 自体が半高さ・倍フレーム）。再生側は各半高フレームを1フィールドと
    // して全高へ展開＋bob。詳細は KurokoVideo.InterlaceMode / kuroko-spec（spec/kuroko.ja.md）。
    public string InterlaceMode { get; set; } = "weave"; // "weave" | "fields"
    // fields の格納レイアウト。"single"＝1トラックにT,B半高倍フレーム／"dual"＝2映像トラック(0=top,1=bottom)でパリティ=トラック。
    public string FieldLayout { get; set; } = "single";
    // bob する区間（本当のインターレース）。容器PTS(=実時間)の ms 範囲。film+29.97i 混在に対応するため区間で持つ。
    // 空/未設定なら全フレーム weave（純 film / プログレッシブ）。
    public List<KurokoRange>? InterlacedRanges { get; set; }
}

// 音声トラック1本の、あるスパン内の構成。
public sealed class KurokoNoneAudioTrack
{
    public int Track { get; set; }              // この av.mkv 内の実トラック番号（映像0の次以降）
    public int Source { get; set; }             // title.audio[].source と対応する安定キー
    public string Codec { get; set; } = "opus";
    public int Channels { get; set; } = 2;
    // このスパンでこのトラックがデュアルモノ（二か国語＝L主/R副）か。再生側は true で 主(L)/副(R)/主+副 を出し分ける。
    public bool DualMono { get; set; }
    // 放送の音声名称（ARIB の audio_component_descriptor 等）。あれば表示名に使う。無ければ null。
    public string? Name { get; set; }
    public string? Language { get; set; }
    // ラウドネス・メタデータ（§9.1）。音声本体はフルレンジのまま格納し、再生時に任意適用する。
    // null＝そのトラックには無い＝フルレンジ（JSON では省略される）。
    public KurokoLoudness? Loudness { get; set; }
}

// 音声トラックのラウドネス・メタデータ参照（§9.1）。seg.json の audio[].tracks[] に載る。
// エンベロープ本体は点数が多くなり得るので別ファイル（loudness/<source>.json）に置く。
public sealed class KurokoLoudness
{
    // ラウドネス・メタデータの型。値の意味と適用方法を決める拡張点。"ac3" / "dts"。
    // 再生側は知らない型を適用せず無視する（＝フルレンジ）。
    public string Type { get; set; } = "ac3";
    // 会話音量。型ごとに意味が異なるのでフィールドを分ける（§9.1.1／§9.1.2）。無ければ null（省略）。
    //   DialogLevelDb（type="ac3"）＝そのトラックの台詞レベル（0dBFS からの下がり幅。負値）。
    //     **測定値であってゲインではない**。適用は applied_dB = targetDb - DialogLevelDb。
    //   DialogGainDb（type="dts"）＝デコーダ出力へ**直接当てるゲイン**（負値。0=正規化しない）。
    public double? DialogLevelDb { get; set; }
    public double? DialogGainDb { get; set; }
    // ゲインエンベロープの実体（セグメント基準の相対パス）。無ければ null（省略）＝エンベロープ無し。
    public string? File { get; set; }
}

// ゲインエンベロープの実体ファイル（loudness/<source>.json）の中身（§9.1）。
public sealed class KurokoLoudnessFile
{
    public string Type { get; set; } = "ac3";   // seg.json 側と同じ型名（自己完結のため再掲）
    public KurokoDrc? Drc { get; set; }         // 主エンベロープ
    public KurokoDrc? DrcRf { get; set; }       // より強い圧縮を指示する第2エンベロープ（任意）
}

// ラウドネス・メタデータのゲインエンベロープ（§9.1）。
public sealed class KurokoDrc
{
    // エンベロープの由来と特性。type:"ac3" では "ac3-line"（通常の圧縮）/ "ac3-rf"（強い圧縮）。
    public string Mode { get; set; } = "ac3-line";
    // 変化点の列 [開始ms, ゲインdB]。各点のゲインは次の点まで一定（階段状）。
    // 時刻はそのトラックの音声先頭を 0 とする。連続する同値は 1 点にまとめる。
    public List<double[]> Points { get; set; } = new();
}

// 字幕メディア属性。
public sealed class KurokoNoneSubtitle
{
    public int Source { get; set; }
    public int Track { get; set; }
    public string Type { get; set; } = "text";  // "text" | "image"
}
