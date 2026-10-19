using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Kuroko;

// .krk（Kuroko）を開き、再生に必要な構造（manifest/disc/title）と、タイトルの av.mkv を
// デマルチプレクサとして供給するファサード。再生エンジンはこの上に乗る。
//   ・KurokoContainerReader をソース生存中ずっと開いておく。
//   ・OpenTitleMedia は av.mkv を KurokoContainerReader.OpenRead（専用ハンドルの BoundedStream）で開き、
//     KurokoMkvDemuxer に渡す（demuxer が Stream を所有・破棄）。複数同時オープン可。
public sealed class KurokoTitleSource : IDisposable
{
    private readonly KurokoContainerReader _iso;
    private readonly bool _isNone;
    private readonly bool _isCleanVm;

    public KurokoManifest Manifest { get; }

    // 中身（ZIP）を読む口。映像を開く処理（再生器側。ffmpeg を使うデマルチプレクサ）はこの SDK の外にあり、ここから読む。
    public KurokoContainerReader Container => _iso;
    public KurokoDisc Disc { get; }

    // controller="dvd-video-vm" かつ version>=3 の **clean 形式**ディスク（KurokoVmDisc）。旧 vm（version<=2）/none は null。
    // clean vm も共通の再生エンジンへ渡すため、Disc（KurokoDisc）は disc.json の共通フィールドで従来どおり埋める。
    // これは Phase ②（VM 配線）向けの純論理ディスク像を保持しておくためのもの。
    public KurokoVmDisc? VmDisc { get; }

    // controller="none"（プレーン再生）か。none は clean モデル（KurokoNoneModel）で書かれるため、読み出し時に
    // エンジンが使う DVD 風モデル（KurokoTitle/KurokoSegment）へアダプトする（再生エンジンを共通化）。
    // controller="kuroko-tiny" もタイトルは none と同じ形なので true（メニューは KurokoTinyPackage で読む）。
    public bool IsNone => _isNone;

    // controller="kuroko-tiny"（nav.json・MENU/<menuId>/menu.json）か。
    public bool IsTiny => string.Equals(Manifest.Controller, KurokoLayout.ControllerKurokoTiny, StringComparison.OrdinalIgnoreCase);

    // controller="dvd-video-vm" の clean 形式（version>=3）か。旧 vm（version<=2）と none は false。
    public bool IsCleanVm => _isCleanVm;

    // レイアウト version 5（公開仕様 1.0）の形か。番号は 1 始まり・セルの位置は 0 始まり・時刻は ms・言語は 3 文字。
    // 4 以前とは JSON の意味が違う項目があるので、読み出しでここを見て内部表現（エンジンが使う形）へそろえる。
    private bool IsV5 => Manifest.Version >= KurokoLayout.CleanLayoutVersion;

    private KurokoTitleSource(KurokoContainerReader iso, KurokoManifest manifest, KurokoDisc disc, KurokoVmDisc? vmDisc)
    {
        _iso = iso;
        Manifest = manifest;
        Disc = disc;
        VmDisc = vmDisc;
        _isNone = string.Equals(manifest.Controller, KurokoLayout.ControllerNone, StringComparison.OrdinalIgnoreCase)
                  || string.Equals(manifest.Controller, KurokoLayout.ControllerKurokoTiny, StringComparison.OrdinalIgnoreCase);
        _isCleanVm = string.Equals(manifest.Controller, KurokoLayout.ControllerDvdVideoVm, StringComparison.OrdinalIgnoreCase)
                     && manifest.Version >= 3;
    }

    // .krk を開く。Kuroko でない/controller 未対応なら例外。
    public static KurokoTitleSource Open(string krkPath)
    {
        var iso = KurokoContainerReader.Open(krkPath);
        try
        {
            var manifest = KurokoJson.Deserialize<KurokoManifest>(ReadFile(iso, KurokoLayout.ManifestFile))
                ?? throw new InvalidDataException("KUROKO.MANIFEST を解析できません。");
            if (!manifest.IsKuroko)
                throw new InvalidDataException($"Kuroko 形式ではありません（format={manifest.Format}）。");
            if (!string.Equals(manifest.Controller, KurokoLayout.ControllerDvdVideoVm, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(manifest.Controller, KurokoLayout.ControllerNone, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(manifest.Controller, KurokoLayout.ControllerKurokoTiny, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"未対応の controller です（{manifest.Controller}）。");

            var discBytes = ReadFile(iso, KurokoLayout.DiscJson);
            var disc = KurokoJson.Deserialize<KurokoDisc>(discBytes)
                ?? throw new InvalidDataException("CONTROL/disc.json を解析できません。");

            // controller と version で経路判定：dvd-video-vm && version>=3 は clean vm（新形式）、
            // version<=2 は旧 vm、none は none。旧 vm/none は従来経路を一切変えない。
            KurokoVmDisc? vmDisc = null;
            if (string.Equals(manifest.Controller, KurokoLayout.ControllerDvdVideoVm, StringComparison.OrdinalIgnoreCase)
                && manifest.Version >= 3)
                vmDisc = KurokoJson.Deserialize<KurokoVmDisc>(discBytes)
                    ?? throw new InvalidDataException("CONTROL/disc.json（clean）を解析できません。");

            return new KurokoTitleSource(iso, manifest, disc, vmDisc);
        }
        catch
        {
            iso.Dispose();
            throw;
        }
    }

    // angle＝アングル番号（1始まり。§10.1）。アングルを持たないタイトルでは無視される。
    public KurokoTitle ReadTitle(int titleId, int angle = 1)
    {
        if (_isNone) return AdaptNoneTitle(ReadNoneTitle(titleId), angle);
        if (_isCleanVm) return AdaptVmTitle(ReadVmTitle(titleId), angle);
        var t = KurokoJson.Deserialize<KurokoTitle>(ReadTitleJson(titleId))
            ?? throw new InvalidDataException($"VIDEO/T{titleId:00}/title.json を解析できません。");
        // 旧 vm（version 2 以前）：形式上の番号は「並びの何番目か」（ファーストプレイの指し方と同じ）。
        for (int i = 0; i < t.Audio.Count; i++) t.Audio[i].Number = i + 1;
        for (int i = 0; i < t.Subtitle.Count; i++) t.Subtitle[i].Number = i + 1;
        return t;
    }

    // タイトルのアングル情報（§10.1）。アングルが無ければ Count=1・Names 空。
    // none は angleType:"bd"（アングルごとに別プレイリスト）、clean vm は angleType:"dvd"
    //（アングルブロックのセルが cells[] に全ぶん載り、playlist だけがアングル別）。
    public (int Count, string? Type, IReadOnlyList<string?> Names) ReadTitleAngles(int titleId)
    {
        try
        {
            if (_isNone)
            {
                var nt = ReadNoneTitle(titleId);
                return nt.AngleCount <= 1
                    ? (1, nt.AngleType, Array.Empty<string?>())
                    : (nt.AngleCount, nt.AngleType, nt.Angles.OrderBy(a => a.Number).Select(a => a.Name).ToList());
            }
            if (_isCleanVm)
            {
                var vt = ReadVmTitle(titleId);
                return vt.AngleCount <= 1
                    ? (1, vt.AngleType, Array.Empty<string?>())
                    : (vt.AngleCount, vt.AngleType, vt.Angles.OrderBy(a => a.Number).Select(a => a.Name).ToList());
            }
        }
        catch { }
        return (1, null, Array.Empty<string?>());
    }

    // セグメント（v2）の seg.json を読む。none/clean vm は seg.json が clean モデル（KurokoNoneSegment）なので共通アダプト。
    public KurokoSegment ReadSegment(string id)
        => (_isNone || _isCleanVm)
            ? AdaptSegment(ReadNoneSegment(id), _isCleanVm)
            : KurokoJson.Deserialize<KurokoSegment>(ReadFile(_iso, KurokoLayout.SegmentJson(id)))
              ?? throw new InvalidDataException($"SEGMENTS/{id}/seg.json を解析できません。");

    // 字幕（§11）：subs/img/<source>/index.json（画像型 cue 列）を読む。無ければ null。
    public KurokoSubsImage? ReadSubsImage(string segId, int source)
    {
        if (_iso.FindEntry(KurokoLayout.SubsImageIndex(segId, source)) is not KrkEntry e) return null;
        try { return KurokoJson.Deserialize<KurokoSubsImage>(_iso.ReadAllBytes(e)); } catch { return null; }
    }

    // 字幕（§11・§11.1）：subs/text/<source>.json（テキスト型。放送 TS の ARIB 字幕など）を読む。無ければ null。
    public KurokoSubsText? ReadSubsText(string segId, int source)
    {
        if (_iso.FindEntry(KurokoLayout.SubsTextJson(segId, source)) is not KrkEntry e) return null;
        try { return KurokoJson.Deserialize<KurokoSubsText>(_iso.ReadAllBytes(e)); } catch { return null; }
    }

    // 字幕 cue の PNG バイト列を読む（subs/img/<source>/<name>）。無ければ null。
    public byte[]? ReadSubsImagePng(string segId, int source, string name)
        => _iso.FindEntry(KurokoLayout.SubsImagePng(segId, source, name)) is KrkEntry e ? _iso.ReadAllBytes(e) : null;

    // 字幕 cue の実体を**まとめて読む**（subs/img/<source>/ 配下。名前→中身）。cue は連続して置かれているので、
    // 1本ずつ読むとファイル共有越しでは往復が本数ぶん発生する（例：千数百 cue で十数秒）。
    public Dictionary<string, byte[]> ReadSubsImageFiles(string segId, int source, IReadOnlyCollection<string> names)
    {
        var entries = new List<KrkEntry>(names.Count);
        var pathOf = new Dictionary<string, string>(names.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            if (string.IsNullOrEmpty(n)) continue;
            string path = KurokoLayout.SubsImagePng(segId, source, n);
            if (_iso.FindEntryForBatch(path) is not KrkEntry e) continue;
            entries.Add(e);
            pathOf[e.Name.Replace("\\", "/").TrimStart('/')] = n;
        }
        var read = _iso.ReadAllBytesBatch(entries);
        var result = new Dictionary<string, byte[]>(read.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in read)
            if (pathOf.TryGetValue(kv.Key, out var name)) result[name] = kv.Value;
        return result;
    }

    // ── controller="none"（clean モデル）の読み出し・アダプト ─────────────────────────
    private KurokoNoneTitle ReadNoneTitle(int titleId)
        => KurokoJson.Deserialize<KurokoNoneTitle>(ReadTitleJson(titleId))
           ?? throw new InvalidDataException($"VIDEO/T{titleId:00}/title.json を解析できません。");

    private KurokoNoneSegment ReadNoneSegment(string id)
        => KurokoJson.Deserialize<KurokoNoneSegment>(_segJsonCache.TryGetValue(id, out var cached) ? cached : ReadFile(_iso, KurokoLayout.SegmentJson(id)))
           ?? throw new InvalidDataException($"SEGMENTS/{id}/seg.json を解析できません。");

    // ── controller="dvd-video-vm" clean 形式（version>=3）の読み出し・アダプト ─────────────
    private KurokoVmTitle ReadVmTitle(int titleId)
        => KurokoJson.Deserialize<KurokoVmTitle>(ReadTitleJson(titleId))
           ?? throw new InvalidDataException($"VIDEO/T{titleId:00}/title.json を解析できません。");

    // clean vm の純論理 title.json（KurokoVmTitle）を、エンジン内部モデル（KurokoTitle）へアダプトする。
    // AdaptNoneTitle と同型：メディア属性（映像・音声コーデック/チャンネル/正準トラック・字幕実トラック）は先頭
    // セグメント（seg.json＝KurokoNoneSegment）から source（＝DVD 物理サブストリームID）で解決する。VM/UOP/PGC命令・
    // パレット・章・プログラム・タイミングは title 側の値をそのまま写す。DVD の非恒等 AST_CTL は KurokoAudio.AstCtl に保持。
    private KurokoTitle AdaptVmTitle(KurokoVmTitle vt, int angle)
    {
        // アングル（§10.1 angleType:"dvd"）：選択中アングルのプレイリストで組み立てる。
        // cells[] は PGC セルと 1:1（全アングルぶん）なので、このアングルが再生しないセルは
        // プレイリストのどのセグメントにも入らない＝再生対象にならない。章・LinkCN が別アングルのセルを
        // 指していても、同じブロックのセルは開始時刻を共有しているので正しい時刻に着地する。
        var playlist = vt.PlaylistForAngle(angle);
        var seg0 = playlist.Count > 0 ? ReadNoneSegment(playlist[0].Seg) : new KurokoNoneSegment();
        var t = new KurokoTitle
        {
            Video = AdaptVideo(seg0.Video),
            Palette = vt.Palette,
            UopControl = vt.UopControl,
            PgcCommands = vt.PgcCommands,
            Timing = new KurokoTiming
            {
                FrameRate = vt.Timing.FrameRate,
                TotalMs = vt.Timing.TotalMs,
                CellBaseFrame = vt.Timing.CellBaseFrame,
                ChapterFrame = vt.Timing.ChapterFrame,
            },
        };
        // プログラムの開始セル：version 5 は 0 始まり、内部（DvdVmNavigator）は IFO と同じ 1 始まり。
        foreach (var p in vt.Programs)
            t.Programs.Add(IsV5 ? new KurokoProgram { Pgn = p.Pgn, EntryCell = p.EntryCell + 1 } : p);

        // 論理音声：number/source（＝物理サブストリームID）→ DVD 風（DvdStream=論理番号, Substream=source, MkvTrack=正準）。
        // codec/channels/正準トラックは先頭セグメントの音声スパン union から source で引く（none と同じ）。
        var srcTrack = new Dictionary<int, KurokoNoneAudioTrack>();
        foreach (var sp in seg0.Audio) foreach (var tr in sp.Tracks) srcTrack.TryAdd(tr.Source, tr);
        for (int i = 0; i < vt.Audio.Count; i++)
        {
            var la = vt.Audio[i];
            srcTrack.TryGetValue(la.Source, out var sa);
            t.Audio.Add(new KurokoAudio
            {
                // DvdStream は DVD の論理音声番号（0 始まり。VM の SPRM[1] と照合する）。version 5 の number は 1 始まり。
                DvdStream = IsV5 ? la.Number - 1 : la.Number,
                Number = IsV5 ? la.Number : i + 1,
                Substream = la.Source, MkvTrack = sa?.Track ?? (1 + i),
                Codec = sa?.Codec ?? "opus", Channels = sa?.Channels ?? 2,
                Language = KurokoLanguage.Normalize(la.Language), AstCtl = la.AstCtl,
                DualMonoRanges = null,
            });
        }

        // 論理字幕：表示モード別の物理ID（Modes）を Phys** に、代表を Substream/DvdStream/SpstCtl に。正準トラック
        // Track** は seg（物理→実トラック）で解決したものを写す（seg で解決するもの＝seg0 の substream 一致トラック）。
        int SegSubTrack(int physId) => physId >= 0
            ? (seg0.Subtitle.FirstOrDefault(s => s.Source == physId)?.Track ?? -1) : -1;
        for (int si = 0; si < vt.Subtitle.Count; si++)
        {
            var ls = vt.Subtitle[si];
            var m = ls.Modes;
            t.Subtitle.Add(new KurokoSubtitle
            {
                DvdStream = IsV5 ? ls.Number - 1 : ls.Number,
                Number = IsV5 ? ls.Number : si + 1,
                Substream = ls.Source, SpstCtl = ls.SpstCtl, Language = KurokoLanguage.Normalize(ls.Language),
                Phys43 = m.Normal, PhysWide = m.Wide, PhysLetterbox = m.Letterbox, PhysPanScan = m.PanScan,
                Track43 = SegSubTrack(m.Normal), TrackWide = SegSubTrack(m.Wide),
                TrackLetterbox = SegSubTrack(m.Letterbox), TrackPanScan = SegSubTrack(m.PanScan),
            });
        }

        // プレイリスト → Segments（連結 demux）。
        foreach (var pi in playlist)
            t.Segments.Add(new KurokoSegmentRef
            {
                Id = pi.Seg, TitleCellStart = pi.TitleCellStart, CellCount = pi.CellCount,
                StartTime = KurokoTime.Format(TimeSpan.FromMilliseconds(pi.StartMs)),
            });

        // セル（ms→"c"）。Duration は次セル開始（無ければ総尺）との差。
        // ただし**アングルブロックの兄弟セルは飛ばす**（§17.1）。同じブロックのセルは「同じ区間の別アングル」で
        // startMs を共有するため、素直に次セルとの差を取るとアングル1のセルが尺 0 になる。
        // ブロック内は angle が 1,2,… と続くので、そこが切れるところがブロックの終わり。
        for (int i = 0; i < vt.Cells.Count; i++)
        {
            var c = vt.Cells[i];
            int next = i + 1;
            if (c.Angle >= 1)
                while (next < vt.Cells.Count && vt.Cells[next].Angle == vt.Cells[next - 1].Angle + 1) next++;
            double dur = (next < vt.Cells.Count ? vt.Cells[next].StartMs : vt.Timing.TotalMs) - c.StartMs;
            if (dur < 0) dur = 0;
            t.Cells.Add(new KurokoCell
            {
                Index = c.Index, StartTime = KurokoTime.Format(TimeSpan.FromMilliseconds(c.StartMs)),
                Duration = KurokoTime.Format(TimeSpan.FromMilliseconds(dur)),
                Still = c.Still, CommandNr = c.CommandNr,
                Segment = c.Seg, SegCell = c.SegCell,
                Angle = c.Angle,
            });
        }

        foreach (var ch in vt.Chapters)
            t.Chapters.Add(new KurokoChapter
            {
                Number = ch.Number, StartCell = ch.StartCell,
                StartTime = KurokoTime.Format(TimeSpan.FromMilliseconds(ch.StartMs)),
            });

        return t;
    }

    // 番組（events）。title.json の events は title 通し時刻。再生位置に応じた番組名表示に使う。
    public List<KurokoNoneEvent> ReadNoneEvents(int titleId) => ReadNoneTitle(titleId).Events;

    // 音声構成スパン（title 通し時刻）。各セグメントの seg.audio スパンをプレイリスト開始時刻ぶん平行移動して連結。
    // 再生エンジンが「現在位置の音声メニュー（トラック集合・主/副・名称）」を引くために使う。
    public List<KurokoNoneAudioSpan> ReadNoneAudioSpans(int titleId, int angle = 1)
    {
        var nt = ReadNoneTitle(titleId);
        var outp = new List<KurokoNoneAudioSpan>();
        foreach (var pi in nt.PlaylistForAngle(angle))
        {
            KurokoNoneSegment ns;
            try { ns = ReadNoneSegment(pi.Seg); } catch { continue; }
            foreach (var sp in ns.Audio)
                outp.Add(new KurokoNoneAudioSpan
                {
                    StartMs = sp.StartMs + pi.StartMs,
                    EndMs = sp.EndMs + pi.StartMs,
                    Tracks = sp.Tracks,
                });
        }
        return outp;
    }

    private static KurokoVideo AdaptVideo(KurokoNoneVideo v) => new KurokoVideo
    {
        Codec = v.Codec,
        Width = v.PixelWidth, Height = v.PixelHeight,
        Aspect = v.Aspect == "4:3" ? "4:3" : "16:9",
        Fps = v.FrameRate,
        Interlaced = string.Equals(v.Cadence, "interlaced", StringComparison.OrdinalIgnoreCase),
        Tff = v.Tff,
        InterlaceMode = string.IsNullOrEmpty(v.InterlaceMode) ? "weave" : v.InterlaceMode,
        FieldLayout = string.IsNullOrEmpty(v.FieldLayout) ? "single" : v.FieldLayout,
        InterlacedRanges = v.InterlacedRanges,
    };

    private static KurokoSegment AdaptSegment(KurokoNoneSegment ns, bool includeSubtitles)
    {
        var s = new KurokoSegment
        {
            Video = AdaptVideo(ns.Video),
            Timing = new KurokoTiming { FrameRate = ns.Video.FrameRate, TotalMs = ns.Timing.TotalMs },
        };
        // demux/TrackMap 用に、スパン横断の音声トラック union（track/source）を作る。デュアルモノ/名称はメニュー側で扱う。
        var seenTr = new SortedDictionary<int, KurokoNoneAudioTrack>();
        foreach (var sp in ns.Audio)
            foreach (var tr in sp.Tracks)
                if (!seenTr.ContainsKey(tr.Track)) seenTr[tr.Track] = tr;
        foreach (var tr in seenTr.Values)
            s.Audio.Add(new KurokoAudio
            {
                Substream = tr.Source, MkvTrack = tr.Track,
                Codec = tr.Codec, Channels = tr.Channels,
                DualMonoRanges = null,
            });
        // 字幕（clean vm の SPU）：物理ID→実トラックの解決に使う（BuildTrackMap の SegTrackOf が seg.Subtitle を引く）。
        // clean vm 限定で埋める（none は現状字幕写像を持たず、seg.Subtitle を触らない＝挙動を確実に非改変にする）。
        if (includeSubtitles)
            foreach (var sub in ns.Subtitle)
                s.Subtitle.Add(new KurokoSubtitle { Substream = sub.Source, MkvTrack = sub.Track });
        s.Cells.Add(new KurokoSegCell { StartInSegment = "00:00:00", Duration = KurokoTime.Format(TimeSpan.FromMilliseconds(ns.Timing.TotalMs)) });
        if (ns.Parts is { Count: > 0 })
            s.Parts = ns.Parts.Select(p => new KurokoSegmentPart
            {
                File = p.File, DurationMs = p.DurationMs,
                PixelHeight = p.PixelHeight, InterlaceMode = p.InterlaceMode, FieldLayout = p.FieldLayout,
                Still = p.Still, // §8.2 疎セル（静止画）パーツ。落とすと再生側の即 Drain が発火しない
            }).ToList();
        return s;
    }

    private KurokoTitle AdaptNoneTitle(KurokoNoneTitle nt, int angle)
    {
        // アングルを持つタイトルは、選択中アングルのプレイリストで組み立てる（§10.1）。
        // 尺・章・音声・字幕はアングル間で共通なので、ここ以外はアングルに依存しない。
        var playlist = nt.PlaylistForAngle(angle);
        var seg0 = playlist.Count > 0 ? ReadNoneSegment(playlist[0].Seg) : new KurokoNoneSegment();
        var t = new KurokoTitle
        {
            Video = AdaptVideo(seg0.Video),
            Timing = new KurokoTiming { FrameRate = seg0.Video.FrameRate, TotalMs = nt.Timing.TotalMs },
        };
        // 論理音声：clean title の number/source → DVD 風（DvdStream=論理番号, Substream=source, MkvTrack=正準）。
        // codec/channels/正準トラックは先頭セグメントの音声スパンの union から source で引く。
        var srcTrack = new Dictionary<int, KurokoNoneAudioTrack>();
        foreach (var sp in seg0.Audio) foreach (var tr in sp.Tracks) srcTrack.TryAdd(tr.Source, tr);
        for (int i = 0; i < nt.Audio.Count; i++)
        {
            var la = nt.Audio[i];
            srcTrack.TryGetValue(la.Source, out var sa);
            t.Audio.Add(new KurokoAudio
            {
                DvdStream = la.Number, Number = IsV5 ? la.Number : i + 1,
                Substream = la.Source, MkvTrack = sa?.Track ?? (1 + i),
                Codec = sa?.Codec ?? "opus", Channels = sa?.Channels ?? 2, Language = KurokoLanguage.Normalize(la.Language),
                DualMonoRanges = null,
            });
        }
        // 論理字幕：clean title の number/source → DVD 風。BD/TS 由来には表示モード別の変種（4:3/ワイド/
        // レターボックス/パン&スキャン）が無いので、Phys** は全て同じ source を指す（SubtitlePhysForAspect が
        // どれを選んでも同じ物理へ行き着く）。実体は subs/img/<source>/ にある。
        for (int i = 0; i < nt.Subtitle.Count; i++)
        {
            var ls = nt.Subtitle[i];
            t.Subtitle.Add(new KurokoSubtitle
            {
                DvdStream = ls.Number, Number = IsV5 ? ls.Number : i + 1,
                Substream = ls.Source, Language = KurokoLanguage.Normalize(ls.Language),
                Phys43 = ls.Source, PhysWide = ls.Source, PhysLetterbox = ls.Source, PhysPanScan = ls.Source,
                MkvTrack = seg0.Subtitle.FirstOrDefault(s => s.Source == ls.Source)?.Track ?? -1,
            });
        }
        // プレイリスト → Segments（プレイリスト連結 demux）＋ Cells（1 セグメント=1 セル）。
        for (int si = 0; si < playlist.Count; si++)
        {
            var pi = playlist[si];
            double dur = (si + 1 < playlist.Count ? playlist[si + 1].StartMs : nt.Timing.TotalMs) - pi.StartMs;
            string st = KurokoTime.Format(TimeSpan.FromMilliseconds(pi.StartMs));
            t.Segments.Add(new KurokoSegmentRef { Id = pi.Seg, TitleCellStart = si, CellCount = 1, StartTime = st });
            t.Cells.Add(new KurokoCell { Index = si, StartTime = st, Segment = pi.Seg, SegCell = 0, Duration = KurokoTime.Format(TimeSpan.FromMilliseconds(dur)) });
        }
        foreach (var ch in nt.Chapters)
            t.Chapters.Add(new KurokoChapter { Number = ch.Number, StartCell = 0, StartTime = KurokoTime.Format(TimeSpan.FromMilliseconds(ch.StartMs)) });
        return t;
    }

    // ── メニュー（M1b-4） ────────────────────────────────────────────────
    // メニューPGCの buttons.json（セル境界/静止/代表PCI）。無ければ空。
    public KurokoButtons ReadMenuButtons(string domain, int pgc)
        => _iso.FindEntry(KurokoLayout.MenuButtonsJson(domain, pgc)) is KrkEntry f
            ? KurokoJson.Deserialize<KurokoButtons>(_iso.ReadAllBytes(f)) ?? new KurokoButtons()
            : new KurokoButtons();

    // メニューPGCの palette.json（YUV16色）。無ければタイトルの既定へ委ねる用に空。
    public KurokoMenuPalette ReadMenuPalette(string domain, int pgc)
        => _iso.FindEntry(KurokoLayout.MenuPaletteJson(domain, pgc)) is KrkEntry f
            ? KurokoJson.Deserialize<KurokoMenuPalette>(_iso.ReadAllBytes(f)) ?? new KurokoMenuPalette()
            : new KurokoMenuPalette();

    // ── メニュー v3（セル単位ファイル。menu.json＋C<nn>.mkv/C<nn>.spu） ───────────────
    // メニューPGCの menu.json（KurokoMenuV3。PGCメタ＋全セルの時刻付きper-VOBU PCI）。無ければ null＝v3 メニュー無し。
    public KurokoMenuV3? ReadMenuV3(string domain, int pgc)
    {
        if (_iso.FindEntry(KurokoLayout.MenuJson(domain, pgc)) is not KrkEntry f) return null;
        var m = KurokoJson.Deserialize<KurokoMenuV3>(_iso.ReadAllBytes(f));
        // programMap：version 5 は 0 始まり、内部（エンジン・DvdVmNavigator）は IFO と同じ 1 始まり。
        if (m is not null && IsV5)
            m.ProgramMap = m.ProgramMap.Select(c => c + 1).ToList();
        return m;
    }

    // v3 メニューの1セルの SPU 図柄 C<nn>.spu（表示モード別の物理サブピクチャを substream 0x20+n でタグ付けした
    // 小さな MPEG-PS。PTS はセル 0起点）。無ければ空配列（背景のみのセル）。復号は KurokoMenuSpu.Parse。
    public byte[] ReadMenuCellSpu(string domain, int pgc, int cell)
        => _iso.FindEntry(KurokoLayout.MenuCellSpu(domain, pgc, cell)) is KrkEntry f
            ? _iso.ReadAllBytes(f)
            : System.Array.Empty<byte>();

    // title.json の中身。PrefetchTitleJsons で先に取り込んでいればそれを使う（1 回きり：使ったら捨てる＝メモリを抱えない）。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> _titleJsonCache = new();
    // seg.json の中身（PrefetchTitleJsons で取り込む）。タイトル間で同じセグメントを使うので、使っても捨てない（小さい）。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _segJsonCache = new(StringComparer.OrdinalIgnoreCase);

    private byte[] ReadTitleJson(int titleId)
        => _titleJsonCache.TryRemove(titleId, out var cached) ? cached : ReadFile(_iso, KurokoLayout.TitleJson(titleId));

    // 全タイトルの title.json を**まとめて読む**（ReadAllBytesBatch）。タイトル一覧（再生画面のタイトルボタン・
    // ファサードの vc_player_disc_state）は全タイトルの title.json を読むので、1 本ずつだとファイル源（SMB）では
    // 本数ぶんの往復になり、BD 由来の 65 タイトルの krk で最初の一覧に 3〜6 秒かかっていた。
    public void PrefetchTitleJsons(IEnumerable<int> titleIds)
    {
        var entries = new List<KrkEntry>();
        var idOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in titleIds)
        {
            if (_titleJsonCache.ContainsKey(id)) continue;
            if (_iso.FindEntryForBatch(KurokoLayout.TitleJson(id)) is not KrkEntry e) continue;
            entries.Add(e);
            idOf[e.Name.Replace("\\", "/").TrimStart('/')] = id;
        }
        // seg.json も一緒に（タイトルの組み立てが最初のセグメントの seg.json を読むため。BD 由来の krk ではタイトルの数だけ読む）。
        var segOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _iso.Entries)
        {
            string name = e.Name.Replace("\\", "/").TrimStart('/');
            const string Prefix = "SEGMENTS/", Suffix = "/seg.json";
            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) continue;
            string segId = name[Prefix.Length..^Suffix.Length];
            if (segId.Contains('/') || _segJsonCache.ContainsKey(segId)) continue;
            entries.Add(e);
            segOf[name] = segId;
        }
        if (entries.Count < 2) return;
        try
        {
            foreach (var kv in _iso.ReadAllBytesBatch(entries))
            {
                if (idOf.TryGetValue(kv.Key, out var id)) _titleJsonCache[id] = kv.Value;
                else if (segOf.TryGetValue(kv.Key, out var segId)) _segJsonCache[segId] = kv.Value;
            }
        }
        catch { }   // 読めなければ従来どおり 1 本ずつ読む
    }

    private static byte[] ReadFile(KurokoContainerReader iso, string path)
        => iso.FindEntry(path) is KrkEntry f
            ? iso.ReadAllBytes(f)
            : throw new FileNotFoundException($"{path} が .krk 内に見つかりません。");

    public void Dispose() => _iso.Dispose();
}
