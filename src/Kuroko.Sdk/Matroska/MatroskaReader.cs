using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Kuroko.Matroska;

// Matroska（.mkv・.webm・krk の av.mkv）の読み出し（自前。ffmpeg の libavformat を使わない）。
// 仕様：RFC 9559（Matroska Media Container Format Specification）・RFC 8794（EBML）。
//   ・EBML ヘッダ → Segment（大きさ不明も可）→ SeekHead・Info・Tracks・Cues・Cluster…
//   ・Cluster（大きさ不明も可）の SimpleBlock・BlockGroup（Block・BlockDuration・ReferenceBlock）。レーシング（Xiph・EBML・固定）を分ける。
//   ・トラックの ContentEncoding：ヘッダ除去（ContentCompAlgo=3。各フレームの頭へ設定のバイト列を足す）と zlib（0）。
//   ・時刻は TimestampScale の単位（既定 1,000,000ns＝1ms）。レーシングの 2 つ目以降のフレームは、ブロックの長さ÷フレーム数ずつ進める
//     （長さが分からなければ時刻なし）。ffmpeg の matroskadec と同じ扱い。
//   ・シーク：Cues から目標の時刻以前で最も近い点のクラスタへ飛び、目標の時刻に満たないブロックとキーフレームまでを読み飛ばす
//     （ffmpeg の av_seek_frame（AVSEEK_FLAG_BACKWARD）と同じ着地）。Cues が無ければ読んだクラスタの位置を覚えて使い、無ければ先頭から。
// トラックの番号（Index）は Tracks に並んだ順（ffmpeg の stream_index と同じ）。
public sealed class MatroskaTrack
{
    public int Index;                 // Tracks に並んだ順（0 から）
    public long Number;               // TrackNumber（ブロックが指す番号）
    public int Type;                  // 1＝映像・2＝音声・17＝字幕
    public string CodecId = "";
    public byte[]? CodecPrivate;
    public long DefaultDurationNs;    // 0＝不明
    public long CodecDelayNs, SeekPreRollNs;
    public string? Language;
    public string? Name;
    public bool FlagDefault = true, FlagForced, FlagEnabled = true;
    // 映像
    public int PixelWidth, PixelHeight, DisplayWidth, DisplayHeight, DisplayUnit;
    public int FlagInterlaced, FieldOrder = -1;
    public int MatrixCoefficients = 2, Range;   // Colour（2＝未指定・0＝未指定）
    // 音声
    public double SamplingFrequency = 8000, OutputSamplingFrequency;
    public int Channels = 1, BitDepth;
    // ContentEncoding
    public byte[]? StripHeader;
    public bool Zlib;
}

public readonly record struct MatroskaFrame(int TrackIndex, long Timestamp, bool HasTimestamp, long Duration, bool KeyFrame, byte[] Data, long Position);

public sealed class MatroskaReader : IDisposable
{
    private const uint IdEbml = 0x1A45DFA3, IdSegment = 0x18538067, IdSeekHead = 0x114D9B74, IdInfo = 0x1549A966, IdTracks = 0x1654AE6B,
        IdCues = 0x1C53BB6B, IdCluster = 0x1F43B675, IdChapters = 0x1043A770, IdTags = 0x1254C367, IdAttachments = 0x1941A469;
    private const uint IdTimestamp = 0xE7, IdSimpleBlock = 0xA3, IdBlockGroup = 0xA0, IdBlock = 0xA1, IdBlockDuration = 0x9B, IdReferenceBlock = 0xFB;
    private const long Unknown = -1;

    private readonly Stream _s;
    private readonly bool _leaveOpen;
    private readonly long _length;
    private long _segmentStart;          // Segment の中身の先頭（Cues・SeekHead の位置の基準）
    private long _segmentEnd;            // 大きさ不明なら long.MaxValue
    private long _firstClusterPos = -1;
    private readonly Dictionary<long, MatroskaTrack> _byNumber = new();
    private readonly List<(long Time, long ClusterPos, long Track)> _cues = new();
    private readonly SortedList<long, long> _seenClusters = new();   // クラスタの時刻 → 位置（Cues が無いとき用）

    // 読み位置の状態
    private long _pos;                   // 次に読む要素の位置
    private long _clusterEnd = -1;       // 今のクラスタの終わり（大きさ不明は long.MaxValue）。-1＝クラスタの外
    private long _clusterTime;
    private readonly Queue<MatroskaFrame> _pending = new();
    private long _skipTo = long.MinValue; // シーク後：これより前の時刻のブロックを捨てる
    private bool _skipToKey;
    private int _skipKeyTrack = -1;

    public long TimestampScaleNs { get; private set; } = 1_000_000;
    public double DurationTicks { get; private set; } = double.NaN;   // TimestampScale の単位
    public IReadOnlyList<MatroskaTrack> Tracks { get; }
    public string DocType { get; private set; } = "";
    public bool HasCues => _cues.Count > 0;

    public MatroskaReader(Stream stream, bool leaveOpen = false)
    {
        if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("読み出し・シークのできる Stream が要る");
        _s = stream;
        _leaveOpen = leaveOpen;
        _length = stream.Length;
        Tracks = ReadHeaders();
    }

    public void Dispose()
    {
        if (!_leaveOpen) _s.Dispose();
    }

    // ── ヘッダ ─────────────────────────────────────────────

    private List<MatroskaTrack> ReadHeaders()
    {
        long p = 0;
        if (!ReadElementHeader(ref p, out uint id, out long size) || id != IdEbml) throw new InvalidDataException("EBML ヘッダが無い");
        long ebmlEnd = p + size;
        while (p < ebmlEnd)
        {
            if (!ReadElementHeader(ref p, out uint cid, out long csz)) break;
            if (cid == 0x4282) DocType = ReadString(p, csz);
            p += csz;
        }
        p = ebmlEnd;
        // Segment を探す（前に Void などがあっても飛ばす）
        while (true)
        {
            if (!ReadElementHeader(ref p, out id, out size)) throw new InvalidDataException("Segment が無い");
            if (id == IdSegment) break;
            if (size == Unknown) throw new InvalidDataException("Segment の前に大きさ不明の要素");
            p += size;
        }
        _segmentStart = p;
        _segmentEnd = size == Unknown ? long.MaxValue : Math.Min(_length, p + size);

        var tracks = new List<MatroskaTrack>();
        long infoPos = -1, tracksPos = -1, cuesPos = -1;
        // 先頭からクラスタまでの要素を読む。SeekHead に Cues 等の位置があれば覚える
        long q = _segmentStart;
        while (q < SegEnd)
        {
            long at = q;
            if (!ReadElementHeader(ref q, out id, out size)) break;
            if (id == IdCluster) { _firstClusterPos = at; break; }
            if (size == Unknown) break;
            switch (id)
            {
                case IdInfo: infoPos = at; break;
                case IdTracks: tracksPos = at; break;
                case IdCues: cuesPos = at; break;
                case IdSeekHead: ReadSeekHead(q, size, ref infoPos, ref tracksPos, ref cuesPos); break;
            }
            q += size;
        }
        if (infoPos >= 0) ReadInfo(infoPos);
        if (tracksPos >= 0) tracks = ReadTracks(tracksPos);
        if (cuesPos >= 0) { try { ReadCues(cuesPos); } catch { _cues.Clear(); } }
        if (_firstClusterPos < 0) _firstClusterPos = q;
        _pos = _firstClusterPos;
        foreach (var t in tracks) _byNumber[t.Number] = t;
        return tracks;
    }

    private long SegEnd => Math.Min(_segmentEnd, _length);

    private void ReadSeekHead(long p, long size, ref long info, ref long tracks, ref long cues)
    {
        long end = p + size;
        while (p < end)
        {
            if (!ReadElementHeader(ref p, out uint id, out long sz) || sz == Unknown) return;
            if (id == 0x4DBB)
            {
                long q = p, e = p + sz;
                uint target = 0; long pos = -1;
                while (q < e)
                {
                    if (!ReadElementHeader(ref q, out uint cid, out long csz)) break;
                    if (cid == 0x53AB) { var b = ReadBytes(q, (int)csz); target = 0; foreach (var x in b) target = target << 8 | x; }
                    else if (cid == 0x53AC) pos = (long)ReadUInt(q, csz);
                    q += csz;
                }
                if (pos >= 0)
                {
                    long abs = _segmentStart + pos;
                    if (target == IdInfo && info < 0) info = abs;
                    else if (target == IdTracks && tracks < 0) tracks = abs;
                    else if (target == IdCues && cues < 0) cues = abs;
                }
            }
            p += sz;
        }
    }

    private void ReadInfo(long at)
    {
        long p = at;
        if (!ReadElementHeader(ref p, out uint id, out long size) || id != IdInfo) return;
        long end = p + size;
        while (p < end)
        {
            if (!ReadElementHeader(ref p, out uint cid, out long csz)) break;
            if (cid == 0x2AD7B1) TimestampScaleNs = (long)ReadUInt(p, csz);
            else if (cid == 0x4489) DurationTicks = ReadFloat(p, csz);
            p += csz;
        }
        if (TimestampScaleNs <= 0) TimestampScaleNs = 1_000_000;
    }

    private List<MatroskaTrack> ReadTracks(long at)
    {
        var list = new List<MatroskaTrack>();
        long p = at;
        if (!ReadElementHeader(ref p, out uint id, out long size) || id != IdTracks) return list;
        long end = p + size;
        while (p < end)
        {
            if (!ReadElementHeader(ref p, out uint cid, out long csz)) break;
            if (cid == 0xAE)
            {
                var t = new MatroskaTrack { Index = list.Count };
                ReadTrackEntry(p, p + csz, t);
                list.Add(t);
            }
            p += csz;
        }
        return list;
    }

    private void ReadTrackEntry(long p, long end, MatroskaTrack t)
    {
        while (p < end)
        {
            if (!ReadElementHeader(ref p, out uint id, out long sz)) break;
            switch (id)
            {
                case 0xD7: t.Number = (long)ReadUInt(p, sz); break;
                case 0x83: t.Type = (int)ReadUInt(p, sz); break;
                case 0xB9: t.FlagEnabled = ReadUInt(p, sz) != 0; break;
                case 0x88: t.FlagDefault = ReadUInt(p, sz) != 0; break;
                case 0x55AA: t.FlagForced = ReadUInt(p, sz) != 0; break;
                case 0x86: t.CodecId = ReadString(p, sz); break;
                case 0x63A2: t.CodecPrivate = ReadBytes(p, (int)sz); break;
                case 0x23E383: t.DefaultDurationNs = (long)ReadUInt(p, sz); break;
                case 0x22B59C: t.Language ??= ReadString(p, sz); break;
                case 0x22B59D: t.Language = ReadString(p, sz); break;
                case 0x536E: t.Name = ReadString(p, sz); break;
                case 0x56AA: t.CodecDelayNs = (long)ReadUInt(p, sz); break;
                case 0x56BB: t.SeekPreRollNs = (long)ReadUInt(p, sz); break;
                case 0xE0: ReadVideo(p, p + sz, t); break;
                case 0xE1: ReadAudio(p, p + sz, t); break;
                case 0x6D80: ReadEncodings(p, p + sz, t); break;
            }
            p += sz;
        }
    }

    private void ReadVideo(long p, long end, MatroskaTrack t)
    {
        while (p < end)
        {
            if (!ReadElementHeader(ref p, out uint id, out long sz)) break;
            switch (id)
            {
                case 0xB0: t.PixelWidth = (int)ReadUInt(p, sz); break;
                case 0xBA: t.PixelHeight = (int)ReadUInt(p, sz); break;
                case 0x54B0: t.DisplayWidth = (int)ReadUInt(p, sz); break;
                case 0x54BA: t.DisplayHeight = (int)ReadUInt(p, sz); break;
                case 0x54B2: t.DisplayUnit = (int)ReadUInt(p, sz); break;
                case 0x9A: t.FlagInterlaced = (int)ReadUInt(p, sz); break;
                case 0x9D: t.FieldOrder = (int)ReadUInt(p, sz); break;
                case 0x55B0:
                {
                    long q = p, e = p + sz;
                    while (q < e)
                    {
                        if (!ReadElementHeader(ref q, out uint cid, out long csz)) break;
                        if (cid == 0x55B1) t.MatrixCoefficients = (int)ReadUInt(q, csz);
                        else if (cid == 0x55B9) t.Range = (int)ReadUInt(q, csz);
                        q += csz;
                    }
                    break;
                }
            }
            p += sz;
        }
    }

    private void ReadAudio(long p, long end, MatroskaTrack t)
    {
        while (p < end)
        {
            if (!ReadElementHeader(ref p, out uint id, out long sz)) break;
            switch (id)
            {
                case 0xB5: t.SamplingFrequency = ReadFloat(p, sz); break;
                case 0x78B5: t.OutputSamplingFrequency = ReadFloat(p, sz); break;
                case 0x9F: t.Channels = (int)ReadUInt(p, sz); break;
                case 0x6264: t.BitDepth = (int)ReadUInt(p, sz); break;
            }
            p += sz;
        }
    }

    private void ReadEncodings(long p, long end, MatroskaTrack t)
    {
        while (p < end)
        {
            if (!ReadElementHeader(ref p, out uint id, out long sz)) break;
            if (id == 0x6240)
            {
                long q = p, e = p + sz;
                int type = 0, scope = 1, algo = 0; byte[]? settings = null;
                while (q < e)
                {
                    if (!ReadElementHeader(ref q, out uint cid, out long csz)) break;
                    if (cid == 0x5032) scope = (int)ReadUInt(q, csz);
                    else if (cid == 0x5033) type = (int)ReadUInt(q, csz);
                    else if (cid == 0x5034)
                    {
                        long r = q, re = q + csz;
                        while (r < re)
                        {
                            if (!ReadElementHeader(ref r, out uint gid, out long gsz)) break;
                            if (gid == 0x4254) algo = (int)ReadUInt(r, gsz);
                            else if (gid == 0x4255) settings = ReadBytes(r, (int)gsz);
                            r += gsz;
                        }
                    }
                    q += csz;
                }
                if (type == 0 && (scope & 1) != 0)
                {
                    if (algo == 3) t.StripHeader = settings ?? Array.Empty<byte>();
                    else if (algo == 0) t.Zlib = true;
                }
            }
            p += sz;
        }
    }

    private void ReadCues(long at)
    {
        long p = at;
        if (!ReadElementHeader(ref p, out uint id, out long size) || id != IdCues || size == Unknown) return;
        long end = p + size;
        while (p < end)
        {
            if (!ReadElementHeader(ref p, out uint cid, out long csz)) break;
            if (cid == 0xBB)
            {
                long q = p, e = p + csz, time = -1;
                var positions = new List<(long Track, long Pos)>();
                while (q < e)
                {
                    if (!ReadElementHeader(ref q, out uint gid, out long gsz)) break;
                    if (gid == 0xB3) time = (long)ReadUInt(q, gsz);
                    else if (gid == 0xB7)
                    {
                        long r = q, re = q + gsz, track = 0, pos = -1;
                        while (r < re)
                        {
                            if (!ReadElementHeader(ref r, out uint hid, out long hsz)) break;
                            if (hid == 0xF7) track = (long)ReadUInt(r, hsz);
                            else if (hid == 0xF1) pos = (long)ReadUInt(r, hsz);
                            r += hsz;
                        }
                        if (pos >= 0) positions.Add((track, pos));
                    }
                    q += gsz;
                }
                if (time >= 0) foreach (var (track, pos) in positions) _cues.Add((time, _segmentStart + pos, track));
            }
            p += csz;
        }
        _cues.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.ClusterPos.CompareTo(b.ClusterPos));
    }

    // ── フレームの読み出し ─────────────────────────────────

    // 直近の ReadFrame が false だったとき、Segment の終わりより手前で読めなくなったか（下位の読み出しが途中で 0 バイトを返した等。
    // 本物の終端ではない）。呼び出し側は開き直しの判断に使う
    public bool EndedEarly { get; private set; }

    // 次のフレーム。終わりで false。
    public bool ReadFrame(out MatroskaFrame frame)
    {
        EndedEarly = false;
        while (true)
        {
            while (_pending.Count > 0)
            {
                var f = _pending.Dequeue();
                if (Skip(f)) continue;
                frame = f;
                return true;
            }
            if (!ReadNextBlock())
            {
                EndedEarly = _pos < SegEnd && SegEnd != long.MaxValue && _pos < _length;
                frame = default;
                return false;
            }
        }
    }

    // シーク後の読み飛ばし（ffmpeg と同じ：字幕以外で Cue の時刻より前のブロックは捨てる。キーフレームを見るトラックは、
    // 最初のキーフレームまでの非キーも捨てる）
    private bool Skip(in MatroskaFrame f)
    {
        var t = Tracks[f.TrackIndex];
        if (_skipTo != long.MinValue && t.Type != 17)
        {
            if (f.HasTimestamp && f.Timestamp < _skipTo) return true;
            _skipTo = long.MinValue;
        }
        if (_skipToKey && f.TrackIndex == _skipKeyTrack)
        {
            if (!f.KeyFrame) return true;
            _skipToKey = false;
        }
        return false;
    }

    private bool ReadNextBlock()
    {
        long segEnd = SegEnd;
        while (true)
        {
            if (_pos >= segEnd || _pos >= _length) return false;
            if (_clusterEnd >= 0 && _pos >= _clusterEnd) _clusterEnd = -1;
            long at = _pos;
            long p = _pos;
            if (!ReadElementHeader(ref p, out uint id, out long size)) return false;
            if (id == IdCluster)
            {
                _clusterEnd = size == Unknown ? long.MaxValue : p + size;
                _clusterTime = 0;
                _pos = p;
                _currentClusterPos = at;
                continue;
            }
            if (_clusterEnd >= 0 && size == Unknown && id != IdBlockGroup)
            {
                // クラスタの中に大きさ不明の要素（壊れている）。読み進められない
                return false;
            }
            if (_clusterEnd == long.MaxValue && IsTopLevel(id))
            {
                // 大きさ不明のクラスタの終わり＝次の最上位の要素
                _clusterEnd = -1;
                if (size == Unknown) return false;
                _pos = p + size;
                continue;
            }
            if (size == Unknown) return false;
            long end = p + size;
            if (end > _length) return false;
            _pos = end;
            if (_clusterEnd < 0) continue;   // クラスタの外の要素（Cues・Tags など）は飛ばす
            switch (id)
            {
                case IdTimestamp:
                    _clusterTime = (long)ReadUInt(p, size);
                    if (!_seenClusters.ContainsKey(_clusterTime)) _seenClusters[_clusterTime] = _currentClusterPos;
                    break;
                case IdSimpleBlock:
                    if (ParseBlock(p, size, simple: true, -1, false, at)) return true;
                    break;
                case IdBlockGroup:
                {
                    long q = p, blockPos = -1, blockSize = 0, duration = -1;
                    bool hasRef = false;
                    while (q < end)
                    {
                        if (!ReadElementHeader(ref q, out uint cid, out long csz) || csz == Unknown) break;
                        if (cid == IdBlock) { blockPos = q; blockSize = csz; }
                        else if (cid == IdBlockDuration) duration = (long)ReadUInt(q, csz);
                        else if (cid == IdReferenceBlock) hasRef = true;
                        q += csz;
                    }
                    if (blockPos >= 0 && ParseBlock(blockPos, blockSize, simple: false, duration, !hasRef, at)) return true;
                    break;
                }
            }
        }
    }

    private long _currentClusterPos;

    private static bool IsTopLevel(uint id) => id is IdCluster or IdCues or IdTags or IdChapters or IdAttachments or IdSeekHead or IdInfo or IdTracks or IdSegment or IdEbml;

    // ブロックを読み、フレームを _pending へ足す。足したら true
    private bool ParseBlock(long p, long size, bool simple, long blockDuration, bool groupKey, long position)
    {
        if (size < 4) return false;
        int head = (int)Math.Min(size, 16);
        var hb = ReadBytes(p, head);
        int n = VintLength(hb[0]);
        if (n == 0 || n + 3 > head) return false;
        long trackNo = hb[0] & (0xFF >> n);
        for (int i = 1; i < n; i++) trackNo = trackNo << 8 | hb[i];
        if (!_byNumber.TryGetValue(trackNo, out var track)) return false;
        short rel = (short)(hb[n] << 8 | hb[n + 1]);
        int flags = hb[n + 2];
        bool key = simple ? (flags & 0x80) != 0 : groupKey;
        int lacing = (flags >> 1) & 3;
        long dataPos = p + n + 3, dataEnd = p + size;
        long ts = _clusterTime + rel;

        var sizes = new List<int>();
        if (lacing == 0) sizes.Add((int)(dataEnd - dataPos));
        else
        {
            var lb = ReadBytes(dataPos, (int)Math.Min(dataEnd - dataPos, 64 * 1024));
            int li = 0;
            int count = lb[li++] + 1;
            long total = dataEnd - dataPos - 1;
            if (lacing == 1)
            {
                long used = 0;
                for (int i = 0; i < count - 1; i++)
                {
                    int sz = 0, b;
                    do { b = lb[li++]; sz += b; } while (b == 255);
                    sizes.Add(sz); used += sz;
                }
                long rest = total - (li - 1) - used;
                if (rest < 0) return false;
                sizes.Add((int)rest);
            }
            else if (lacing == 3)
            {
                long used = 0;
                long first = ReadVintAt(lb, ref li, signed: false);
                sizes.Add((int)first); used += first;
                long prev = first;
                for (int i = 1; i < count - 1; i++)
                {
                    long d = ReadVintAt(lb, ref li, signed: true);
                    prev += d;
                    sizes.Add((int)prev); used += prev;
                }
                long rest = total - (li - 1) - used;
                if (rest < 0) return false;
                sizes.Add((int)rest);
            }
            else
            {
                if (total % count != 0) return false;
                for (int i = 0; i < count; i++) sizes.Add((int)(total / count));
            }
            dataPos += li;
        }

        // レーシングの各フレームの時刻（ffmpeg の matroskadec と同じ）：ブロックの長さ（BlockDuration、無ければ DefaultDuration×個数）を
        // (n+1)×長さ÷個数 − n×長さ÷個数 ずつ割り振る。長さが分からなければ、音声は形式から求めた 1 フレームの長さで進める
        //（ffmpeg は libavformat の共通の処理が同じことをする。求められない形式は時刻なし）。
        // 時刻は CodecDelay（Opus の pre-skip 等）を引いたもの（ffmpeg と同じ。トラックの単位へ四捨五入して引く）
        int laces = sizes.Count;
        long blockDur = blockDuration > 0 ? blockDuration
            : track.DefaultDurationNs > 0 ? track.DefaultDurationNs * laces / TimestampScaleNs : 0;
        ts -= DelayTicks(track);
        bool hasTs = true;
        long cur = dataPos;
        for (int i = 0; i < laces; i++)
        {
            long laceDur = blockDur > 0 ? blockDur * (i + 1) / laces - blockDur * i / laces : 0;
            int sz = sizes[i];
            if (sz < 0 || cur + sz > dataEnd) return _pending.Count > 0;
            var data = ReadBytes(cur, sz);
            cur += sz;
            if (track.Zlib) data = Inflate(data);
            if (track.StripHeader is { Length: > 0 } sh)
            {
                var d2 = new byte[sh.Length + data.Length];
                Buffer.BlockCopy(sh, 0, d2, 0, sh.Length);
                Buffer.BlockCopy(data, 0, d2, sh.Length, data.Length);
                data = d2;
            }
            if (laceDur == 0 && laces > 1 && track.Type == 2) laceDur = AudioFrameTicks(track, data);
            _pending.Enqueue(new MatroskaFrame(track.Index, ts, hasTs, laceDur, key, data, position));
            if (laceDur > 0) ts += laceDur; else hasTs = false;
        }
        return true;
    }

    private long DelayTicks(MatroskaTrack t)
        => t.CodecDelayNs > 0 ? (long)Math.Round((double)t.CodecDelayNs / TimestampScaleNs, MidpointRounding.AwayFromZero) : 0;

    // 音声の 1 フレームの長さ（TimestampScale の単位）。形式から求められなければ 0
    private long AudioFrameTicks(MatroskaTrack t, byte[] d)
    {
        long samples = AudioFrameSamples(t, d, out int rate);
        if (samples <= 0 || rate <= 0) return 0;
        return (long)Math.Round(samples * 1e9 / rate / TimestampScaleNs, MidpointRounding.AwayFromZero);
    }

    private static long AudioFrameSamples(MatroskaTrack t, byte[] d, out int rate)
    {
        rate = (int)Math.Round(t.SamplingFrequency);
        string c = t.CodecId;
        if (c == "A_OPUS")
        {
            rate = 48000;
            if (d.Length < 1) return 0;
            int toc = d[0], cfg = toc >> 3, code = toc & 3;
            double ms = cfg < 12 ? new[] { 10, 20, 40, 60 }[cfg & 3] : cfg < 16 ? new[] { 10, 20 }[cfg & 1] : new[] { 2.5, 5, 10, 20 }[cfg & 3];
            int frames = code == 0 ? 1 : code < 3 ? 2 : d.Length > 1 ? d[1] & 0x3F : 0;
            return (long)Math.Round(frames * ms * 48);
        }
        if (c.StartsWith("A_AAC", StringComparison.Ordinal)) return 1024;
        if (c == "A_AC3") return 1536;
        if (c == "A_EAC3")
        {
            if (d.Length < 5) return 0;
            int fscod = d[4] >> 6, numblkscod = (d[4] >> 4) & 3;
            return (fscod == 3 ? 6 : new[] { 1, 2, 3, 6 }[numblkscod]) * 256;
        }
        if (c is "A_MPEG/L3" or "A_MPEG/L2" or "A_MPEG/L1")
        {
            if (d.Length < 4 || d[0] != 0xFF || (d[1] & 0xE0) != 0xE0) return 0;
            int ver = (d[1] >> 3) & 3, layer = (d[1] >> 1) & 3;
            return layer == 3 ? 384 : layer == 2 ? 1152 : ver == 3 ? 1152 : 576;
        }
        if (c == "A_DTS")
        {
            if (d.Length < 6 || d[0] != 0x7F || d[1] != 0xFE || d[2] != 0x80 || d[3] != 0x01) return 0;
            int nblks = ((d[4] & 1) << 6) | (d[5] >> 2);
            return (nblks + 1) * 32;
        }
        if (c.StartsWith("A_PCM", StringComparison.Ordinal))
        {
            int bytes = Math.Max(1, t.BitDepth / 8) * Math.Max(1, t.Channels);
            return d.Length / bytes;
        }
        return 0;
    }

    private static byte[] Inflate(byte[] data)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return data; }
    }

    // ── シーク ───────────────────────────────────────────

    // ticks（TimestampScale の単位）以前の最も近いキーフレームへ。trackIndex はキーフレームを見るトラック（-1＝映像があれば映像）。
    public bool Seek(long ticks, int trackIndex = -1)
    {
        if (trackIndex < 0)
            foreach (var t in Tracks) if (t.Type == 1) { trackIndex = t.Index; break; }
        long trackNo = trackIndex >= 0 && trackIndex < Tracks.Count ? Tracks[trackIndex].Number : -1;
        long target = Math.Max(0, ticks);
        long clusterPos = _firstClusterPos, cueTime = 0;
        bool found = false;
        if (_cues.Count > 0)
        {
            // そのトラックの Cue（無ければどのトラックでも）で、目標以前の最後
            for (int pass = 0; pass < 2 && !found; pass++)
                for (int i = _cues.Count - 1; i >= 0; i--)
                {
                    var c = _cues[i];
                    if (pass == 0 && trackNo >= 0 && c.Track != trackNo) continue;
                    if (c.Time <= target) { clusterPos = c.ClusterPos; cueTime = c.Time; found = true; break; }
                }
        }
        else
        {
            // 読んだことのあるクラスタの位置から（目標以前の最後）
            for (int i = _seenClusters.Count - 1; i >= 0; i--)
                if (_seenClusters.Keys[i] <= target) { clusterPos = _seenClusters.Values[i]; cueTime = _seenClusters.Keys[i]; found = true; break; }
        }
        _pending.Clear();
        _clusterEnd = -1;
        _pos = clusterPos;
        // Cue の時刻（＝キーフレームの時刻）より前は捨て、キーフレームまで待つ。Cue が無いときは目標の時刻以前の最後のキーフレームを
        // 知らないので、目標までは捨てずに読み（呼び出し側が目標まで復号して捨てる）、最初のキーフレームから出す
        _skipTo = found ? cueTime : long.MinValue + 1;
        _skipToKey = true;
        _skipKeyTrack = trackIndex;
        return true;
    }

    // ── EBML の基本 ───────────────────────────────────────

    private readonly byte[] _hdr = new byte[12];

    private bool ReadElementHeader(ref long p, out uint id, out long size)
    {
        id = 0; size = 0;
        if (p >= _length) return false;
        int got = ReadAt(p, _hdr, 0, (int)Math.Min(12, _length - p));
        if (got < 2) return false;
        int idLen = VintLength(_hdr[0]);
        if (idLen == 0 || idLen > 4 || idLen >= got) return false;
        for (int i = 0; i < idLen; i++) id = id << 8 | _hdr[i];
        int sLen = VintLength(_hdr[idLen]);
        if (sLen == 0 || idLen + sLen > got) return false;
        long v = _hdr[idLen] & (0xFF >> sLen);
        bool allOnes = v == (0xFF >> sLen);
        for (int i = 1; i < sLen; i++) { v = v << 8 | _hdr[idLen + i]; if (_hdr[idLen + i] != 0xFF) allOnes = false; }
        size = allOnes ? Unknown : v;
        p += idLen + sLen;
        return true;
    }

    private static int VintLength(byte b)
    {
        for (int i = 0; i < 8; i++) if ((b & (0x80 >> i)) != 0) return i + 1;
        return 0;
    }

    private static long ReadVintAt(byte[] b, ref int i, bool signed)
    {
        int n = VintLength(b[i]);
        if (n == 0) throw new InvalidDataException("VINT が壊れている");
        long v = b[i] & (0xFF >> n);
        for (int k = 1; k < n; k++) v = v << 8 | b[i + k];
        i += n;
        if (signed) v -= (1L << (7 * n - 1)) - 1;
        return v;
    }

    private ulong ReadUInt(long p, long size)
    {
        if (size <= 0 || size > 8) return 0;
        var b = ReadBytes(p, (int)size);
        ulong v = 0;
        foreach (var x in b) v = v << 8 | x;
        return v;
    }

    private double ReadFloat(long p, long size)
    {
        var b = ReadBytes(p, (int)size);
        return size switch
        {
            4 => BinaryPrimitives.ReadSingleBigEndian(b),
            8 => BinaryPrimitives.ReadDoubleBigEndian(b),
            _ => double.NaN,
        };
    }

    private string ReadString(long p, long size)
    {
        var b = ReadBytes(p, (int)Math.Min(size, 1 << 16));
        int n = Array.IndexOf(b, (byte)0);
        return Encoding.UTF8.GetString(b, 0, n < 0 ? b.Length : n);
    }

    private byte[] ReadBytes(long p, int n)
    {
        if (n <= 0) return Array.Empty<byte>();
        var b = new byte[n];
        int got = ReadAt(p, b, 0, n);
        if (got < n) Array.Resize(ref b, Math.Max(0, got));
        return b;
    }

    // 読み出し（前回の続きなら Seek しない。小さい読みはまとめて先読みする）
    private readonly byte[] _buf = new byte[256 * 1024];
    private long _bufPos = -1;
    private int _bufLen;

    private int ReadAt(long p, byte[] dst, int off, int n)
    {
        int total = 0;
        while (n > 0)
        {
            if (_bufPos >= 0 && p >= _bufPos && p < _bufPos + _bufLen)
            {
                int k = (int)Math.Min(n, _bufPos + _bufLen - p);
                Buffer.BlockCopy(_buf, (int)(p - _bufPos), dst, off, k);
                p += k; off += k; n -= k; total += k;
                continue;
            }
            if (n >= _buf.Length)
            {
                // 大きい読みは直接
                if (_s.Position != p) _s.Position = p;
                int r = 0;
                while (r < n) { int g = _s.Read(dst, off + r, n - r); if (g <= 0) break; r += g; }
                return total + r;
            }
            if (_s.Position != p) _s.Position = p;
            int got = 0;
            while (got < _buf.Length) { int g = _s.Read(_buf, got, _buf.Length - got); if (g <= 0) break; got += g; }
            _bufPos = p; _bufLen = got;
            if (got == 0) break;
        }
        return total;
    }
}
