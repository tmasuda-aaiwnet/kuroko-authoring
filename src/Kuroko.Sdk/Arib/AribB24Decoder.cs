// ARIB STD-B24 8単位符号の文字列デコーダ。
// EIT short_event_descriptor の番組名／audio_component_descriptor の名称などを
// Unicode 文字列へ変換する。
//
// 実装方針（ariblib / epgdump_py のロジックを移植）：
//   ARIB の符号集合切替（G0〜G3・GL/GR・シングルシフト・エスケープ）を解釈し、
//   漢字／英数／ひらがな／カタカナは中間 iso-2022-jp バイト列へ変換して
//   .NET の iso-2022-jp デコーダで Unicode 化する。追加シンボル（外字）は
//   AribGaiji.Map で直接 Unicode 文字列へ置換する。
using System;
using System.Collections.Generic;
using System.Text;

namespace Kuroko.Arib;

public static class AribB24Decoder
{
    private static readonly Encoding Iso2022Jp;

    static AribB24Decoder()
    {
        // .NET (Core) では既定で iso-2022-jp 等のコードページが未登録のため登録する。
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        Iso2022Jp = Encoding.GetEncoding("iso-2022-jp");
    }

    // 符号集合の種別。
    private enum CodeSet
    {
        Kanji, Alphanumeric, Hiragana, Katakana,
        JisX0201Katakana, JisKanjiPlane1, JisKanjiPlane2,
        AdditionalSymbols, Unsupported
    }

    // 終端バイト → (符号集合, バイト数)。CODE_SET_G 相当。
    private static (CodeSet set, int size) DesignateG(byte f) => f switch
    {
        0x42 => (CodeSet.Kanji, 2),
        0x4A => (CodeSet.Alphanumeric, 1),
        0x30 => (CodeSet.Hiragana, 1),
        0x31 => (CodeSet.Katakana, 1),
        0x36 => (CodeSet.Alphanumeric, 1), // PROP_ALPHANUMERIC
        0x37 => (CodeSet.Hiragana, 1),     // PROP_HIRAGANA
        0x38 => (CodeSet.Katakana, 1),     // PROP_KATAKANA
        0x49 => (CodeSet.JisX0201Katakana, 1),
        0x39 => (CodeSet.JisKanjiPlane1, 2),
        0x3A => (CodeSet.JisKanjiPlane2, 2),
        0x3B => (CodeSet.AdditionalSymbols, 2),
        0x32 or 0x33 or 0x34 or 0x35 => (CodeSet.Unsupported, 1), // モザイク
        _ => (CodeSet.Unsupported, 1),
    };

    // ひらがな／カタカナの一部（ゝゞヽヾ ー 。「」、・）の JIS X 0208 への対応。
    private static readonly Dictionary<byte, byte> AribBase = new()
    {
        [0x79] = 0x3C, [0x7A] = 0x23, [0x7B] = 0x56,
        [0x7C] = 0x57, [0x7D] = 0x22, [0x7E] = 0x26,
    };
    private static readonly Dictionary<byte, byte> HiraganaMap = Merge(AribBase, new() { [0x77] = 0x35, [0x78] = 0x36 });
    private static readonly Dictionary<byte, byte> KatakanaMap = Merge(AribBase, new() { [0x77] = 0x33, [0x78] = 0x34 });

    private static Dictionary<byte, byte> Merge(Dictionary<byte, byte> a, Dictionary<byte, byte> b)
    {
        var d = new Dictionary<byte, byte>(a);
        foreach (var kv in b) d[kv.Key] = kv.Value;
        return d;
    }

    private static readonly byte[] EscAscii = { 0x1B, 0x28, 0x42 };  // ESC ( B
    private static readonly byte[] EscZenkaku = { 0x1B, 0x24, 0x42 };// ESC $ B
    private static readonly byte[] EscHankaku = { 0x1B, 0x28, 0x49 };// ESC ( I

    public static string Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return string.Empty;
        try
        {
            return new Decoder(data).Run();
        }
        catch
        {
            return string.Empty; // 破損データは空に倒す（表示はフォールバックに委ねる）。
        }
    }

    // 1 回のデコードに閉じた状態機械。
    private sealed class Decoder
    {
        private readonly byte[] _in;
        private int _pos;

        // G0〜G3 の割当（既定: 漢字／英数／ひらがな／カタカナ）。
        private readonly (CodeSet set, int size)[] _g =
        {
            (CodeSet.Kanji, 2), (CodeSet.Alphanumeric, 1),
            (CodeSet.Hiragana, 1), (CodeSet.Katakana, 1),
        };
        private int _gl = 0; // GL に呼び出す G バッファ
        private int _gr = 2; // GR に呼び出す G バッファ
        private int _singleShift = -1;

        private readonly StringBuilder _out = new();
        private readonly List<byte> _jis = new();
        private byte[]? _lastEsc; // _jis に最後に積んだエスケープ列

        public Decoder(ReadOnlySpan<byte> data) { _in = data.ToArray(); }

        private int Next() => _pos < _in.Length ? _in[_pos++] : -1;

        public string Run()
        {
            while (true)
            {
                int d = Next();
                if (d < 0) break;
                if ((0x21 <= d && d <= 0x7E) || (0xA1 <= d && d <= 0xFE))
                    DoConvert((byte)d);
                else if (d == 0x20 || d == 0xA0 || d == 0x09)
                    AppendJis(EscAscii, 0x20);
                else if (d == 0x0D || d == 0x0A)
                    AppendJis(EscAscii, 0x0A);
                else
                    DoControl((byte)d);
            }
            FlushJis();
            return _out.ToString().TrimEnd();
        }

        // 現在のGL/GR/シングルシフトから符号集合を選ぶ。
        private (CodeSet set, int size) CurrentCode(byte data)
        {
            if (0x21 <= data && data <= 0x7E)
            {
                if (_singleShift >= 0)
                {
                    var c = _g[_singleShift];
                    _singleShift = -1;
                    return c;
                }
                return _g[_gl];
            }
            return _g[_gr]; // 0xA1-0xFE
        }

        private void DoConvert(byte data)
        {
            var (set, size) = CurrentCode(data);
            byte ch = data;
            byte ch2 = 0;
            if (size == 2)
            {
                int n = Next();
                ch2 = (byte)(n < 0 ? 0 : n);
            }
            if (0xA1 <= ch && ch <= 0xFE) { ch &= 0x7F; ch2 &= 0x7F; }

            switch (set)
            {
                case CodeSet.Kanji:
                case CodeSet.JisKanjiPlane1:
                case CodeSet.JisKanjiPlane2:
                    AppendJis(EscZenkaku, ch, ch2);
                    break;
                case CodeSet.Alphanumeric:
                    AppendJis(EscAscii, ch);
                    break;
                case CodeSet.Hiragana:
                    if (ch >= 0x77) AppendJis(EscZenkaku, 0x21, HiraganaMap[ch]);
                    else AppendJis(EscZenkaku, 0x24, ch);
                    break;
                case CodeSet.Katakana:
                    if (ch >= 0x77) AppendJis(EscZenkaku, 0x21, KatakanaMap[ch]);
                    else AppendJis(EscZenkaku, 0x25, ch);
                    break;
                case CodeSet.JisX0201Katakana:
                    AppendJis(EscHankaku, ch);
                    break;
                case CodeSet.AdditionalSymbols:
                    FlushJis();
                    int w = (ch << 8) + ch2;
                    _out.Append(AribGaiji.Map.TryGetValue(w, out var g) ? g : "〓");
                    break;
                case CodeSet.Unsupported:
                default:
                    break;
            }
        }

        private void DoControl(byte data)
        {
            switch (data)
            {
                case 0x0F: _gl = 0; break;                 // LS0
                case 0x0E: _gl = 1; break;                 // LS1
                case 0x19: _singleShift = 2; break;        // SS2
                case 0x1D: _singleShift = 3; break;        // SS3
                case 0x1B: DoEscape(); break;              // ESC
                default: break;
            }
        }

        // エスケープシーケンス（符号集合の指示／ロッキングシフト）。
        private void DoEscape()
        {
            int d = Next();
            if (d < 0) return;
            switch (d)
            {
                case 0x6E: _gl = 2; return; // LS2
                case 0x6F: _gl = 3; return; // LS3
                case 0x7E: _gr = 1; return; // LS1R
                case 0x7D: _gr = 2; return; // LS2R
                case 0x7C: _gr = 3; return; // LS3R
                case 0x24: DoEscapeDollar(); return;
                case 0x28: DesignateFinal(0, false); return; // G0
                case 0x29: DesignateFinal(1, false); return; // G1
                case 0x2A: DesignateFinal(2, false); return; // G2
                case 0x2B: DesignateFinal(3, false); return; // G3
                default: return;
            }
        }

        private void DoEscapeDollar()
        {
            int d = Next();
            if (d < 0) return;
            switch (d)
            {
                case 0x28: DesignateFinal(0, false); return; // 2byte G0（終端を更に読む）
                case 0x29: DesignateFinal(1, false); return;
                case 0x2A: DesignateFinal(2, false); return;
                case 0x2B: DesignateFinal(3, false); return;
                default:
                    // ESC $ <F>: 2バイト集合を G0 に指示（d が終端）
                    _g[0] = DesignateG((byte)d);
                    return;
            }
        }

        // 中間バイトをスキップして終端で符号集合を確定。DRCS は未対応（Unsupported）。
        private void DesignateFinal(int gindex, bool drcs)
        {
            int f = Next();
            if (f < 0) return;
            // 0x20 (中間バイト, DRCS など) はスキップして次を終端とする。
            while (f == 0x20)
            {
                f = Next();
                if (f < 0) return;
                drcs = true;
            }
            _g[gindex] = drcs ? (CodeSet.Unsupported, 1) : DesignateG((byte)f);
        }

        // iso-2022-jp 中間バッファへ、必要ならエスケープ列を切り替えてから文字を積む。
        private void AppendJis(byte[] esc, params byte[] chars)
        {
            if (!ReferenceEquals(_lastEsc, esc))
            {
                _jis.AddRange(esc);
                _lastEsc = esc;
            }
            _jis.AddRange(chars);
        }

        private void FlushJis()
        {
            if (_jis.Count == 0) return;
            try { _out.Append(NormalizeJis(Iso2022Jp.GetString(_jis.ToArray()))); }
            catch { /* 破損セグメントは捨てる */ }
            _jis.Clear();
            _lastEsc = null;
        }

        // .NET の iso-2022-jp デコーダは一部の JIS X 0208 文字を Microsoft 系の
        // Unicode へ写像する（例: 波ダッシュ）。ARIB/JIS の本来の符号位置へ正規化する。
        private static string NormalizeJis(string s)
        {
            if (s.IndexOfAny(MsChars) < 0) return s;
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                sb.Append(ch switch
                {
                    '～' => '〜', // ～ → 〜 WAVE DASH
                    '∥' => '‖', // ∥ → ‖ DOUBLE VERTICAL LINE
                    '－' => '−', // － → − MINUS SIGN
                    '￠' => '¢', // ￠ → ¢ CENT
                    '￡' => '£', // ￡ → £ POUND
                    '￢' => '¬', // ￢ → ¬ NOT
                    _ => ch,
                });
            }
            return sb.ToString();
        }

        private static readonly char[] MsChars =
            { '～', '∥', '－', '￠', '￡', '￢' };
    }
}
