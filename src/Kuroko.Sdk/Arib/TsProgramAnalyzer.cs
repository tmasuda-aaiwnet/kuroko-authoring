using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Kuroko.Arib;

// 放送 MPEG-2 TS の「番組（イベント）タイムライン」と「音声コンポーネント（名称・言語・デュアルモノ）」を、
// PAT/PMT/EIT/TOT を解析して抽出する（Kuroko none 変換の events[]・音声名称・デュアルモノ区間・言語のため）。
// 文字は ARIB STD-B24（AribB24Decoder）で復号する。
//
//  ・EIT（p/f=0x4E・schedule=0x50–0x5F）から各イベントの event_id・開始時刻(JST)・尺・番組名・音声コンポーネント。
//  ・TOT（PID 0x14, table 0x73）の UTC＋PCR で「録画先頭の絶対時刻 T0(JST)」を得る。
//  ・イベントの絶対開始時刻 − T0 ＝ 録画先頭からの相対時刻(ms)。録画区間に重なるイベントで events[] を組む。
//  ・音声の component_tag は PMT の stream_identifier(0x52) で PID に対応づく。デュアルモノ＝component_type 0x02。

public sealed class TsAudioComponentInfo
{
    public int Pid { get; set; }
    public byte? ComponentTag { get; set; }
    public byte? ComponentType { get; set; }  // EIT audio_component_descriptor（0x02=デュアルモノ）
    public string? Language { get; set; }
    public string? Name { get; set; }
    public bool IsDualMono => ComponentType == 0x02;
}

public sealed class TsEventInfo
{
    public int EventId { get; set; }
    public double StartMs { get; set; }        // 録画先頭からの相対（ms）
    public double EndMs { get; set; }
    public string? Name { get; set; }
    public DateTime? StartTime { get; set; }    // 放送絶対時刻（JST）
    public List<TsAudioComponentInfo> Audio { get; set; } = new();
}

public sealed class TsProgramTimeline
{
    public int ProgramNumber { get; set; }
    public List<int> AudioPids { get; set; } = new();
    public Dictionary<int, byte> ComponentTagByPid { get; set; } = new();
    public Dictionary<int, string> LanguageByPid { get; set; } = new();
    public Dictionary<int, int> StreamTypeByPid { get; set; } = new();
    // 実ビットストリーム（AAC channel_config==0）から検出したデュアルモノ区間（ms）。PID ごと。EIT より信頼できる。
    public Dictionary<int, List<(double StartMs, double EndMs)>> BitstreamDualMono { get; set; } = new();
    // 各音声 PID が実データを持つ範囲（First..Last ms）。トラックがどの時間帯に存在するか（スパン組立）に使う。
    public Dictionary<int, (double First, double Last)> AudioDataRange { get; set; } = new();
    public List<TsEventInfo> Events { get; set; } = new();   // 時刻順

    // 指定 PID・時刻(ms) の音声コンポーネント情報（該当イベントから）。無ければ null。
    public TsAudioComponentInfo? AudioAt(int pid, double ms)
    {
        var ev = Events.FirstOrDefault(e => ms >= e.StartMs - 1 && ms < e.EndMs + 1) ?? Events.LastOrDefault();
        return ev?.Audio.FirstOrDefault(a => a.Pid == pid);
    }

    // 指定 PID がデュアルモノな区間（ms）。イベントごとの component_type=0x02 を連結。
    public List<(double StartMs, double EndMs)> DualMonoRangesFor(int pid)
    {
        var raw = new List<(double, double)>();
        foreach (var e in Events)
            if (e.Audio.FirstOrDefault(x => x.Pid == pid) is { IsDualMono: true }) raw.Add((e.StartMs, e.EndMs));
        raw.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        var merged = new List<(double, double)>();
        foreach (var r in raw)
            if (merged.Count > 0 && r.Item1 <= merged[^1].Item2 + 1) merged[^1] = (merged[^1].Item1, Math.Max(merged[^1].Item2, r.Item2));
            else merged.Add(r);
        return merged;
    }
}

public static class TsProgramAnalyzer
{
    private const int Pkt = 188;
    public static string LastDebug { get; private set; } = "";

    public static List<TsProgramTimeline> Analyze(string tsPath, double maxSec = 0, CancellationToken ct = default)
    {
        var sc = new Scanner();
        try
        {
            using var fs = new FileStream(tsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1 << 20, FileOptions.SequentialScan);
            int sync = FindSync(fs);
            fs.Seek(sync, SeekOrigin.Begin);
            var pkt = new byte[Pkt];
            while (fs.Read(pkt, 0, Pkt) == Pkt)
            {
                ct.ThrowIfCancellationRequested();
                if (pkt[0] != 0x47) { if (!Resync(fs)) break; continue; }
                sc.Feed(pkt);
                if (maxSec > 0 && !double.IsNaN(sc.FirstPcrMs) && sc.CurMs > maxSec * 1000.0 + 5000.0) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LastDebug = "EXCEPTION:" + ex.Message; return new(); }
        var res = sc.Build(maxSec);
        LastDebug = sc.Debug;
        return res;
    }

    private sealed class PmtState
    {
        public List<int> AudioPids = new();
        public Dictionary<int, byte> Tag = new();
        public Dictionary<int, string> Lang = new();
        public Dictionary<int, int> Type = new();
    }

    // 音声 ES（AAC）の channel_config を PES 単位で追跡し、デュアルモノ（ch_config==0）区間を作る。
    private sealed class EsState
    {
        public int Ch = -1;
        public double RunStart = double.NaN;   // デュアルモノ run の開始（ms）
        public double FirstMs = double.NaN, LastMs;   // このトラックが実データを持つ範囲
        public List<(double, double)> Ranges = new();

        public void Note(double ms) { if (double.IsNaN(FirstMs)) FirstMs = ms; LastMs = ms; }

        public void Sample(int ch, double ms)
        {
            Note(ms);
            LastMs = ms;
            bool dual = ch == 0;                // ARIB：channel_config=0（PCE の 1+1）＝デュアルモノ
            if (dual && double.IsNaN(RunStart)) RunStart = ms;
            else if (!dual && !double.IsNaN(RunStart)) { Ranges.Add((RunStart, ms)); RunStart = double.NaN; }
            Ch = ch;
        }
        public void Close() { if (!double.IsNaN(RunStart)) { Ranges.Add((RunStart, LastMs)); RunStart = double.NaN; } }
    }

    private sealed class Scanner
    {
        private readonly Dictionary<int, int> _pmtPidToProg = new();
        private readonly Dictionary<int, PmtState> _progPmt = new();
        private readonly HashSet<int> _pcrPids = new();
        private readonly Dictionary<int, TsProgramTimeline> _timelines = new();
        private readonly Dictionary<int, TsEventInfo?> _present = new(); // service → 進行中の present イベント
        private readonly Dictionary<int, SectionAsm> _asm = new();
        private readonly HashSet<int> _audioPids = new();       // PMT 由来の音声 ES PID
        private readonly Dictionary<int, EsState> _es = new();  // 音声 PID → channel_config 追跡
        private readonly Dictionary<int, int> _esType = new();  // 音声 PID → stream_type
        public double FirstPcrMs = double.NaN, CurMs;
        public string Debug = "";

        public void Feed(byte[] pkt)
        {
            int pid = ((pkt[1] & 0x1F) << 8) | pkt[2];
            bool pusi = (pkt[1] & 0x40) != 0;
            int afc = (pkt[3] >> 4) & 3;
            int off = 4;
            if (afc is 2 or 3)
            {
                int afLen = pkt[4];
                if (afLen > 0 && (pkt[5] & 0x10) != 0)
                {
                    long b = ((long)pkt[6] << 25) | ((long)pkt[7] << 17) | ((long)pkt[8] << 9) | ((long)pkt[9] << 1) | ((long)(pkt[10] >> 7));
                    double ms = b / 90.0;
                    if (_pcrPids.Contains(pid) || double.IsNaN(FirstPcrMs))
                    {
                        if (double.IsNaN(FirstPcrMs)) FirstPcrMs = ms;
                        double c = ms - FirstPcrMs;
                        if (c < -1000) c += (1L << 33) / 90.0; // PCR ラップ
                        CurMs = c;
                    }
                }
                off += 1 + afLen;
            }
            if (afc is 0 or 2 || off >= Pkt) return;
            if (_audioPids.Contains(pid)) { if (pusi) FeedAudioEs(pkt, off, pid); return; }
            if (pid != 0x0000 && pid != 0x0012 && pid != 0x0014 && !_pmtPidToProg.ContainsKey(pid)) return;
            if (!_asm.TryGetValue(pid, out var a)) _asm[pid] = a = new SectionAsm();
            foreach (var sec in a.Push(pkt, off, Pkt - off, pusi)) Handle(sec, pid);
        }

        // 音声 ES PES の先頭 ADTS フレームから channel_config を読み、PES PTS でデュアルモノ区間を追跡。
        private void FeedAudioEs(byte[] pkt, int off, int pid)
        {
            if (double.IsNaN(FirstPcrMs)) return;
            // PES header: 00 00 01 stream_id, PES_len(2), flags(2), header_len(1), [PTS]
            if (off + 9 > Pkt || pkt[off] != 0x00 || pkt[off + 1] != 0x00 || pkt[off + 2] != 0x01) return;
            int flags2 = pkt[off + 7];
            int hdrLen = pkt[off + 8];
            double ptsMs;
            if ((flags2 & 0x80) != 0 && off + 14 <= Pkt) // PTS_DTS_flags の PTS
            {
                long pts = ((long)(pkt[off + 9] & 0x0E) << 29) | ((long)pkt[off + 10] << 22) | ((long)(pkt[off + 11] & 0xFE) << 14)
                         | ((long)pkt[off + 12] << 7) | ((long)pkt[off + 13] >> 1);
                ptsMs = pts / 90.0 - FirstPcrMs;
                // PTS 33bit ラップ補正（PCR 経路 CurMs と同様）。放送 PTS は約26.5hで一巡するため、録画が
                // ラップ点を跨ぐと ptsMs が巨大な負値へ飛ぶ。これを補正しないと AudioDataRange の Last が壊れ、
                // BuildAudioSpans が span 中点を外して全音声トラックを破棄する（seg.json audio.tracks=[]）。
                if (ptsMs < -1000) ptsMs += (1L << 33) / 90.0;
            }
            else ptsMs = CurMs;
            int es = off + 9 + hdrLen; // AAC データ先頭
            if (es + 4 > Pkt) return;
            if (!_es.TryGetValue(pid, out var st)) _es[pid] = st = new EsState();
            // ADTS 同期（0xFFFx）なら channel_config を読む（LATM 等は対象外＝ch 不明で dual 判定しない）。
            if (pkt[es] == 0xFF && (pkt[es + 1] & 0xF6) == 0xF0)
            {
                int ch = ((pkt[es + 2] & 1) << 2) | ((pkt[es + 3] >> 6) & 3);
                st.Sample(ch, ptsMs);
            }
            else st.Note(ptsMs);
        }

        private void Handle(byte[] sec, int pid)
        {
            if (sec.Length < 8) return;
            int tableId = sec[0];
            int secLen = ((sec[1] & 0x0F) << 8) | sec[2];
            int end = Math.Min(sec.Length, 3 + secLen);
            if (end < 8) return;

            if (tableId == 0x00 && pid == 0x0000)
            {
                for (int p = 8; p + 4 <= end - 4; p += 4)
                {
                    int prog = (sec[p] << 8) | sec[p + 1];
                    int pmtPid = ((sec[p + 2] & 0x1F) << 8) | sec[p + 3];
                    if (prog != 0 && pmtPid > 0) _pmtPidToProg[pmtPid] = prog;
                }
                return;
            }
            if (tableId == 0x02 && _pmtPidToProg.TryGetValue(pid, out var program)) { ParsePmt(sec, end, program); return; }
            // EIT[p/f actual] present（table 0x4E, section 0）＝現在放送中の番組。これだけを追う（絶対時刻・schedule は不要）。
            if (pid == 0x0012 && tableId == 0x4E && sec[6] == 0x00) ParsePresent(sec, end);
        }

        private void ParsePmt(byte[] sec, int end, int program)
        {
            int pmtSvc = (sec[3] << 8) | sec[4];
            if (pmtSvc != 0) program = pmtSvc;
            int pcrPid = ((sec[8] & 0x1F) << 8) | sec[9];
            if (pcrPid is > 0 and < 0x1FFF) _pcrPids.Add(pcrPid);
            var st = new PmtState();
            int piLen = ((sec[10] & 0x0F) << 8) | sec[11];
            int pos = 12 + piLen, loopEnd = end - 4;
            while (pos + 5 <= loopEnd)
            {
                int streamType = sec[pos];
                int esPid = ((sec[pos + 1] & 0x1F) << 8) | sec[pos + 2];
                int esLen = ((sec[pos + 3] & 0x0F) << 8) | sec[pos + 4];
                int dStart = pos + 5, dEnd = Math.Min(dStart + esLen, loopEnd);
                if (IsAudio(streamType))
                {
                    st.AudioPids.Add(esPid);
                    st.Type[esPid] = streamType;
                    _audioPids.Add(esPid);
                    _esType[esPid] = streamType;
                    int dp = dStart;
                    while (dp + 2 <= dEnd)
                    {
                        int tag = sec[dp], len = sec[dp + 1], ds = dp + 2;
                        if (ds + len > dEnd) break;
                        if (tag == 0x52 && len >= 1) st.Tag[esPid] = sec[ds];
                        else if (tag == 0x0A && len >= 3) st.Lang[esPid] = Ascii3(sec, ds);
                        dp = ds + len;
                    }
                }
                pos = dStart + esLen;
            }
            if (st.AudioPids.Count == 0) return;
            _progPmt[program] = st;
            if (!_timelines.TryGetValue(program, out var tl)) _timelines[program] = tl = new TsProgramTimeline { ProgramNumber = program };
            tl.AudioPids = st.AudioPids.Distinct().ToList();
            tl.ComponentTagByPid = new Dictionary<int, byte>(st.Tag);
            tl.LanguageByPid = new Dictionary<int, string>(st.Lang);
            tl.StreamTypeByPid = new Dictionary<int, int>(st.Type);
        }

        // EIT[p/f] present（section 0）：現在放送中の番組。present は同一番組の間くり返し送られ、番組が変わると
        // event_id が変わる。event_id が変わった位置（PCR 相対時刻 CurMs）を番組境界として events を組む。絶対時刻は不要。
        private void ParsePresent(byte[] sec, int end)
        {
            int serviceId = (sec[3] << 8) | sec[4];
            if (!_timelines.TryGetValue(serviceId, out var tl)) return; // PMT 未取得のサービスは対象外
            int pos = 14, loopEnd = end - 4;
            if (pos + 12 > loopEnd) return;                 // present はイベント1件
            int eventId = (sec[pos] << 8) | sec[pos + 1];
            DateTime? startJst = ParseMjdBcd(sec, pos + 2);  // 番組の予定開始時刻（表示用）
            int dloop = ((sec[pos + 10] & 0x0F) << 8) | sec[pos + 11];
            int dStart = pos + 12, dEnd = Math.Min(dStart + dloop, loopEnd);

            _present.TryGetValue(serviceId, out var cur);
            if (cur is null || cur.EventId != eventId)
            {
                if (cur is not null) cur.EndMs = CurMs;      // 前番組を現在位置で閉じる
                var ev = new TsEventInfo { EventId = eventId, StartMs = cur is null ? 0 : CurMs, StartTime = startJst };
                ParseEitDescriptors(sec, dStart, dEnd, ev, serviceId);
                tl.Events.Add(ev);
                _present[serviceId] = ev;
            }
            else if (string.IsNullOrEmpty(cur.Name) || cur.Audio.Count == 0)
                ParseEitDescriptors(sec, dStart, dEnd, cur, serviceId); // 記述子を後追いで補完
        }

        private void ParseEitDescriptors(byte[] sec, int start, int end, TsEventInfo ev, int serviceId)
        {
            int pos = start;
            while (pos + 2 <= end)
            {
                int tag = sec[pos], len = sec[pos + 1], ds = pos + 2, de = ds + len;
                if (de > end) break;
                if (tag == 0x4D && len >= 5) // short_event_descriptor（番組名）
                {
                    int nameLen = sec[ds + 3], ns = ds + 4;
                    if (nameLen > 0 && ns + nameLen <= de && string.IsNullOrEmpty(ev.Name))
                        ev.Name = SafeDecode(sec, ns, nameLen);
                }
                else if (tag == 0xC4 && len >= 9) // audio_component_descriptor
                {
                    byte componentType = sec[ds + 1], componentTag = sec[ds + 2], flags = sec[ds + 5];
                    bool multi = (flags & 0x80) != 0;
                    string? lang = Ascii3(sec, ds + 6);
                    int tp = ds + 9; if (multi && tp + 3 <= de) tp += 3;
                    string? name = tp < de ? SafeDecode(sec, tp, de - tp) : null;
                    int pid = -1;
                    if (_timelines.TryGetValue(serviceId, out var tl))
                        foreach (var kv in tl.ComponentTagByPid) if (kv.Value == componentTag) { pid = kv.Key; break; }
                    var comp = ev.Audio.FirstOrDefault(a => a.ComponentTag == componentTag);
                    if (comp is null) ev.Audio.Add(new TsAudioComponentInfo
                    {
                        Pid = pid, ComponentTag = componentTag, ComponentType = componentType,
                        Language = lang, Name = string.IsNullOrWhiteSpace(name) ? null : name,
                    });
                }
                pos = de;
            }
        }

        public List<TsProgramTimeline> Build(double maxSec)
        {
            double recEnd = maxSec > 0 ? maxSec * 1000.0 : (CurMs > 0 ? CurMs : 0);
            foreach (var kv in _es) kv.Value.Close();
            // ES 由来のデュアルモノ区間を各タイムラインへ（PID→区間）。
            foreach (var tl in _timelines.Values)
                foreach (var pid in tl.AudioPids)
                    if (_es.TryGetValue(pid, out var st))
                    {
                        if (st.Ranges.Count > 0)
                            tl.BitstreamDualMono[pid] = st.Ranges
                                .Where(r => r.Item2 - r.Item1 > 100)
                                .Select(r => (Math.Max(0, r.Item1), maxSec > 0 ? Math.Min(recEnd, r.Item2) : r.Item2)).ToList();
                        if (!double.IsNaN(st.FirstMs))
                            tl.AudioDataRange[pid] = (Math.Max(0, st.FirstMs), st.LastMs);
                    }
            // present 追跡で積んだ events を仕上げる：末尾を録画終端で閉じ、音声 PID を補完。
            foreach (var tl in _timelines.Values)
            {
                if (tl.Events.Count > 0) tl.Events[^1].EndMs = Math.Max(tl.Events[^1].StartMs, recEnd);
                foreach (var e in tl.Events)
                {
                    e.EndMs = Math.Min(e.EndMs <= e.StartMs ? recEnd : e.EndMs, recEnd);
                    foreach (var a in e.Audio)
                        if (a.Pid < 0 && a.ComponentTag is byte tg)
                            foreach (var kv in tl.ComponentTagByPid) if (kv.Value == tg) { a.Pid = kv.Key; break; }
                }
            }
            Debug = $"curMs={CurMs:F0} svcs=[{string.Join(",", _timelines.Keys)}] " +
                    $"events=[{string.Join(",", _timelines.Values.Select(t => t.Events.Count))}]\n  " +
                    string.Join("\n  ", _timelines.Values.SelectMany(t => t.Events.Select(e =>
                        $"ev{e.EventId} [{e.StartMs:F0}-{e.EndMs:F0}]ms start={e.StartTime:MM-dd HH:mm:ss} \"{e.Name}\""))) + "\n  " +
                    string.Join("\n  ", _timelines.Values.SelectMany(t => t.AudioPids.Select(p =>
                        $"PID {p} type=0x{(t.StreamTypeByPid.TryGetValue(p, out var ty) ? ty : 0):X2} " +
                        $"bitstreamDualMono=[{(t.BitstreamDualMono.TryGetValue(p, out var rr) ? string.Join(",", rr.Select(r => $"{r.StartMs:F0}-{r.EndMs:F0}")) : "")}]")));
            return _timelines.Values.ToList();
        }
    }

    private static bool IsAudio(int t) => t is 0x03 or 0x04 or 0x0F or 0x11 or 0x81 or 0x87;

    private static string Ascii3(byte[] b, int o) => o + 3 <= b.Length ? System.Text.Encoding.ASCII.GetString(b, o, 3) : "";

    private static string? SafeDecode(byte[] b, int o, int len)
    {
        try { var s = AribB24Decoder.Decode(b.AsSpan(o, Math.Min(len, b.Length - o))); return string.IsNullOrWhiteSpace(s) ? null : s.Trim(); }
        catch { return null; }
    }

    private static double DurationMs(byte[] b, int o)
        => o + 3 <= b.Length ? (FromBcd(b[o]) * 3600 + FromBcd(b[o + 1]) * 60 + FromBcd(b[o + 2])) * 1000.0 : 0;

    // MJD＋BCD の解釈は共有ヘルパへ（TOT/EIT で同じ形式。AribMjdTime を参照）。
    private static DateTime? ParseMjdBcd(byte[] b, int o) => AribMjdTime.Parse(b, o);

    private static int FromBcd(byte v) => AribMjdTime.FromBcd(v);

    private static int FindSync(FileStream fs)
    {
        long pos0 = fs.Position;
        var buf = new byte[Pkt * 64];
        int read = fs.Read(buf, 0, buf.Length);
        fs.Seek(pos0, SeekOrigin.Begin);
        for (int off = 0; off < Pkt && off < read; off++)
        {
            int m = 0;
            for (int p = off; p + 1 <= read; p += Pkt) { if (buf[p] == 0x47) m++; else break; }
            if (m >= 4) return off;
        }
        return 0;
    }

    private static bool Resync(FileStream fs)
    {
        long start = fs.Position - 1;
        var one = new byte[1];
        long limit = Math.Min(fs.Length, start + Pkt * 32);
        for (long p = start; p < limit; p++)
        {
            fs.Seek(p, SeekOrigin.Begin);
            if (fs.Read(one, 0, 1) != 1) return false;
            if (one[0] == 0x47) { fs.Seek(p, SeekOrigin.Begin); return true; }
        }
        return false;
    }

    private sealed class SectionAsm
    {
        private readonly List<byte> _buf = new();
        private int? _expected;

        public IEnumerable<byte[]> Push(byte[] pkt, int off, int count, bool pusi)
        {
            var outp = new List<byte[]>();
            int pos = off, end = off + count;
            if (pusi)
            {
                if (pos >= end) return outp;
                int ptr = pkt[pos]; pos++;
                // pointer_field 分は**進行中セクションの残り**。まず食わせて完成させる（前 section を落とさない）。
                int tail = Math.Min(ptr, end - pos);
                Feed(pkt, pos, tail, outp);
                pos += ptr; if (pos > end) pos = end;
                _buf.Clear(); _expected = null; // ここから新 section
            }
            Feed(pkt, pos, end - pos, outp);
            return outp;
        }

        private void Feed(byte[] pkt, int pos, int count, List<byte[]> outp)
        {
            int end = pos + count;
            while (pos < end)
            {
                if (_buf.Count == 0 && pkt[pos] == 0xFF) break; // stuffing
                _buf.Add(pkt[pos]); pos++;
                if (_buf.Count == 3) _expected = 3 + (((_buf[1] & 0x0F) << 8) | _buf[2]);
                if (_expected is int exp && _buf.Count >= exp)
                {
                    outp.Add(_buf.Take(exp).ToArray());
                    var extra = _buf.Skip(exp).ToArray();
                    _buf.Clear(); _buf.AddRange(extra);
                    _expected = _buf.Count >= 3 ? 3 + (((_buf[1] & 0x0F) << 8) | _buf[2]) : null;
                }
            }
        }
    }
}
