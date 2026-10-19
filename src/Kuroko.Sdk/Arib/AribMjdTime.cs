using System;

namespace Kuroko.Arib;

// ARIB/DVB の日付時刻表現「MJD(修正ユリウス日・16bit) ＋ BCD の hhmmss」を DateTime へ直す。
// TOT(PID 0x14, table 0x73) の JST_time と EIT の start_time が同じ形式で、ARIB では **JST そのもの**
// （UTC ではない）が入る。MJD が 16bit なので 2038-04-22 までしか表せない。
// 変換式は ARIB STD-B10 附属（DVB EN 300 468 Annex C と同じ）。
public static class AribMjdTime
{
    // b[o..o+4] の 5 バイトを読む。未定義(0x0000/0xFFFF)や不正値は null。
    public static DateTime? Parse(ReadOnlySpan<byte> b, int o)
    {
        if (o < 0 || o + 5 > b.Length) return null;
        int mjd = (b[o] << 8) | b[o + 1];
        if (mjd is 0 or 0xFFFF) return null;
        int hh = FromBcd(b[o + 2]), mm = FromBcd(b[o + 3]), ss = FromBcd(b[o + 4]);
        int yP = (int)((mjd - 15078.2) / 365.25);
        int mP = (int)((mjd - 14956.1 - (int)(yP * 365.25)) / 30.6001);
        int day = mjd - 14956 - (int)(yP * 365.25) - (int)(mP * 30.6001);
        int k = (mP == 14 || mP == 15) ? 1 : 0;
        int year = yP + k + 1900, month = mP - 1 - k * 12;
        try
        {
            return new DateTime(year, month, day,
                Math.Clamp(hh, 0, 23), Math.Clamp(mm, 0, 59), Math.Clamp(ss, 0, 59),
                DateTimeKind.Unspecified);
        }
        catch { return null; }
    }

    public static int FromBcd(byte v) => ((v >> 4) & 0x0F) * 10 + (v & 0x0F);
}
