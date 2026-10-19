// 読み解いた ARIB 字幕（AribCaptionDecoder の出力）を、krk のテキスト型（subs/text/<source>.json、charset "arib"）へ写す。
// 一次仕様は docs/kuroko-format-spec.md §11.1。
//
// 表示時間は TS 再生（同梱 ffmpeg の libaribcaption）と同じ決め方にする：字幕データ 1 つ＝表示 1 回で、
// **次の字幕データ（消去だけのものを含む）が来たら置き換わる**。TIME で表示時間が指定されていれば、そこで消える。
// 終わりの無い表示は、次の字幕データの時刻（無ければセグメントの終わり）を endMs に入れて有限にする。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Kuroko.Arib;

public sealed class AribCaptionToKuroko
{
    private readonly List<(double Ms, AribCaption C)> _items = new();
    private readonly Dictionary<string, string> _drcsChar = new();   // 形（キー）→ 私用面の文字
    private readonly List<KurokoSubsDrcs> _drcs = new();
    private int _planeW = 960, _planeH = 540;

    public int CueCount { get; private set; }
    public int DrcsCount => _drcs.Count;

    // ms＝セグメント内 0 基準の表示開始時刻。
    public void Add(double ms, AribCaption c)
    {
        _items.Add((ms, c));
        _planeW = c.PlaneWidth; _planeH = c.PlaneHeight;
    }

    public KurokoSubsText Build(double segmentEndMs, string? lang, string? name)
    {
        // 時刻順に並べる。**同じ時刻のものは届いた順を保つ**（安定な並べ替え）：描画器は同じ時刻の字幕を後から来たもので
        // 置き換えるので、どれが「最後」かを崩さない（List.Sort は安定でなく、フジの録画で同じ時刻の表示を取り違えた）。
        var sorted = _items.Select((x, i) => (x, i)).OrderBy(t => t.x.Ms).ThenBy(t => t.i).Select(t => t.x).ToList();
        _items.Clear(); _items.AddRange(sorted);
        var cues = new List<KurokoSubsTextCue>();
        for (int i = 0; i < _items.Count; i++)
        {
            var (start, c) = _items[i];
            if (c.Regions.Count == 0) continue; // 消去だけの字幕＝前の表示を終わらせるだけ
            double end = c.WaitMs > 0 ? start + c.WaitMs : double.PositiveInfinity;
            if (i + 1 < _items.Count) end = Math.Min(end, _items[i + 1].Ms);
            end = Math.Min(end, segmentEndMs);
            if (start < 0) start = 0;
            if (end <= start) continue;
            var cue = new KurokoSubsTextCue { StartMs = Math.Round(start, 3), EndMs = Math.Round(end, 3), Lines = new() };
            foreach (var r in c.Regions)
                if (r.Chars.Count > 0) cue.Lines.Add(BuildLine(r));
            if (cue.Lines.Count > 0) cues.Add(cue);
        }
        CueCount = cues.Count;
        return new KurokoSubsText
        {
            Lang = KurokoLanguage.Normalize(lang),
            Name = name,
            Charset = "arib",
            Canvas = new KurokoSubsCanvas { Width = _planeW, Height = _planeH },
            Drcs = _drcs.Count > 0 ? _drcs : null,
            Cues = cues,
        };
    }

    // 書式（run を分ける単位）。
    private readonly record struct Style(int W, int H, int Hs, int Vs, string Size, string Color, string Bg, string Edge,
        string Deco, string Box, string Flash);

    private static Style StyleOf(AribCaptionChar ch) => new(
        ch.CharWidth, ch.CharHeight, ch.HSpacing, ch.VSpacing, SizeName(ch.ScaleX, ch.ScaleY),
        Hex(ch.TextColor), Hex(ch.BackColor), ch.Stroke ? Hex(ch.StrokeColor) : "none",
        string.Join(",", Deco(ch)), BoxName(ch.Enclosure), ch.Flash switch { 1 => "normal", 2 => "inverted", _ => "none" });

    private KurokoSubsTextLine BuildLine(AribCaptionRegion r)
    {
        var line = new KurokoSubsTextLine { X = r.Chars[0].X, Y = r.Chars[0].Y };
        Style? prev = null;
        KurokoSubsTextRun? run = null;
        foreach (var ch in r.Chars)
        {
            var st = StyleOf(ch);
            if (run is null || st != prev)
            {
                run = new KurokoSubsTextRun();
                // 先頭の run は全部書く。以降は前の run から変わった項目だけ（§11.1 の引き継ぎ）。
                bool all = prev is null;
                var p = prev ?? default;
                if (all || st.W != p.W) run.W = st.W;
                if (all || st.H != p.H) run.H = st.H;
                if (all || st.Hs != p.Hs) run.Hs = st.Hs;
                if (all || st.Vs != p.Vs) run.Vs = st.Vs;
                if (all ? st.Size != "normal" : st.Size != p.Size) run.Size = st.Size;
                if (all || st.Color != p.Color) run.Color = st.Color;
                if (all ? st.Bg != TransparentHex : st.Bg != p.Bg) run.Bg = st.Bg;
                if (all ? st.Edge != "none" : st.Edge != p.Edge) run.Edge = st.Edge;
                if (all ? st.Deco.Length > 0 : st.Deco != p.Deco) run.Deco = st.Deco.Length > 0 ? new List<string>(st.Deco.Split(',')) : new List<string>();
                if (all ? st.Box.Length > 0 : st.Box != p.Box) run.Box = st.Box;
                if (all ? st.Flash != "none" : st.Flash != p.Flash) run.Flash = st.Flash;
                line.Runs.Add(run);
                prev = st;
            }
            run.Text += ch.Drcs is { } d ? DrcsChar(d) : char.ConvertFromUtf32(ch.Codepoint);
        }
        return line;
    }

    private string DrcsChar(AribDrcsPattern d)
    {
        string pattern = Convert.ToBase64String(d.Pixels);
        string key = $"{d.Width}x{d.Height}x{d.DepthBits}:{pattern}";
        if (_drcsChar.TryGetValue(key, out var s)) return s;
        int cp = 0xF0000 + _drcs.Count;
        s = char.ConvertFromUtf32(cp);
        _drcsChar[key] = s;
        _drcs.Add(new KurokoSubsDrcs
        {
            Char = "U+" + cp.ToString("X5", CultureInfo.InvariantCulture),
            Width = d.Width, Height = d.Height, Depth = d.DepthBits, Pattern = pattern,
        });
        return s;
    }

    private const string TransparentHex = "#00000000";

    private static string Hex(uint rgba)
    {
        uint a = rgba & 0xFF;
        return a == 0xFF ? $"#{rgba >> 8:X6}" : $"#{rgba:X8}";
    }

    private static string SizeName(float sx, float sy) => (sx, sy) switch
    {
        (0.5f, 0.5f) => "small",
        (0.5f, 1f) => "medium",
        (1f, 2f) => "tall",
        (2f, 1f) => "wide",
        (2f, 2f) => "double",
        _ => "normal",
    };

    private static IEnumerable<string> Deco(AribCaptionChar ch)
    {
        if (ch.Underline) yield return "underline";
        if (ch.Bold) yield return "bold";
        if (ch.Italic) yield return "italic";
    }

    private static string BoxName(int e)
        => ((e & 4) != 0 ? "t" : "") + ((e & 1) != 0 ? "b" : "") + ((e & 8) != 0 ? "l" : "") + ((e & 2) != 0 ? "r" : "");
}
