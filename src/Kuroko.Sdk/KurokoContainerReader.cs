using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Kuroko;

// .krk コンテナ内の1ファイル。DataOffset＝krk ファイル内の生データ先頭バイト（Stored は展開済みデータ、
// Deflate は圧縮データの先頭）。Length＝展開後の長さ。Compressed＝ZIP Deflate なら true。
//
// **DataOffset は遅延解決**（未解決は -1）。ZIP はデータ本体の位置を中央ディレクトリに持たず、ローカルヘッダの
// name/extra 長を足して初めて求まる（ローカルと中央で extra 長が違い得るので中央の値では代用できない）。
// 開いた時点で全件解決すると**エントリ数ぶんのランダム読み**になり、ファイル共有越しでは1本ごとに往復が
// 発生して致命的に遅い（例：1 万 6 千エントリの krk で再生開始まで 3 分以上）。
// 実際に読むエントリだけ、使うときに解決する（KurokoContainerReader.EnsureDataOffset）。
public sealed class KrkEntry
{
    public string Name = "";
    public long DataOffset = -1;   // -1＝未解決（初めて読むときに確定）
    public long Length;
    public long CompLength;   // 圧縮後の長さ（Stored は Length と同じ）
    public bool Compressed;
    public long LocalHeaderOffset = -1;   // ZIP のローカルヘッダ位置（遅延解決に使う）
}

// .krk（ZIP）を読む。中央ディレクトリを解析し、av.mkv 等の Stored（無圧縮）は DataOffset に境界付きのシーク可能な
// ストリームを被せる。JSON 等の Deflate は ReadAllBytes で展開する。
// 旧形式（version 1・2 の ISO9660 の容器）は読まない（仕様の対象外。開くと NotSupportedException）。
public sealed class KurokoContainerReader : IDisposable
{
    private readonly string _path;
    private readonly Dictionary<string, KrkEntry>? _zip;     // 新 ZIP krk（正規化名→entry）

    // 遅延解決と小さいエントリ読みで使う**共有ハンドル**（開き直さない）。ローカルヘッダ位置の昇順索引つき。
    // 1回の解決で **64KB ぶんまとめて読み、その範囲に入る未解決エントリを全部解決する**（近接エントリは
    // 連続して置かれるので、字幕 cue のように大量に読む経路でも往復回数が激減する）。
    private readonly List<KrkEntry>? _byLocalHeader;
    private readonly object _ioLock = new();
    private Stream? _sharedStream;
    private const int ResolveBlockBytes = 64 * 1024;

    // 共有ハンドル（遅延解決・小さいエントリの読み取りで使い回す）。**ファイル共有越しは往復回数が効く**ので、
    // 呼ばれるたびに開き直さない。bufferSize:1＝FileStream の内部バッファを使わず、要求した範囲だけ読む。
    private Stream SharedStream()
        => _sharedStream ??= KurokoIo.OpenRead(_path, bufferSize: 1);

    // [offset, +length) を **1回の読み**でメモリへ。短く終わったぶんは切り詰めて返す。
    private byte[] ReadRange(long offset, long length)
    {
        if (length < 0 || length > int.MaxValue)
            throw new IOException($"krk: エントリが大きすぎてメモリへ読めません（{length} バイト）。");
        var buf = new byte[(int)length];
        lock (_ioLock)
        {
            var fs = SharedStream();
            fs.Seek(offset, SeekOrigin.Begin);
            int got = 0;
            while (got < buf.Length)
            {
                int n = fs.Read(buf, got, buf.Length - got);
                if (n <= 0) break;
                got += n;
            }
            if (got < buf.Length) Array.Resize(ref buf, got);
        }
        return buf;
    }

    public string ImagePath => _path;
    public bool IsZip => _zip is not null;

    private KurokoContainerReader(string path, Dictionary<string, KrkEntry>? zip)
    {
        _path = path; _zip = zip;
        if (zip is not null)
        {
            _byLocalHeader = new List<KrkEntry>(zip.Values);
            _byLocalHeader.Sort((a, b) => a.LocalHeaderOffset.CompareTo(b.LocalHeaderOffset));
        }
    }

    // このエントリの DataOffset を確定させる（未解決なら 1 回だけ読む）。
    private void EnsureDataOffset(KrkEntry e)
    {
        if (e.DataOffset >= 0 || e.LocalHeaderOffset < 0 || _byLocalHeader is null) return;
        lock (_ioLock)
        {
            if (e.DataOffset >= 0) return;
            var fs = SharedStream();

            long from = e.LocalHeaderOffset;
            int want = (int)Math.Min(ResolveBlockBytes, Math.Max(30, fs.Length - from));
            var block = new byte[want];
            fs.Seek(from, SeekOrigin.Begin);
            int got = 0;
            while (got < want)
            {
                int n = fs.Read(block, got, want - got);
                if (n <= 0) break;
                got += n;
            }

            // 読んだ範囲に入る未解決エントリをまとめて解決する（同じ往復を使い回す）。
            int i = _byLocalHeader.BinarySearch(e, Comparer<KrkEntry>.Create(
                (a, b) => a.LocalHeaderOffset.CompareTo(b.LocalHeaderOffset)));
            if (i < 0) i = ~i;
            for (; i < _byLocalHeader.Count; i++)
            {
                var t = _byLocalHeader[i];
                int o = (int)(t.LocalHeaderOffset - from);
                if (o < 0) continue;
                if (o + 30 > got) break;             // ブロックの外＝次の機会に解決する
                if (t.DataOffset >= 0) continue;
                int lNameLen = block[o + 26] | (block[o + 27] << 8);
                int lExtraLen = block[o + 28] | (block[o + 29] << 8);
                t.DataOffset = t.LocalHeaderOffset + 30 + lNameLen + lExtraLen;
            }

            // 保険：ブロックから求まらなかった（境界に掛かった）ときは、そのエントリだけ最小読みで解決する。
            if (e.DataOffset < 0)
            {
                var lh = new byte[4];
                fs.Seek(e.LocalHeaderOffset + 26, SeekOrigin.Begin);
                ReadFull(fs, lh, 0, 4);
                int lNameLen = lh[0] | (lh[1] << 8);
                int lExtraLen = lh[2] | (lh[3] << 8);
                e.DataOffset = e.LocalHeaderOffset + 30 + lNameLen + lExtraLen;
            }
        }
    }

    public static KurokoContainerReader Open(string path)
    {
        bool isZip;
        using (var fs = KurokoIo.OpenRead(path))
        {
            var sig = new byte[4];
            int got = fs.Read(sig, 0, 4);
            isZip = got == 4 && sig[0] == 0x50 && sig[1] == 0x4B && (sig[2] == 0x03 || sig[2] == 0x05 || sig[2] == 0x07);
        }
        if (!isZip)
            throw new NotSupportedException("この krk は旧形式（ISO の容器。version 1・2）で、対応していません。変換し直してください。");
        return new KurokoContainerReader(path, ParseZip(path));
    }

    // 全エントリ列挙（ZIP のみ。旧 ISO krk は未対応で空）。復旧でセグメント一覧を得るのに使う。
    public IEnumerable<KrkEntry> Entries => _zip is null ? System.Linq.Enumerable.Empty<KrkEntry>() : _zip.Values;

    // まとめ読み（ReadAllBytesBatch）へ渡すエントリを探す。データの位置は解決しない（まとめ読みがローカルヘッダを
    // 自分で読んで決める）。FindEntry は 1 件ごとに位置を確かめる読み出し（EnsureDataOffset）をするので、字幕の cue
    // （千数百件）を探すだけでファイル源（SMB）の往復が数十〜百回になる。
    public KrkEntry? FindEntryForBatch(string path)
        => _zip is not null ? (_zip.TryGetValue(Norm(path), out var e) ? e : null) : FindEntry(path);

    public KrkEntry? FindEntry(string path)
    {
        if (_zip is not null)
        {
            _zip.TryGetValue(Norm(path), out var e);
            if (e is not null) EnsureDataOffset(e);
            return e;
        }
        return null;
    }

    public byte[] ReadAllBytes(KrkEntry e)
    {
        EnsureDataOffset(e);
        // 共有ハンドルで **1 回の読み**にまとめる。以前は呼ばれるたびに FileStream を開き直し、Deflate も
        // 4KB ずつ小分けに読んでいたため、ファイル共有越しでは 1 エントリで何往復も発生していた。
        long raw = !e.Compressed ? e.Length : (e.CompLength > 0 ? e.CompLength : e.Length);
        var bytes = ReadRange(e.DataOffset, raw);
        if (!e.Compressed) return bytes;

        using var ms = new MemoryStream(bytes, writable: false);
        using var inflate = new DeflateStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream(e.Length > 0 && e.Length <= int.MaxValue ? (int)e.Length : 0);
        inflate.CopyTo(outMs);
        return outMs.ToArray();
    }

    // 複数エントリを**まとめて読む**。近接したエントリを連続範囲へ束ね、1 回の読みでローカルヘッダごと
    // 取り込んでから各エントリを取り出す（Deflate はメモリ上で展開）。
    //
    // 字幕 cue のように「小さいファイルが数百〜千数百本、連続して置かれている」経路のためのもの。
    // 1 本ずつ読むとファイル共有越しでは 1 本あたり往復が発生し、十数秒かかることがある。
    // 戻り値はエントリ名（正規化）→ 中身。読めなかったものは入らない。
    //
    // 束ねるのは「前のエントリの終わりから次のエントリまでのすき間が maxGapBytes 以下」のときだけ。
    // title.json のように、間に大きなエントリ（字幕画像など）を挟んで散らばったものを束ねると、すき間ごと
    // 読んでしまう（BD 由来の 65 タイトルの krk で title.json に 35MB を読み、SMB で 2 秒かかっていた）。
    public Dictionary<string, byte[]> ReadAllBytesBatch(IReadOnlyList<KrkEntry> entries, int maxChunkBytes = 8 << 20,
                                                        int maxGapBytes = 256 << 10)
    {
        var result = new Dictionary<string, byte[]>(entries.Count, StringComparer.OrdinalIgnoreCase);
        if (entries.Count == 0) return result;
        if (_zip is null)   // 旧 ISO krk：束ねる意味が無いので素直に1本ずつ
        {
            foreach (var e in entries) result[Norm(e.Name)] = ReadAllBytes(e);
            return result;
        }

        // ローカルヘッダ位置の昇順に。各エントリが占める最大長（ヘッダ＋名前＋extra 上限＋データ）で範囲を見積もる。
        var sorted = new List<KrkEntry>(entries);
        sorted.Sort((a, b) => a.LocalHeaderOffset.CompareTo(b.LocalHeaderOffset));
        long Span(KrkEntry e) => 30 + Encoding.UTF8.GetByteCount(e.Name) + 512
                                 + (e.Compressed ? e.CompLength : e.Length);

        for (int i = 0; i < sorted.Count;)
        {
            long from = sorted[i].LocalHeaderOffset;
            if (from < 0) { result[Norm(sorted[i].Name)] = ReadAllBytes(sorted[i]); i++; continue; }
            long to = from + Span(sorted[i]);
            int j = i + 1;
            while (j < sorted.Count && sorted[j].LocalHeaderOffset >= 0
                   && sorted[j].LocalHeaderOffset - to <= maxGapBytes
                   && sorted[j].LocalHeaderOffset + Span(sorted[j]) - from <= maxChunkBytes)
            {
                to = Math.Max(to, sorted[j].LocalHeaderOffset + Span(sorted[j]));
                j++;
            }
            var block = ReadRange(from, Math.Min(to - from, maxChunkBytes));

            for (int k = i; k < j; k++)
            {
                var e = sorted[k];
                int o = (int)(e.LocalHeaderOffset - from);
                if (o < 0 || o + 30 > block.Length) { result[Norm(e.Name)] = ReadAllBytes(e); continue; }
                int lNameLen = block[o + 26] | (block[o + 27] << 8);
                int lExtraLen = block[o + 28] | (block[o + 29] << 8);
                int dataAt = o + 30 + lNameLen + lExtraLen;
                long rawLen = e.Compressed ? (e.CompLength > 0 ? e.CompLength : e.Length) : e.Length;
                if (e.DataOffset < 0) e.DataOffset = e.LocalHeaderOffset + 30 + lNameLen + lExtraLen;
                if (dataAt < 0 || rawLen < 0 || dataAt + rawLen > block.Length)
                {
                    result[Norm(e.Name)] = ReadAllBytes(e);   // 束から外れた（見積もり超過）＝個別に読む
                    continue;
                }
                if (!e.Compressed)
                {
                    var raw = new byte[rawLen];
                    Buffer.BlockCopy(block, dataAt, raw, 0, (int)rawLen);
                    result[Norm(e.Name)] = raw;
                }
                else
                {
                    using var ms = new MemoryStream(block, dataAt, (int)rawLen, writable: false);
                    using var inflate = new DeflateStream(ms, CompressionMode.Decompress);
                    using var outMs = new MemoryStream(e.Length > 0 && e.Length <= int.MaxValue ? (int)e.Length : 0);
                    inflate.CopyTo(outMs);
                    result[Norm(e.Name)] = outMs.ToArray();
                }
            }
            i = j;
        }
        return result;
    }

    // シーク可能サブストリーム。Stored は krk ファイルの [DataOffset, +Length) を境界付きで（専用ハンドル）。
    // Deflate は展開してメモリ（av.mkv は Stored 前提なのでここは通常来ない）。
    public Stream OpenRead(KrkEntry e)
    {
        EnsureDataOffset(e);
        if (!e.Compressed)
        {
            var fs = KurokoIo.OpenRead(_path);
            return new KurokoBoundedStream(fs, e.DataOffset, e.Length, leaveOpen: false, sharedLock: null);
        }
        return new MemoryStream(ReadAllBytes(e));
    }

    public void Dispose()
    {
        lock (_ioLock) { try { _sharedStream?.Dispose(); } catch { } _sharedStream = null; }
    }

    // 指定プレフィックス（ディレクトリ）配下の全エントリの展開後バイト合計。字幕 subs/ の容量集計等に使う。
    // 旧 ISO krk は subs/ を持たないので 0（ZIP のみ集計）。件数も out で返す。
    public long SumBytesUnder(string prefix, out int count)
        => SumBytesUnder(prefix, out count, out _);

    // 戻り値＝中身の合計（非圧縮）。stored ＝**実際に .krk が占めている大きさ**（圧縮後）。
    // 字幕の実体（.pgs / .spu / .png）は ZIP で Deflate されるので両者が大きく違う
    //（例：BD 由来の krk の .pgs は 466MB → 205MB）。ファイルサイズと突き合わせる集計には stored を使う。
    public long SumBytesUnder(string prefix, out int count, out long stored)
    {
        count = 0; stored = 0;
        if (_zip is null) return 0;
        string np = Norm(prefix).TrimEnd('/') + "/";
        long sum = 0;
        foreach (var kv in _zip)
            if (kv.Key.StartsWith(np, System.StringComparison.OrdinalIgnoreCase))
            {
                sum += kv.Value.Length;
                stored += kv.Value.CompLength > 0 ? kv.Value.CompLength : kv.Value.Length;
                count++;
            }
        return sum;
    }

    private static string Norm(string p) => p.Replace('\\', '/').TrimStart('/');

    // ── ZIP 解析（中央ディレクトリ＋ZIP64） ─────────────────────────────────────────
    private static Dictionary<string, KrkEntry> ParseZip(string path)
    {
        using var fs = KurokoIo.OpenRead(path);
        long len = fs.Length;
        int back = (int)Math.Min(len, 65557); // EOCD(22)＋最大コメント(65535)
        var tail = new byte[back];
        fs.Seek(len - back, SeekOrigin.Begin);
        ReadFull(fs, tail, 0, back);
        int eocd = -1;
        for (int i = back - 22; i >= 0; i--)
            if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException("ZIP EOCD が見つかりません。");

        long count = U16(tail, eocd + 10);
        long cdSize = U32(tail, eocd + 12);
        long cdOffset = U32(tail, eocd + 16);

        // ZIP64：EOCD の値が 0xFFFF/0xFFFFFFFF なら ZIP64 EOCD ロケータ→ZIP64 EOCD から 64bit で取り直す。
        if (count == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF)
        {
            long locPos = (len - back) + eocd - 20;
            if (locPos >= 0)
            {
                var loc = new byte[20];
                fs.Seek(locPos, SeekOrigin.Begin);
                ReadFull(fs, loc, 0, 20);
                if (loc[0] == 0x50 && loc[1] == 0x4B && loc[2] == 0x06 && loc[3] == 0x07)
                {
                    long z64 = (long)U64(loc, 8);
                    var z = new byte[56];
                    fs.Seek(z64, SeekOrigin.Begin);
                    ReadFull(fs, z, 0, 56);
                    if (z[0] == 0x50 && z[1] == 0x4B && z[2] == 0x06 && z[3] == 0x06)
                    {
                        count = (long)U64(z, 32);
                        cdSize = (long)U64(z, 40);
                        cdOffset = (long)U64(z, 48);
                    }
                }
            }
        }

        var cd = new byte[cdSize];
        fs.Seek(cdOffset, SeekOrigin.Begin);
        ReadFull(fs, cd, 0, (int)cdSize);

        var map = new Dictionary<string, KrkEntry>(StringComparer.OrdinalIgnoreCase);
        int p = 0;
        for (long n = 0; n < count && p + 46 <= cd.Length; n++)
        {
            if (U32(cd, p) != 0x02014b50) break; // 中央ディレクトリ record シグネチャ
            int method = U16(cd, p + 10);
            long compSize = U32(cd, p + 20);
            long uncompSize = U32(cd, p + 24);
            int nameLen = U16(cd, p + 28);
            int extraLen = U16(cd, p + 30);
            int commentLen = U16(cd, p + 32);
            long lho = U32(cd, p + 42);
            string name = Encoding.UTF8.GetString(cd, p + 46, nameLen);

            // ZIP64 拡張フィールド（header id 0x0001）から 64bit の値を取り直す（0xFFFFFFFF の項目のみ順に格納）。
            if (uncompSize == 0xFFFFFFFF || compSize == 0xFFFFFFFF || lho == 0xFFFFFFFF)
            {
                int ep = p + 46 + nameLen, eEnd = ep + extraLen;
                while (ep + 4 <= eEnd)
                {
                    int hid = U16(cd, ep), hsz = U16(cd, ep + 2); int q = ep + 4;
                    if (hid == 0x0001)
                    {
                        if (uncompSize == 0xFFFFFFFF && q + 8 <= eEnd) { uncompSize = (long)U64(cd, q); q += 8; }
                        if (compSize == 0xFFFFFFFF && q + 8 <= eEnd) { compSize = (long)U64(cd, q); q += 8; }
                        if (lho == 0xFFFFFFFF && q + 8 <= eEnd) { lho = (long)U64(cd, q); q += 8; }
                        break;
                    }
                    ep += 4 + hsz;
                }
            }

            p += 46 + nameLen + extraLen + commentLen;
            if (name.EndsWith("/")) continue; // ディレクトリエントリは無視

            // データ先頭はローカルヘッダ（30固定＋name＋extra）の直後だが、**ここでは読まない**。
            // 全件読むとエントリ数ぶんのランダム読みになり、ファイル共有越しで致命的に遅くなる（KrkEntry の説明）。
            map[Norm(name)] = new KrkEntry
            {
                Name = name, DataOffset = -1, LocalHeaderOffset = lho,
                Length = uncompSize, CompLength = compSize, Compressed = method != 0,
            };
        }
        return map;
    }

    private static void ReadFull(Stream s, byte[] b, int off, int len)
    {
        int got = 0;
        while (got < len)
        {
            int n = s.Read(b, off + got, len - got);
            if (n <= 0) throw new EndOfStreamException();
            got += n;
        }
    }
    private static int U16(byte[] b, int o) => b[o] | (b[o + 1] << 8);
    private static long U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    private static ulong U64(byte[] b, int o)
    {
        ulong v = 0;
        for (int i = 7; i >= 0; i--) v = (v << 8) | b[o + i];
        return v;
    }
}
