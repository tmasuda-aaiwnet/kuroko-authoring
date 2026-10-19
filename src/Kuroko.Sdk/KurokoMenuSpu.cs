using System.Collections.Generic;

namespace Kuroko;

// v3 メニューの C<nn>.spu（ボタン図柄SPU）を解析する。中身は変換器 DvdH264FullConverter.BuildSubsPs が書いた
// 小さな MPEG-PS で、private_stream_1（0xBD）の substream 0x20..0x3F に、表示モード別の物理サブピクチャを
// PTS（セル 0起点・90kHz）付きで格納している。ここではそれを「物理サブID → 時刻付きSPU列」へ復号する。
// 再生側は menu.json の SPST_CTL（表示モード別物理番号）で物理IDを選び、時刻で該当SPUを DvdSpuDecoder に渡す。
public static class KurokoMenuSpu
{
    // 1つの SPU 図柄（DvdSpuDecoder に渡す生バイト＝SPDSZ 始まり）と、その表示開始時刻(ms・セル0起点)。
    public readonly record struct Entry(double StartMs, byte[] Data);

    // C<nn>.spu の PS を解析。空配列/解析不能なら空辞書。物理サブID（0x20+n）→ 時刻昇順の SPU 列。
    public static Dictionary<int, List<Entry>> Parse(byte[] ps)
    {
        var result = new Dictionary<int, List<Entry>>();
        if (ps.Length < 4) return result;

        // 組み立て中SPU（物理ID別）。BuildSubsPs は 1SPU を PTS付きPESで開始し、SPDSZ 到達で確定する。
        var asmBuf = new Dictionary<int, List<byte>>();
        var asmNeed = new Dictionary<int, int>();
        var asmPts = new Dictionary<int, long>();

        void Emit(int sid)
        {
            if (!asmBuf.TryGetValue(sid, out var b) || b.Count == 0) return;
            int need = asmNeed.TryGetValue(sid, out var n) && n > 0 ? n : b.Count;
            int take = System.Math.Min(need, b.Count);
            var data = new byte[take];
            b.CopyTo(0, data, 0, take);
            long pts = asmPts.TryGetValue(sid, out var p) ? p : 0;
            if (!result.TryGetValue(sid, out var lst)) result[sid] = lst = new List<Entry>();
            lst.Add(new Entry(pts / 90.0, data)); // 90kHz → ms
            b.Clear(); asmNeed[sid] = 0;
        }

        void Feed(byte[] body)
        {
            if (body.Length < 4 || (body[0] & 0xC0) != 0x80) return; // PES拡張の '10' マーカ
            int flags = body[1];
            int hdrLen = body[2];
            int payOff = 3 + hdrLen;
            if (payOff >= body.Length) return;
            long? pts = null;
            if ((flags & 0x80) != 0 && payOff >= 8)
                pts = ((long)(body[3] & 0x0E) << 29) | ((long)body[4] << 22) | ((long)(body[5] & 0xFE) << 14)
                    | ((long)body[6] << 7) | ((long)(body[7] & 0xFE) >> 1);
            int sid = body[payOff];
            if (sid < 0x20 || sid > 0x3F) return;
            int dataOff = payOff + 1;
            if (body.Length - dataOff <= 0) return;

            if (pts.HasValue)
            {
                Emit(sid); // 直前の未完SPUを確定（PTS付きPES＝新SPU開始）
                var nb = new List<byte>(body.Length - dataOff);
                for (int i = dataOff; i < body.Length; i++) nb.Add(body[i]);
                asmBuf[sid] = nb; asmPts[sid] = pts.Value;
                asmNeed[sid] = nb.Count >= 2 ? (nb[0] << 8) | nb[1] : 0;
            }
            else
            {
                if (!asmBuf.TryGetValue(sid, out var b)) return; // 開始PES未見の継続は捨てる
                for (int i = dataOff; i < body.Length; i++) b.Add(body[i]);
                if ((!asmNeed.TryGetValue(sid, out var nn) || nn == 0) && b.Count >= 2) asmNeed[sid] = (b[0] << 8) | b[1];
            }
            if (asmBuf.TryGetValue(sid, out var bb) && asmNeed.TryGetValue(sid, out var need2) && need2 > 0 && bb.Count >= need2)
                Emit(sid);
        }

        int pos = 0;
        int Rd() => pos < ps.Length ? ps[pos++] : -1;
        byte[]? ReadN(int n)
        {
            if (pos + n > ps.Length) { pos = ps.Length; return null; }
            var buf = new byte[n];
            System.Array.Copy(ps, pos, buf, 0, n); pos += n; return buf;
        }
        void SkipN(int n) => pos = System.Math.Min(ps.Length, pos + n);

        int b0 = Rd(), b1 = Rd(), b2 = Rd();
        while (b2 != -1)
        {
            if (!(b0 == 0 && b1 == 0 && b2 == 1)) { b0 = b1; b1 = b2; b2 = Rd(); continue; }
            int id = Rd();
            if (id < 0) break;
            if (id == 0xBA) { var h = ReadN(10); if (h == null) break; SkipN(h[9] & 0x07); }
            else if (id == 0xB9) break;
            else if (id == 0xBD)
            {
                int hi = Rd(), lo = Rd(); if (lo < 0) break;
                var body = ReadN((hi << 8) | lo); if (body == null) break;
                Feed(body);
            }
            else if (id >= 0xBB) { int hi = Rd(), lo = Rd(); if (lo < 0) break; SkipN((hi << 8) | lo); }
            b0 = Rd(); b1 = Rd(); b2 = Rd();
        }
        foreach (var sid in new List<int>(asmBuf.Keys)) Emit(sid);
        foreach (var lst in result.Values) lst.Sort((x, y) => x.StartMs.CompareTo(y.StartMs));
        return result;
    }
}
