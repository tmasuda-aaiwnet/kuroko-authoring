// krk のテキスト型（charset "arib"、docs/kuroko-format-spec.md §11.1）の cue 1 つから、ARIB 字幕の字幕文データ
//（PES_data_field）を組み立て直す。再生器はこれを TS 再生と同じ描画器（同梱 ffmpeg の libaribcaption）へ渡して描く。
// こうすると、ARIB 外字入りのフォント・DRCS（既知の形の置き換えも含む）・位置の計算がすべて TS 再生と同じになる。
//
// AribCaptionDecoder（＝libaribcaption の解釈）で読み解くと**元の行・文字・書式に戻る**ように組む：
//   ・画面消去（CS）から始める。描画器の状態（符号集合・色・大きさ）がそこで既定へ戻るので、cue ごとに独立する。
//   ・行は点単位の位置指定（ACPS）。ACPS は「セルの左下」を指すので、行の Y にセルの高さを足して渡す。
//   ・文字は G0（漢字の集合）で表す。読み解き側と同じ変換（中サイズの半角化など）を全符号で試した逆引き表を使い、
//     元の文字に戻る符号を選ぶ。漢字の集合に無い字は英数（G1）・ひらがな（G2）で表し、どれにも無ければ〓。
//   ・文字色の 0〜7 番は libaribcaption が COL で受け付けない（読み解きが失敗する）ので BKF〜WHF で指定する。
//   ・DRCS は cue の中で使う形だけを DRCS 符号化（データユニット 0x30）で定義し、1 バイト DRCS の集合を G3 へ指示して SS3 で出す。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Kuroko.Arib;

public static class AribCaptionEncoder
{
    private static Dictionary<int, int>? _kanjiNormal, _kanjiMsz, _alnumNormal, _alnumMsz, _hiraNormal, _hiraMsz;

    private static void EnsureTables()
    {
        if (_kanjiNormal is not null) return;
        var kn = new Dictionary<int, int>(); var km = new Dictionary<int, int>();
        for (int ku = 0; ku < 94; ku++)
            for (int ten = 0; ten < 94; ten++)
            {
                int code = ((ku + 0x21) << 8) | (ten + 0x21);
                int un, um;
                if (ku < 84)
                {
                    int index = ku * 94 + ten;
                    un = AribCaptionTables.Kanji[index];
                    um = un;
                    if (ku < 2) um = AribCaptionTables.KanjiSymbolsHalfwidth[index];
                    if (um == 0x3000 || (um >= 0xFF01 && um <= 0xFF5E)) um = (um & 0xFF) + 0x20;
                }
                else un = um = AribCaptionTables.AdditionalSymbolsUnicode[(ku - 84) * 94 + ten];
                if (un != 0) kn.TryAdd(un, code);
                if (um != 0) km.TryAdd(um, code);
            }
        var an = new Dictionary<int, int>(); var am = new Dictionary<int, int>();
        var hn = new Dictionary<int, int>(); var hm = new Dictionary<int, int>();
        for (int ch = 0x21; ch < 0x7F; ch++)
        {
            an.TryAdd(AribCaptionTables.AlphanumericFullwidth[ch - 0x21], ch);
            am.TryAdd(AribCaptionTables.AlphanumericHalfwidth[ch - 0x21], ch);
            int h = AribCaptionTables.Hiragana[ch - 0x21];
            hn.TryAdd(h, ch);
            hm.TryAdd(ch >= 0x79 ? AribCaptionTables.KanaSymbolsHalfwidth[ch - 0x79] : h, ch);
        }
        _kanjiMsz = km; _alnumNormal = an; _alnumMsz = am; _hiraNormal = hn; _hiraMsz = hm;
        _kanjiNormal = kn;
    }

    // run の書式（引き継ぎを解いた後の値）。
    private sealed class RunState
    {
        public int W = 36, H = 36, Hs = 4, Vs = 24;
        public string Size = "normal", Color = "#FFFFFF", Bg = "#00000000", Edge = "none", Box = "", Flash = "none";
        public List<string> Deco = new();

        public RunState Apply(KurokoSubsTextRun r)
        {
            var s = (RunState)MemberwiseClone();
            s.Deco = new List<string>(Deco);
            if (r.W is int w) s.W = w;
            if (r.H is int h) s.H = h;
            if (r.Hs is int hs) s.Hs = hs;
            if (r.Vs is int vs) s.Vs = vs;
            if (r.Size is not null) s.Size = r.Size;
            if (r.Color is not null) s.Color = r.Color;
            if (r.Bg is not null) s.Bg = r.Bg;
            if (r.Edge is not null) s.Edge = r.Edge;
            if (r.Box is not null) s.Box = r.Box;
            if (r.Flash is not null) s.Flash = r.Flash;
            if (r.Deco is not null) s.Deco = new List<string>(r.Deco);
            return s;
        }

        public (float X, float Y) Scale => Size switch
        {
            "small" => (0.5f, 0.5f),
            "medium" => (0.5f, 1f),
            "tall" => (1f, 2f),
            "wide" => (2f, 1f),
            "double" => (2f, 2f),
            _ => (1f, 1f),
        };

        public int SectionHeight => (int)MathF.Floor((H + Vs) * Scale.Y);
        public bool Msz => Scale.X * 2 == Scale.Y;
    }

    // drcs：cue が使う DRCS の形（"U+F0000" → 形）。canvas：座標系（960×540 等）。
    public static byte[] Encode(KurokoSubsTextCue cue, KurokoSubsCanvas? canvas, IReadOnlyDictionary<int, KurokoSubsDrcs>? drcs)
    {
        EnsureTables();
        var body = new MemoryStream();
        body.WriteByte(0x0C); // CS：状態を既定へ戻す
        int swf = (canvas?.Width, canvas?.Height) switch
        {
            (1920, 1080) => 5,
            (720, 480) => 9,
            _ => 7,
        };
        if (swf != 7) Csi(body, swf.ToString(CultureInfo.InvariantCulture), 0x53);

        // DRCS：この cue で使う形に 1 バイト DRCS の符号を振る（集合 1..15 × 0x21..0x7E）。
        var drcsCode = new Dictionary<int, (int Set, int Ch)>();
        var drcsUnit = new MemoryStream();
        if (drcs is not null && cue.Lines is not null)
        {
            foreach (var line in cue.Lines)
                foreach (var run in line.Runs)
                    foreach (int cp in CodePoints(run.Text))
                    {
                        if (drcsCode.ContainsKey(cp) || !drcs.TryGetValue(cp, out var pat)) continue;
                        int n = drcsCode.Count;
                        if (n >= 15 * 94) continue;
                        var key = (1 + n / 94, 0x21 + n % 94);
                        drcsCode[cp] = key;
                        byte[] pixels = Convert.FromBase64String(pat.Pattern);
                        drcsUnit.WriteByte((byte)(0x40 + key.Item1));
                        drcsUnit.WriteByte((byte)key.Item2);
                        drcsUnit.WriteByte(1);                                   // number_of_font
                        drcsUnit.WriteByte((byte)(pat.Depth <= 1 ? 0x00 : 0x01)); // font_id 0 / mode
                        drcsUnit.WriteByte((byte)((1 << Math.Max(1, pat.Depth)) - 2)); // depth（階調数−2）
                        drcsUnit.WriteByte((byte)pat.Width);
                        drcsUnit.WriteByte((byte)pat.Height);
                        drcsUnit.Write(pixels, 0, pixels.Length);
                    }
        }

        int gl = 0;            // 今の GL（0＝G0 漢字、1＝G1 英数）
        int g3Drcs = -1;       // G3 に指示中の DRCS の集合番号
        foreach (var line in cue.Lines ?? new List<KurokoSubsTextLine>())
        {
            var st = new RunState();
            RunState? cur = null;
            bool positioned = false;
            foreach (var run in line.Runs)
            {
                var next = (cur ?? st).Apply(run);
                EmitStyle(body, cur, next);
                cur = next;
                if (!positioned)
                {
                    Csi(body, $"{line.X};{line.Y + next.SectionHeight}", 0x61); // ACPS
                    positioned = true;
                }
                foreach (int cp in CodePoints(run.Text))
                {
                    if (drcsCode.TryGetValue(cp, out var dc))
                    {
                        if (g3Drcs != dc.Set)
                        {
                            body.WriteByte(0x1B); body.WriteByte(0x2B); body.WriteByte(0x20); body.WriteByte((byte)(0x40 + dc.Set));
                            g3Drcs = dc.Set;
                        }
                        body.WriteByte(0x1D); body.WriteByte((byte)dc.Ch); // SS3
                        continue;
                    }
                    if (cp is 0x20 or 0x3000) { body.WriteByte(0x20); continue; } // SP（MSZ なら半角、他は全角の空白になる）
                    bool msz = next.Msz;
                    if ((msz ? _kanjiMsz! : _kanjiNormal!).TryGetValue(cp, out int kc))
                    {
                        if (gl != 0) { body.WriteByte(0x0F); gl = 0; } // LS0
                        body.WriteByte((byte)(kc >> 8)); body.WriteByte((byte)(kc & 0xFF));
                    }
                    else if ((msz ? _alnumMsz! : _alnumNormal!).TryGetValue(cp, out int ac))
                    {
                        if (gl != 1) { body.WriteByte(0x0E); gl = 1; } // LS1
                        body.WriteByte((byte)ac);
                    }
                    else if ((msz ? _hiraMsz! : _hiraNormal!).TryGetValue(cp, out int hc))
                    {
                        body.WriteByte((byte)(hc | 0x80)); // GR＝G2（ひらがな）
                    }
                    else
                    {
                        if (gl != 0) { body.WriteByte(0x0F); gl = 0; }
                        body.WriteByte(0x22); body.WriteByte(0x2E); // 〓
                    }
                }
            }
        }

        // データユニット：DRCS（あれば）→ 字幕文。
        var units = new MemoryStream();
        if (drcsUnit.Length > 0)
        {
            var d = new MemoryStream();
            d.WriteByte((byte)drcsCode.Count);
            drcsUnit.Position = 0; drcsUnit.CopyTo(d);
            WriteUnit(units, 0x30, d.ToArray());
        }
        WriteUnit(units, 0x20, body.ToArray());

        var caption = new MemoryStream();
        caption.WriteByte(0x3F);                                   // TMD=0（自由）＋予約
        int loop = (int)units.Length;
        caption.WriteByte((byte)(loop >> 16)); caption.WriteByte((byte)(loop >> 8)); caption.WriteByte((byte)loop);
        units.Position = 0; units.CopyTo(caption);

        var pes = new MemoryStream();
        pes.WriteByte(0x80); pes.WriteByte(0xFF); pes.WriteByte(0xF0); // data_identifier／private_stream_id／ヘッダ長 0
        int size = (int)caption.Length;
        pes.WriteByte(0x01 << 2);                                  // data_group_id＝1（第 1 言語の字幕文）、版 0
        pes.WriteByte(0); pes.WriteByte(0);                        // link／last_link
        pes.WriteByte((byte)(size >> 8)); pes.WriteByte((byte)size);
        caption.Position = 0; caption.CopyTo(pes);
        pes.WriteByte(0); pes.WriteByte(0);                        // CRC16（描画器は見ない）
        return pes.ToArray();
    }

    private static void EmitStyle(MemoryStream b, RunState? prev, RunState s)
    {
        if (prev is null || prev.W != s.W || prev.H != s.H) Csi(b, $"{s.W};{s.H}", 0x57);   // SSM
        if (prev is null || prev.Hs != s.Hs) Csi(b, s.Hs.ToString(CultureInfo.InvariantCulture), 0x58); // SHS
        if (prev is null || prev.Vs != s.Vs) Csi(b, s.Vs.ToString(CultureInfo.InvariantCulture), 0x59); // SVS
        if (prev is null || prev.Size != s.Size)
        {
            switch (s.Size)
            {
                case "small": b.WriteByte(0x88); break;                        // SSZ
                case "medium": b.WriteByte(0x89); break;                       // MSZ
                case "tall": b.WriteByte(0x8A); b.WriteByte(0x8B); b.WriteByte(0x41); break;
                case "wide": b.WriteByte(0x8A); b.WriteByte(0x8B); b.WriteByte(0x44); break;
                case "double": b.WriteByte(0x8A); b.WriteByte(0x8B); b.WriteByte(0x45); break;
                default: b.WriteByte(0x8A); break;                             // NSZ
            }
        }
        if (prev is null || prev.Color != s.Color)
        {
            var (p, i) = ClutIndex(s.Color);
            SetPalette(b, p);
            if (i < 8) b.WriteByte((byte)(0x80 + i));                          // BKF..WHF
            else { b.WriteByte(0x90); b.WriteByte((byte)(0x40 + i)); }         // COL 前景 8..15
        }
        if (prev is null || prev.Bg != s.Bg)
        {
            var (p, i) = ClutIndex(s.Bg);
            SetPalette(b, p);
            b.WriteByte(0x90); b.WriteByte((byte)(0x50 + i));                  // COL 背景
        }
        if (prev is null || prev.Edge != s.Edge)
        {
            if (s.Edge == "none") Csi(b, "0", 0x63);                           // ORN なし
            else { var (p, i) = ClutIndex(s.Edge); Csi(b, $"1;{p * 100 + i}", 0x63); }
        }
        if (prev is null || !SameDeco(prev.Deco, s.Deco))
        {
            b.WriteByte(s.Deco.Contains("underline") ? (byte)0x9A : (byte)0x99); // STL／SPL
            int mdf = (s.Deco.Contains("bold") ? 1 : 0) | (s.Deco.Contains("italic") ? 2 : 0);
            Csi(b, mdf.ToString(CultureInfo.InvariantCulture), 0x64);          // MDF
        }
        if (prev is null || prev.Box != s.Box)
        {
            int e = (s.Box.Contains('b') ? 1 : 0) | (s.Box.Contains('r') ? 2 : 0) | (s.Box.Contains('t') ? 4 : 0) | (s.Box.Contains('l') ? 8 : 0);
            b.WriteByte(0x97); b.WriteByte((byte)(0x40 | e));                  // HLC
        }
        if (prev is null || prev.Flash != s.Flash)
        {
            b.WriteByte(0x91); b.WriteByte(s.Flash switch { "normal" => 0x40, "inverted" => 0x47, _ => 0x4F }); // FLC
        }
    }

    // COL のパレット指定。色を指定するたびに出す（cue は短いので、省くより確実さを取る）。
    private static void SetPalette(MemoryStream b, int p)
    {
        b.WriteByte(0x90); b.WriteByte(0x20); b.WriteByte((byte)(0x40 + p));
    }

    private static bool SameDeco(List<string> a, List<string> b)
        => a.Count == b.Count && a.TrueForAll(b.Contains);

    // #RRGGBB / #RRGGBBAA → 128 色表の（パレット, 番号）。完全一致が無ければ最も近い色。
    private static (int Palette, int Index) ClutIndex(string hex)
    {
        uint rgba = ParseHex(hex);
        int best = 7; long bestD = long.MaxValue;
        for (int k = 0; k < AribCaptionTables.Clut.Length; k++)
        {
            uint c = AribCaptionTables.Clut[k];
            if (c == rgba) return (k / 16, k % 16);
            long d = 0;
            for (int sh = 0; sh < 32; sh += 8) { long x = ((c >> sh) & 0xFF) - (long)((rgba >> sh) & 0xFF); d += x * x; }
            if (d < bestD) { bestD = d; best = k; }
        }
        return (best / 16, best % 16);
    }

    private static uint ParseHex(string hex)
    {
        string h = hex.TrimStart('#');
        uint v = uint.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return h.Length == 6 ? (v << 8) | 0xFF : v;
    }

    private static void Csi(MemoryStream b, string param, byte final)
    {
        b.WriteByte(0x9B);
        foreach (char c in param) b.WriteByte((byte)c);
        b.WriteByte(0x20);
        b.WriteByte(final);
    }

    private static void WriteUnit(MemoryStream units, byte param, byte[] data)
    {
        units.WriteByte(0x1F); units.WriteByte(param);
        units.WriteByte((byte)(data.Length >> 16)); units.WriteByte((byte)(data.Length >> 8)); units.WriteByte((byte)data.Length);
        units.Write(data, 0, data.Length);
    }

    private static IEnumerable<int> CodePoints(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            int cp = char.ConvertToUtf32(s, i);
            if (char.IsHighSurrogate(s[i])) i++;
            yield return cp;
        }
    }

    // "U+F0000" → 0xF0000。
    public static int ParseCharSpec(string spec)
        => int.Parse(spec.StartsWith("U+", StringComparison.OrdinalIgnoreCase) ? spec[2..] : spec, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
