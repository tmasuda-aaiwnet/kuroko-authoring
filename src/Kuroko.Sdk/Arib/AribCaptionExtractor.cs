// 放送 TS から ARIB 字幕の PES を時刻（PTS）つきで取り出す（188 バイトの TS パケット）。
// 返すのは PES_packet_data_byte＝PES_data_field（data_identifier から始まる）。AribCaptionDecoder.Decode にそのまま渡せる。
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Kuroko.Arib;

public static class AribCaptionExtractor
{
    private const int PacketSize = 188;

    // onPes(pts90k, pesDataField)。PTS の無い PES は捨てる（字幕は同期型 PES で必ず PTS を持つ）。
    public static void Extract(Stream ts, int pid, Action<long, byte[]> onPes, CancellationToken ct = default)
    {
        var buf = new byte[PacketSize * 2048];
        var pes = new MemoryStream();
        bool collecting = false;
        int carry = 0;
        ts.Position = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = ts.Read(buf, carry, buf.Length - carry);
            int len = carry + read;
            if (len < PacketSize) break;
            int p = 0;
            while (p + PacketSize <= len)
            {
                if (buf[p] != 0x47)
                {
                    // 同期を取り直す（次の 0x47 で、さらに 188 先も 0x47 のところ）。
                    int q = p + 1;
                    while (q + PacketSize < len && !(buf[q] == 0x47 && buf[q + PacketSize] == 0x47)) q++;
                    p = q;
                    continue;
                }
                int pktPid = ((buf[p + 1] & 0x1F) << 8) | buf[p + 2];
                if (pktPid == pid)
                {
                    bool pusi = (buf[p + 1] & 0x40) != 0;
                    int afc = (buf[p + 3] >> 4) & 0x03;
                    int off = 4;
                    if (afc is 2 or 3) off += 1 + buf[p + 4];
                    if ((afc is 1 or 3) && off < PacketSize)
                    {
                        if (pusi)
                        {
                            if (collecting) Emit(pes, onPes);
                            pes.SetLength(0);
                            collecting = true;
                        }
                        if (collecting) pes.Write(buf, p + off, PacketSize - off);
                    }
                }
                p += PacketSize;
            }
            carry = len - p;
            if (carry > 0) Buffer.BlockCopy(buf, p, buf, 0, carry);
            if (read == 0) break;
        }
        if (collecting) Emit(pes, onPes);
    }

    private static void Emit(MemoryStream pes, Action<long, byte[]> onPes)
    {
        var b = pes.GetBuffer();
        int n = (int)pes.Length;
        if (n < 9 || b[0] != 0 || b[1] != 0 || b[2] != 1) return;
        int streamId = b[3];
        int pesLen = (b[4] << 8) | b[5];
        int end = pesLen > 0 ? Math.Min(n, 6 + pesLen) : n;
        // private_stream_2 等は拡張ヘッダが無い（PTS も無い）ので字幕ではない。
        if (streamId is 0xBC or 0xBE or 0xBF or 0xF0 or 0xF1 or 0xFF or 0xF2 or 0xF8) return;
        int flags = b[7];
        int hdrLen = b[8];
        int data = 9 + hdrLen;
        if ((flags & 0x80) == 0 || data > end) return;
        long pts = ((long)(b[9] & 0x0E) << 29) | ((long)b[10] << 22) | ((long)(b[11] & 0xFE) << 14)
                 | ((long)b[12] << 7) | ((long)b[13] >> 1);
        onPes(pts, b.AsSpan(data, end - data).ToArray());
    }

    // 33 ビットの PTS を、届いた順に**連続した値**へ直す（1 周＝約 26.5 時間のラップを何度跨いでも続く）。
    // 基準からの差を ±半周に丸めるだけ（Diff90k）では、基準から 13 時間を過ぎた時刻が負に化ける
    //（40 時間を超える録画で、後半の字幕が全部落ちた。40 時間の録画で見つかった）。
    // 前の値との差を積み上げる：字幕は時刻順に届くので、隣どうしの差は半周より十分小さい。
    public sealed class Unwrapper
    {
        private long _lastRaw = -1, _last;
        public Unwrapper(long origin90k) { _lastRaw = origin90k & ((1L << 33) - 1); _last = origin90k; }
        public long Next(long pts90k)
        {
            _last += Diff90k(pts90k, _lastRaw);
            _lastRaw = pts90k;
            return _last;
        }
    }

    // 33 ビットの PTS の差（90kHz）を ±半周（約 13 時間）に丸める。隣どうしの差に使う。
    public static long Diff90k(long pts90k, long base90k)
    {
        const long Wrap = 1L << 33;
        long d = (pts90k - base90k) % Wrap;
        if (d < 0) d += Wrap;
        if (d >= Wrap / 2) d -= Wrap;
        return d;
    }
}
