// 診断：ARIB 字幕の往復テスト。TS の字幕データを読み解き（AribCaptionDecoder）→ krk のテキスト型へ写し
//（AribCaptionToKuroko）→ 組み立て直し（AribCaptionEncoder）→ もう一度読み解いて、**元と同じ文字・位置・書式**に
// 戻るかを全件で確かめる。再生は組み立て直したものを TS 再生と同じ描画器へ渡すので、ここが一致すれば見え方も一致する。
//   Kuroko.Converter.exe --arib-caption-selftest <in.ts> <字幕の PID（0x130 等）> <out.txt>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Kuroko.Arib;

public static class AribCaptionSelfTest
{
    public static string Run(string tsPath, int pid)
    {
        var sb = new StringBuilder();
        var pesList = new List<(long Pts, byte[] Data)>();
        using (var fs = File.OpenRead(tsPath))
            AribCaptionExtractor.Extract(fs, pid, (pts, d) => pesList.Add((pts, d)));
        sb.AppendLine($"PES {pesList.Count} 件");
        var dec = new AribCaptionDecoder(1);
        var originals = new List<AribCaption>();
        var conv = new AribCaptionToKuroko();
        long base90k = pesList.Count > 0 ? pesList[0].Pts : 0;
        var unwrap = new AribCaptionExtractor.Unwrapper(base90k);
        foreach (var (pts, d) in pesList)
        {
            long cont = unwrap.Next(pts);
            var c = dec.Decode(d);
            if (c is null) continue;
            conv.Add((cont - base90k) / 90.0, c);
            if (c.Regions.Count > 0) originals.Add(c);
        }
        var text = conv.Build(double.MaxValue, "jpn", "字幕");
        var drcs = (text.Drcs ?? new()).ToDictionary(x => AribCaptionEncoder.ParseCharSpec(x.Char), x => x);
        sb.AppendLine($"表示 {originals.Count} 回 → cue {text.Cues.Count} 件、DRCS {drcs.Count} 字");

        // cue と元の表示は同じ順（どちらも表示のある字幕データだけ、時刻順）。尺が 0 で落ちたものがあれば数がずれる。
        int ok = 0, ng = 0, idx = 0;
        var redec = new AribCaptionDecoder(1);
        foreach (var cue in text.Cues)
        {
            while (idx < originals.Count && !SameText(originals[idx], cue)) idx++;
            if (idx >= originals.Count) { sb.AppendLine($"cue {cue.StartMs:F0}ms：元の表示が見つからない"); ng++; continue; }
            var orig = originals[idx++];
            byte[] pes = AribCaptionEncoder.Encode(cue, text.Canvas, drcs);
            var back = redec.Decode(pes);
            string? diff = back is null ? "読み解けない" : Compare(orig, back);
            if (diff is null) ok++;
            else { ng++; if (ng <= 20) sb.AppendLine($"cue {cue.StartMs:F0}ms 不一致：{diff}"); }
        }
        sb.AppendLine($"一致 {ok} 件／不一致 {ng} 件");
        return sb.ToString();
    }

    // 先頭 count 件の字幕データを、時刻・種類・中身つきで書き出す（調査用）。
    public static string Dump(string tsPath, int pid, int count)
    {
        var sb = new StringBuilder();
        var dec = new AribCaptionDecoder(1);
        int n = 0;
        long base90k = -1;
        using var fs = File.OpenRead(tsPath);
        try
        {
            AribCaptionExtractor.Extract(fs, pid, (pts, d) =>
            {
                if (n >= count) throw new OperationCanceledException();
                if (base90k < 0) base90k = pts;
                int dgId = d.Length > 3 + (d[2] & 0x0F) ? (d[3 + (d[2] & 0x0F)] >> 2) : -1;
                var c = dec.Decode(d);
                sb.AppendLine($"{AribCaptionExtractor.Diff90k(pts, base90k) / 90.0,9:F0}ms dg=0x{dgId:X2} len={d.Length,5} "
                    + (c is null ? "（無し）" : $"CS={c.ClearScreen} wait={c.WaitMs} 行={c.Regions.Count}「{Plain(c)}」"));
                n++;
            });
        }
        catch (OperationCanceledException) { }
        return sb.ToString();
    }

    private static string Plain(AribCaption c) => string.Concat(c.Regions.SelectMany(r => r.Chars).Select(ch => ch.Drcs is null ? char.ConvertFromUtf32(ch.Codepoint) : "□"));
    private static bool SameText(AribCaption c, KurokoSubsTextCue cue)
    {
        string a = Plain(c);
        string b = string.Concat(cue.Lines!.SelectMany(l => l.Runs).Select(r => r.Text));
        b = string.Concat(EnumerateCp(b).Select(cp => cp >= 0xF0000 ? "□" : char.ConvertFromUtf32(cp)));
        return a == b;
    }

    private static IEnumerable<int> EnumerateCp(string s)
    {
        for (int i = 0; i < s.Length; i++) { int cp = char.ConvertToUtf32(s, i); if (char.IsHighSurrogate(s[i])) i++; yield return cp; }
    }

    private static string? Compare(AribCaption a, AribCaption b)
    {
        var ca = a.Regions.SelectMany(r => r.Chars).ToList();
        var cb = b.Regions.SelectMany(r => r.Chars).ToList();
        if (ca.Count != cb.Count) return $"文字数 {ca.Count}→{cb.Count}「{Plain(a)}」→「{Plain(b)}」";
        if (a.Regions.Count != b.Regions.Count) return $"行数 {a.Regions.Count}→{b.Regions.Count}";
        for (int i = 0; i < ca.Count; i++)
        {
            var x = ca[i]; var y = cb[i];
            string? d =
                x.Codepoint != y.Codepoint ? $"文字 U+{x.Codepoint:X4}→U+{y.Codepoint:X4}" :
                (x.Drcs is null) != (y.Drcs is null) ? "DRCS の有無" :
                x.Drcs is not null && !x.Drcs.Pixels.AsSpan().SequenceEqual(y.Drcs!.Pixels) ? "DRCS の形" :
                x.X != y.X || x.Y != y.Y ? $"位置 ({x.X},{x.Y})→({y.X},{y.Y})" :
                x.CharWidth != y.CharWidth || x.CharHeight != y.CharHeight || x.HSpacing != y.HSpacing || x.VSpacing != y.VSpacing ? "文字の大きさ・間隔" :
                x.ScaleX != y.ScaleX || x.ScaleY != y.ScaleY ? $"サイズ ({x.ScaleX},{x.ScaleY})→({y.ScaleX},{y.ScaleY})" :
                x.TextColor != y.TextColor ? $"文字色 {x.TextColor:X8}→{y.TextColor:X8}" :
                x.BackColor != y.BackColor ? $"背景色 {x.BackColor:X8}→{y.BackColor:X8}" :
                x.Stroke != y.Stroke || x.StrokeColor != y.StrokeColor ? "縁" :
                x.Underline != y.Underline || x.Bold != y.Bold || x.Italic != y.Italic ? "装飾" :
                x.Enclosure != y.Enclosure ? "囲み" : null;
            if (d is not null) return $"{i} 文字目：{d}「{Plain(a)}」";
        }
        return null;
    }
}
