// ARIB 字幕（ARIB STD-B24 第 1 編第 3 部）の字幕文データを、**表示 1 回ぶんの文字の並び（位置・書式つき）**へ読み解く。
// TS→krk 変換で字幕をテキスト型（docs/kuroko-format-spec.md §11.1、charset "arib"）へ写すために使う。
//
// TS 再生（同梱 ffmpeg の libaribcaption）と**同じ見え方**にするのが目的なので、libaribcaption の
// src/decoder/decoder_impl.cpp（MIT、Copyright (C) 2021 magicxqq）の解釈を、日本の放送（8 単位符号・プロファイル A）の
// 範囲でそのまま写している（位置の計算、MSZ のときの半角への置き換え、無視する制御符号まで同じ）。
// 表は AribCaptionTables（同じく libaribcaption から生成）。
//
// 違い：
//   ・DRCS は libaribcaption が MD5 で既知の字へ置き換えるが、ここでは**置き換えずに形（パターン）のまま**返す。
//     再生時に組み立て直して libaribcaption へ渡すので、置き換えはそこで同じように起きる。
//   ・FLC（点滅）は libaribcaption では読み捨てだが、ここでは記録だけする（描画には使わない）。
//   ・UTF-8／ラテン文字の方式（ブラジル・フィリピン）とプロファイル C（ワンセグ）は扱わない。
using System;
using System.Collections.Generic;

namespace Kuroko.Arib;

public sealed class AribDrcsPattern
{
    public int Width, Height;
    public int Depth;       // 階調数（2＝2 階調、4＝4 階調）
    public int DepthBits;   // 1 画素のビット数（1 または 2）
    public byte[] Pixels = Array.Empty<byte>(); // ARIB のパターンデータそのまま（左上から行順に詰めたビット列）
}

public sealed class AribCaptionChar
{
    public int Codepoint;               // Unicode（DRCS のときは 0）
    public AribDrcsPattern? Drcs;       // DRCS の字なら形
    public int X, Y;                    // セルの左上（canvas 座標）
    public int CharWidth, CharHeight, HSpacing, VSpacing;
    public float ScaleX = 1f, ScaleY = 1f;
    public uint TextColor, BackColor, StrokeColor; // RGBA
    public bool Underline, Bold, Italic, Stroke;
    public int Enclosure;               // HLC の下位 4 ビット（下・右・上・左）
    public int Flash;                   // 0＝なし、1＝通常、2＝反転

    public int SectionWidth => (int)MathF.Floor((CharWidth + HSpacing) * ScaleX);
    public int SectionHeight => (int)MathF.Floor((CharHeight + VSpacing) * ScaleY);
}

public sealed class AribCaptionRegion
{
    public int X, Y, Width, Height;
    public bool IsRuby;
    public readonly List<AribCaptionChar> Chars = new();
}

public sealed class AribCaption
{
    public bool ClearScreen;
    public int WaitMs;                  // TIME で指定された表示時間。0＝次の字幕まで
    public int PlaneWidth = 960, PlaneHeight = 540;
    public readonly List<AribCaptionRegion> Regions = new();
}

public sealed class AribCaptionDecoder
{
    private enum GSet
    {
        Kanji, Alphanumeric, Hiragana, Katakana, ProportionalAlphanumeric, ProportionalHiragana, ProportionalKatakana,
        JisX0201Katakana, JisX0213Kanji1, JisX0213Kanji2, AdditionalSymbols, Mosaic, LatinOrOther, Drcs, Macro,
    }

    private readonly record struct Entry(GSet Set, int Bytes, int DrcsIndex = 0);

    private static readonly Entry KanjiE = new(GSet.Kanji, 2), AlnumE = new(GSet.Alphanumeric, 1),
        HiraganaE = new(GSet.Hiragana, 1), MacroE = new(GSet.Macro, 1);

    private static Entry? GByF(int f) => f switch
    {
        0x42 => KanjiE,
        0x4A => AlnumE,
        0x4B or 0x4C => new Entry(GSet.LatinOrOther, 1),
        0x30 => HiraganaE,
        0x31 => new Entry(GSet.Katakana, 1),
        0x32 or 0x33 or 0x34 or 0x35 => new Entry(GSet.Mosaic, 1),
        0x36 => new Entry(GSet.ProportionalAlphanumeric, 1),
        0x37 => new Entry(GSet.ProportionalHiragana, 1),
        0x38 => new Entry(GSet.ProportionalKatakana, 1),
        0x49 => new Entry(GSet.JisX0201Katakana, 1),
        0x39 => new Entry(GSet.JisX0213Kanji1, 2),
        0x3A => new Entry(GSet.JisX0213Kanji2, 2),
        0x3B => new Entry(GSet.AdditionalSymbols, 2),
        _ => null,
    };

    private static Entry? DrcsByF(int f) => f switch
    {
        0x40 => new Entry(GSet.Drcs, 2, 0),
        >= 0x41 and <= 0x4F => new Entry(GSet.Drcs, 1, f - 0x40),
        0x70 => MacroE,
        _ => null,
    };

    private readonly int _languageId; // 1＝第 1 言語、2＝第 2 言語

    private readonly Entry[] _g = new Entry[4];
    private int _gl, _gr;
    private readonly Dictionary<int, AribDrcsPattern>[] _drcsMaps = new Dictionary<int, AribDrcsPattern>[16];

    private AribCaption _caption = new();
    private int _swf = 7;
    private int _planeW = 960, _planeH = 540;
    private int _areaW = 960, _areaH = 540, _areaX, _areaY;
    private bool _posInited;
    private int _posX, _posY;
    private int _charW = 36, _charH = 36, _hs = 4, _vs = 24;
    private float _scaleX = 1f, _scaleY = 1f;
    private bool _underline, _bold, _italic, _stroke;
    private uint _strokeColor;
    private int _enclosure, _flash;
    private int _palette;
    private uint _textColor, _backColor;
    private int _prevDgiGroup = -1;

    public AribCaptionDecoder(int languageId = 1)
    {
        _languageId = languageId;
        for (int i = 0; i < 16; i++) _drcsMaps[i] = new Dictionary<int, AribDrcsPattern>();
        ResetInternalState();
    }

    // 字幕の言語情報（字幕管理データ）。ISO 639-2 の 3 文字。
    public string?[] Languages { get; } = new string?[2];

    private static uint Clut(int palette, int index) => AribCaptionTables.Clut[(palette & 7) * 16 + (index & 15)];

    private void ResetGraphicSets()
    {
        _g[0] = KanjiE; _g[1] = AlnumE; _g[2] = HiraganaE; _g[3] = MacroE;
        _gl = 0; _gr = 2;
    }

    private void ResetWritingFormat()
    {
        switch (_swf)
        {
            case 5: _planeW = _areaW = 1920; _planeH = _areaH = 1080; _charW = 36; _charH = 36; _hs = 4; _vs = 24; break;
            case 8: _planeW = _areaW = 960; _planeH = _areaH = 540; _charW = 36; _charH = 36; _hs = 12; _vs = 24; break;
            case 9: _planeW = _areaW = 720; _planeH = _areaH = 480; _charW = 36; _charH = 36; _hs = 4; _vs = 16; break;
            case 10: _planeW = _areaW = 720; _planeH = _areaH = 480; _charW = 36; _charH = 36; _hs = 8; _vs = 24; break;
            default: _planeW = _areaW = 960; _planeH = _areaH = 540; _charW = 36; _charH = 36; _hs = 4; _vs = 24; break;
        }
    }

    private void ResetInternalState()
    {
        ResetGraphicSets();
        ResetWritingFormat();
        _areaX = 0; _areaY = 0;
        _posInited = false; _posX = 0; _posY = 0;
        _scaleX = 1f; _scaleY = 1f;
        _underline = _bold = _italic = _stroke = false;
        _strokeColor = 0;
        _enclosure = 0; _flash = 0;
        _palette = 0;
        _textColor = Clut(0, 7);
        _backColor = Clut(0, 8);
    }

    // PES の PES_data_field（data_identifier から）を 1 つ読む。表示 1 回ぶんが出来れば返す（字幕管理データ・別言語・空は null）。
    public AribCaption? Decode(ReadOnlySpan<byte> pes)
    {
        if (pes.Length < 3) return null;
        if (pes[0] != 0x80 || pes[1] != 0xFF) return null; // 0x80＝字幕（0x81 文字スーパーは扱わない）
        int begin = 3 + (pes[2] & 0x0F);
        if (begin + 5 > pes.Length) return null;
        int dgId = (pes[begin] & 0xFC) >> 2;
        int dgSize = (pes[begin + 3] << 8) | pes[begin + 4];
        if (dgSize == 0 || begin + 5 + dgSize > pes.Length) return null;
        var body = pes.Slice(begin + 5, dgSize);
        int dgiId = dgId & 0x0F;
        // libaribcaption は組（A/B）を (data_group_id & 0xF0) >> 8 で取っていて常に 0 になる＝**2 回目以降の字幕管理データは
        // すべて再送扱いで読まない**。TS 再生と同じ結果にするため、この挙動をそのまま写す。
        int dgiGroup = 0;
        _caption = new AribCaption();
        if (dgiId == 0)
        {
            if (dgiGroup == _prevDgiGroup) return null; // 再送（ARIB TR-B14 4.2.4）
            _prevDgiGroup = dgiGroup;
            if (!ParseManagement(body)) return null;
        }
        else
        {
            if (dgiId != _languageId) return null;
            if (!ParseStatement(body)) return null;
        }
        if (_caption.Regions.Count == 0 && !_caption.ClearScreen && _caption.WaitMs == 0) return null;
        _caption.PlaneWidth = _planeW; _caption.PlaneHeight = _planeH;
        return _caption;
    }

    private bool ParseManagement(ReadOnlySpan<byte> d)
    {
        if (d.Length < 10) return false;
        int tmd = (d[0] & 0xC0) >> 6;
        int off = 1;
        if (tmd == 2) off += 5;
        int numLang = d[off]; off++;
        if (numLang == 0 || numLang > 2) return false;
        for (int i = 0; i < numLang; i++)
        {
            if (off + 6 > d.Length) return false;
            int tag = (d[off] & 0xE0) >> 5;
            int dmf = d[off] & 0x0F; off++;
            if (dmf is 0x0C or 0x0D or 0x0E) off++;
            string lang = new(new[] { (char)d[off], (char)d[off + 1], (char)d[off + 2] }); off += 3;
            int format = (d[off] & 0xF0) >> 4; off++;
            if (tag < 2) Languages[tag] = lang;
            if (tag + 1 == _languageId)
            {
                _swf = format - 1;
                ResetGraphicSets();
                ResetWritingFormat();
            }
        }
        if (off + 3 > d.Length) return false;
        int loop = (d[off] << 16) | (d[off + 1] << 8) | d[off + 2]; off += 3;
        if (loop == 0) return true;
        if (off + loop > d.Length) return false;
        return ParseDataUnits(d.Slice(off, loop));
    }

    private bool ParseStatement(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4) return false;
        int tmd = (d[0] & 0xC0) >> 6;
        int off = 1;
        if (tmd is 1 or 2) off += 5;
        if (off + 4 > d.Length) return false;
        int loop = (d[off] << 16) | (d[off + 1] << 8) | d[off + 2]; off += 3;
        if (loop == 0) return true;
        if (off + loop > d.Length) return false;
        return ParseDataUnits(d.Slice(off, loop));
    }

    private bool ParseDataUnits(ReadOnlySpan<byte> d)
    {
        int off = 0;
        while (off < d.Length)
        {
            if (off + 5 > d.Length) return false;
            if (d[off] != 0x1F) return false;
            int param = d[off + 1];
            int size = (d[off + 2] << 16) | (d[off + 3] << 8) | d[off + 4];
            if (size == 0) return true;
            if (off + 5 + size > d.Length) return false;
            var unit = d.Slice(off + 5, size);
            if (param == 0x20) ParseStatementBody(unit);
            else if (param == 0x30) ParseDrcs(unit, 1);
            else if (param == 0x31) ParseDrcs(unit, 2);
            off += 5 + size;
        }
        return true;
    }

    private bool ParseStatementBody(ReadOnlySpan<byte> d)
    {
        int off = 0;
        while (off < d.Length)
        {
            byte ch = d[off];
            int n;
            bool ok;
            if (ch <= 0x20) ok = HandleC0(d.Slice(off), out n);
            else if (ch < 0x7F) ok = HandleGlGr(d.Slice(off), out n, _gl);
            else if (ch <= 0xA0) ok = HandleC1(d.Slice(off), out n);
            else if (ch < 0xFF) ok = HandleGlGr(d.Slice(off), out n, _gr);
            else { ok = false; n = 1; }
            if (!ok) return false;
            off += n;
        }
        return true;
    }

    private bool ParseDrcs(ReadOnlySpan<byte> d, int byteCount)
    {
        if (d.Length == 0) return false;
        int off = 0;
        int numberOfCode = d[off++];
        for (int i = 0; i < numberOfCode; i++)
        {
            if (off + 3 > d.Length) return false;
            int code = (d[off] << 8) | d[off + 1];
            int numberOfFont = d[off + 2];
            off += 3;
            for (int j = 0; j < numberOfFont; j++)
            {
                if (off + 4 > d.Length) return false;
                int mode = d[off] & 0x0F;
                off++;
                if (mode is 0 or 1)
                {
                    int depth = d[off] + 2, width = d[off + 1], height = d[off + 2];
                    off += 3;
                    int bits = 0;
                    for (int n = depth - 1; n != 0; n >>= 1) bits++;
                    int size = (width * height * bits + 7) / 8;
                    if (depth < 2 || off + size > d.Length) return false;
                    var p = new AribDrcsPattern { Width = width, Height = height, Depth = depth, DepthBits = bits, Pixels = d.Slice(off, size).ToArray() };
                    off += size;
                    if (byteCount == 1)
                    {
                        int f = ((code & 0x0F00) >> 8) + 0x40;
                        int key = (code & 0xFF) & 0x7F;
                        var e = DrcsByF(f);
                        if (e is { Set: GSet.Drcs }) _drcsMaps[e.Value.DrcsIndex][key] = p;
                    }
                    else
                    {
                        int key = code >= 0xEC00 && code <= 0xF8FF ? code : code & 0x7F7F;
                        _drcsMaps[0][key] = p;
                    }
                }
                else
                {
                    if (off + 4 > d.Length) return false;
                    int geo = (d[off + 2] << 8) | d[off + 3];
                    off += 4 + geo;
                }
            }
        }
        return true;
    }

    private bool HandleC0(ReadOnlySpan<byte> d, out int n)
    {
        n = 1;
        switch (d[0])
        {
            case 0x08: MoveRelative(-1, 0); break;        // APB
            case 0x09: MoveRelative(1, 0); break;         // APF
            case 0x0A: MoveRelative(0, 1); break;         // APD
            case 0x0B: MoveRelative(0, -1); break;        // APU
            case 0x0C: ResetInternalState(); _caption.ClearScreen = true; break; // CS
            case 0x0D: MoveToNewline(); break;            // APR
            case 0x0E: _gl = 1; break;                    // LS1
            case 0x0F: _gl = 0; break;                    // LS0
            case 0x16:                                    // PAPF
                if (d.Length < 2) return false;
                MoveRelative(d[1] & 0x3F, 0); n = 2; break;
            case 0x19:                                    // SS2
            {
                if (d.Length < 2) return false;
                if (!HandleGlGr(d.Slice(1), out int m, 2)) return false;
                n = 1 + m; break;
            }
            case 0x1B:                                    // ESC
            {
                if (d.Length < 2) return false;
                if (!HandleEsc(d.Slice(1), out int m)) return false;
                n = 1 + m; break;
            }
            case 0x1C:                                    // APS
                if (d.Length < 3) return false;
                SetAbsolutePos(d[2] & 0x3F, d[1] & 0x3F); n = 3; break;
            case 0x1D:                                    // SS3
            {
                if (d.Length < 2) return false;
                if (!HandleGlGr(d.Slice(1), out int m, 3)) return false;
                n = 1 + m; break;
            }
            case 0x20:                                    // SP
                PushChar(MszMode ? 0x0020 : 0x3000); // MSZ では半角の空白（libaribcaption の置き換えと同じ）
                MoveRelative(1, 0);
                break;
        }
        return true;
    }

    private bool MszMode => _scaleX * 2 == _scaleY;

    private bool HandleEsc(ReadOnlySpan<byte> d, out int n)
    {
        n = 1;
        switch (d[0])
        {
            case 0x6E: _gl = 2; return true;   // LS2
            case 0x6F: _gl = 3; return true;   // LS3
            case 0x7E: _gr = 1; return true;   // LS1R
            case 0x7D: _gr = 2; return true;   // LS2R
            case 0x7C: _gr = 3; return true;   // LS3R
        }
        if (d[0] == 0x24)
        {
            if (d.Length < 2) return false;
            if (d[1] >= 0x28 && d[1] <= 0x2B)
            {
                if (d.Length < 3) return false;
                int gx = d[1] - 0x28;
                if (d[2] == 0x20)
                {
                    if (d.Length < 4) return false;
                    if (DrcsByF(d[3]) is Entry e) _g[gx] = e;
                    n = 4;
                }
                else
                {
                    if (GByF(d[2]) is Entry e) _g[gx] = e;
                    n = 3;
                }
            }
            else
            {
                if (GByF(d[1]) is Entry e) _g[0] = e;
                n = 2;
            }
        }
        else if (d[0] >= 0x28 && d[0] <= 0x2B)
        {
            if (d.Length < 2) return false;
            int gx = d[0] - 0x28;
            if (d[1] == 0x20)
            {
                if (d.Length < 3) return false;
                if (DrcsByF(d[2]) is Entry e) _g[gx] = e;
                n = 3;
            }
            else
            {
                if (GByF(d[1]) is Entry e) _g[gx] = e;
                n = 2;
            }
        }
        return true;
    }

    private bool HandleC1(ReadOnlySpan<byte> d, out int n)
    {
        n = 1;
        switch (d[0])
        {
            case >= 0x80 and <= 0x87: _textColor = Clut(_palette, d[0] - 0x80); break; // BKF..WHF
            case 0x90:                                                                  // COL
                if (d.Length < 2) return false;
                if (d[1] == 0x20)
                {
                    if (d.Length < 3) return false;
                    _palette = d[2] & 0x07; n = 3;
                }
                else if (d[1] >= 0x48 && d[1] <= 0x7F)
                {
                    if ((d[1] & 0xF0) == 0x40) _textColor = Clut(_palette, d[1] & 0x0F);
                    else if ((d[1] & 0xF0) == 0x50) _backColor = Clut(_palette, d[1] & 0x0F);
                    n = 2;
                }
                else return false;
                break;
            case 0x93: n = 2; break;                                                    // POL
            case 0x88: _scaleX = 0.5f; _scaleY = 0.5f; break;                           // SSZ
            case 0x89: _scaleX = 0.5f; _scaleY = 1f; break;                             // MSZ
            case 0x8A: _scaleX = 1f; _scaleY = 1f; break;                               // NSZ
            case 0x8B:                                                                  // SZX
                if (d.Length < 2) return false;
                if (d[1] == 0x41) _scaleY = 2f;
                else if (d[1] == 0x44) _scaleX = 2f;
                else if (d[1] == 0x45) { _scaleX = 2f; _scaleY = 2f; }
                n = 2; break;
            case 0x91:                                                                  // FLC（記録のみ）
                if (d.Length < 2) return false;
                _flash = d[1] == 0x40 ? 1 : d[1] == 0x47 ? 2 : 0;
                n = 2; break;
            case 0x92:                                                                  // CDC
                if (d.Length < 2) return false;
                n = d[1] == 0x20 ? 3 : 2;
                if (d.Length < n) return false;
                break;
            case 0x94: n = 2; break;                                                    // WMM
            case 0x9D:                                                                  // TIME
                if (d.Length < 3) return false;
                if (d[1] == 0x20) { _caption.WaitMs += (d[2] & 0x3F) * 100; n = 3; }
                else if (d[1] == 0x28) n = 3;
                else return false;
                break;
            case 0x95: return false;                                                    // MACRO（TR-B14 で不使用）
            case 0x98: if (d.Length < 2) return false; n = 2; break;                    // RPC（libaribcaption と同じく読み捨て）
            case 0x9A: _underline = true; break;                                        // STL
            case 0x99: _underline = false; break;                                       // SPL
            case 0x97:                                                                  // HLC
                if (d.Length < 2) return false;
                _enclosure = d[1] & 0x0F; n = 2; break;
            case 0x9B:                                                                  // CSI
            {
                if (!HandleCsi(d.Slice(1), out int m)) return false;
                n = 1 + m; break;
            }
        }
        return true;
    }

    private bool HandleCsi(ReadOnlySpan<byte> d, out int n)
    {
        n = 0;
        int off = 0, p1 = 0, p2 = 0, count = 0;
        while (off < d.Length)
        {
            byte c = d[off];
            if (c >= 0x30 && c <= 0x39) { if (count <= 1) p2 = p2 * 10 + (c & 0x0F); }
            else if (c == 0x20) { if (count == 0) p1 = p2; count++; break; }
            else if (c == 0x3B) { if (count == 0) { p1 = p2; p2 = 0; } count++; }
            off++;
        }
        if (++off >= d.Length) return false;
        switch (d[off])
        {
            case 0x53: if (count == 1) _swf = p1; ResetWritingFormat(); break;          // SWF
            case 0x56: _areaW = p1; _areaH = p2; break;                                  // SDF
            case 0x57: _charW = p1; _charH = p2; break;                                  // SSM
            case 0x58: _hs = p1; break;                                                  // SHS
            case 0x59: _vs = p1; break;                                                  // SVS
            case 0x5F:                                                                   // SDP
                _areaX = p1;
                if (count >= 2) _areaY = p2;
                if (!_posInited) SetAbsolutePos(0, 0);
                break;
            case 0x61: _posInited = true; _posX = p1; _posY = p2; break;                  // ACPS
            case 0x63:                                                                   // ORN
                if (p1 == 0) _stroke = false;
                else if (p1 == 1 && count >= 2)
                {
                    int pa = p2 / 100, ix = p2 % 100;
                    if (pa >= 8 || ix >= 16) return false;
                    _stroke = true; _strokeColor = Clut(pa, ix);
                }
                break;
            case 0x64:                                                                   // MDF
                if (p1 == 0) { _bold = false; _italic = false; }
                else if (p1 == 1) _bold = true;
                else if (p1 == 2) _italic = true;
                else if (p1 == 3) { _bold = true; _italic = true; }
                break;
        }
        n = off + 1;
        return true;
    }

    private bool HandleGlGr(ReadOnlySpan<byte> d, out int n, int gIndex)
    {
        var e = _g[gIndex];
        n = e.Bytes;
        int ch = d[0] & 0x7F;
        if (ch < 0x21 || ch >= 0x7F) return false;
        int ch2 = 0;
        if (e.Bytes == 2)
        {
            if (d.Length < 2) return false;
            ch2 = d[1] & 0x7F;
            if (ch2 < 0x21 || ch2 >= 0x7F) return false;
        }
        switch (e.Set)
        {
            case GSet.Hiragana or GSet.ProportionalHiragana:
            {
                int u = AribCaptionTables.Hiragana[ch - 0x21];
                if (ch >= 0x79 && MszMode) u = AribCaptionTables.KanaSymbolsHalfwidth[ch - 0x79];
                PushChar(u); MoveRelative(1, 0); break;
            }
            case GSet.Katakana or GSet.ProportionalKatakana:
            {
                int u = AribCaptionTables.Katakana[ch - 0x21];
                if (ch >= 0x79 && MszMode) u = AribCaptionTables.KanaSymbolsHalfwidth[ch - 0x79];
                PushChar(u); MoveRelative(1, 0); break;
            }
            case GSet.JisX0201Katakana:
            {
                int u = MszMode ? AribCaptionTables.JisX0201KatakanaHalfwidth[ch - 0x21] : AribCaptionTables.JisX0201Katakana[ch - 0x21];
                PushChar(u); MoveRelative(1, 0); break;
            }
            case GSet.Kanji or GSet.JisX0213Kanji1 or GSet.JisX0213Kanji2 or GSet.AdditionalSymbols:
            {
                int ku = ch - 0x21, ten = ch2 - 0x21;
                int u;
                if (ku < 84)
                {
                    int index = ku * 94 + ten;
                    u = AribCaptionTables.Kanji[index];
                    if (MszMode)
                    {
                        if (ku < 2) u = AribCaptionTables.KanjiSymbolsHalfwidth[index];
                        if (u == 0x3000 || (u >= 0xFF01 && u <= 0xFF5E)) u = (u & 0xFF) + 0x20;
                    }
                }
                else u = AribCaptionTables.AdditionalSymbolsUnicode[(ku - 84) * 94 + ten];
                PushChar(u); MoveRelative(1, 0); break;
            }
            case GSet.Alphanumeric or GSet.ProportionalAlphanumeric:
            {
                int u = MszMode ? AribCaptionTables.AlphanumericHalfwidth[ch - 0x21] : AribCaptionTables.AlphanumericFullwidth[ch - 0x21];
                PushChar(u); MoveRelative(1, 0); break;
            }
            case GSet.Macro:
                if (ch >= 0x60 && ch <= 0x6F && !ParseStatementBody(AribCaptionTables.DefaultMacros[ch & 0x0F])) return false;
                break;
            case GSet.Drcs:
            {
                int key = e.Bytes == 2 ? (ch << 8) | ch2 : ch;
                if (_drcsMaps[e.DrcsIndex].TryGetValue(key, out var p)) PushDrcs(p);
                else PushChar(0x3013); // 定義の無い DRCS は〓（libaribcaption と同じ）
                MoveRelative(1, 0);
                break;
            }
            // モザイク・ラテン等は扱わない（libaribcaption と同じく無視）
        }
        return true;
    }

    private void PushChar(int codepoint) => PushCaptionChar(new AribCaptionChar { Codepoint = codepoint });

    private void PushDrcs(AribDrcsPattern p) => PushCaptionChar(new AribCaptionChar { Codepoint = 0, Drcs = p });

    private void PushCaptionChar(AribCaptionChar c)
    {
        c.X = _posX;
        c.Y = _posY - SectionHeight;
        c.CharWidth = _charW; c.CharHeight = _charH; c.HSpacing = _hs; c.VSpacing = _vs;
        c.ScaleX = _scaleX; c.ScaleY = _scaleY;
        c.TextColor = _textColor; c.BackColor = _backColor;
        c.Underline = _underline; c.Bold = _bold; c.Italic = _italic;
        c.Stroke = _stroke; c.StrokeColor = _stroke ? _strokeColor : 0;
        c.Enclosure = _enclosure; c.Flash = _flash;
        if (NeedNewRegion()) MakeNewRegion();
        var r = _caption.Regions[^1];
        r.Width += c.SectionWidth;
        r.Chars.Add(c);
    }

    private bool NeedNewRegion()
    {
        if (_caption.Regions.Count == 0) return true;
        var prev = _caption.Regions[^1];
        if (prev.Chars.Count == 0) return false;
        var pc = prev.Chars[^1];
        if (_posX != pc.X + pc.SectionWidth) return true;
        if (_posY - SectionHeight != pc.Y) return true;
        if (SectionHeight != pc.SectionHeight) return true;
        return false;
    }

    private void MakeNewRegion()
    {
        if (_caption.Regions.Count == 0 || _caption.Regions[^1].Chars.Count != 0) _caption.Regions.Add(new AribCaptionRegion());
        var r = _caption.Regions[^1];
        r.X = _posX; r.Y = _posY - SectionHeight; r.Height = SectionHeight;
        r.IsRuby = (_scaleX == 0.5f && _scaleY == 0.5f) || (_charW == 18 && _charH == 18);
    }

    private int SectionWidth => (int)MathF.Floor((_charW + _hs) * _scaleX);
    private int SectionHeight => (int)MathF.Floor((_charH + _vs) * _scaleY);

    private void SetAbsolutePos(int x, int y)
    {
        _posInited = true;
        _posX = _areaX + x * SectionWidth;
        _posY = _areaY + (y + 1) * SectionHeight;
    }

    private void MoveRelative(int x, int y)
    {
        if (_posX < 0 || _posY < 0) SetAbsolutePos(0, 0);
        _posInited = true;
        while (x < 0)
        {
            _posX -= SectionWidth; x++;
            if (_posX < _areaX) { _posX = _areaX + _areaW - SectionWidth; y--; }
        }
        while (x > 0)
        {
            _posX += SectionWidth; x--;
            if (_posX >= _areaX + _areaW) { _posX = _areaX; y++; }
        }
        while (y < 0)
        {
            _posY -= SectionHeight; y++;
            if (_posY < _areaY) _posY = _areaY + _areaH;
        }
        while (y > 0)
        {
            _posY += SectionHeight; y--;
            if (_posY > _areaY + _areaH) _posY = _areaY + SectionHeight;
        }
    }

    private void MoveToNewline()
    {
        if (_posX < 0 || _posY < 0) SetAbsolutePos(0, 0);
        _posInited = true;
        _posX = _areaX;
        _posY += SectionHeight;
    }
}
