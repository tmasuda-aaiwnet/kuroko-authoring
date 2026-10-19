using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kuroko.Authoring.Export;

// SRT（SubRip）の字幕を読み、krk のテキスト型の字幕（§11 の簡単な形：region＋runs）にする。
// 書式のタグ（<i> <b> <font> など）は取り除く（最初の版は文字だけ）。
public static partial class SrtReader
{
    public sealed record Cue(double StartMs, double EndMs, string Text);

    public static List<Cue> Read(string path) => Parse(ReadText(path));

    // UTF-8（BOM の有無を問わない）として読む。UTF-8 として正しくなければ Shift_JIS として読む（日本の古い SRT）。
    public static string ReadText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932).GetString(bytes);
        }
    }

    public static List<Cue> Parse(string text)
    {
        var cues = new List<Cue>();
        var blocks = Regex.Split(text.Replace("\r\n", "\n").Replace('\r', '\n').Trim(), @"\n\s*\n");
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            int i = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (i < 0) continue;
            var m = TimeLine().Match(lines[i]);
            if (!m.Success) continue;
            double start = Ms(m.Groups[1].Value), end = Ms(m.Groups[2].Value);
            string body = string.Join('\n', lines.Skip(i + 1).Select(l => Tag().Replace(l, "").TrimEnd())).Trim('\n');
            if (body.Length == 0 || end <= start) continue;
            cues.Add(new Cue(start, end, body));
        }
        cues.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        return cues;
    }

    // krk のテキスト型の字幕（subs/text/<source>.json の中身）。canvas＝映像の表示画素。
    public static KurokoSubsText ToKuroko(List<Cue> cues, string? language, string? name, int canvasWidth, int canvasHeight)
    {
        var text = new KurokoSubsText
        {
            Lang = KurokoLanguage.Normalize(language),
            Name = name,
            Canvas = new KurokoSubsCanvas { Width = canvasWidth, Height = canvasHeight },
        };
        foreach (var c in cues)
            text.Cues.Add(new KurokoSubsTextCue
            {
                StartMs = c.StartMs,
                EndMs = c.EndMs,
                Region = new KurokoSubsRegion
                {
                    X = canvasWidth / 20, Y = canvasHeight / 20,
                    W = canvasWidth - canvasWidth / 10, H = canvasHeight - canvasHeight / 10,
                    Anchor = "bottom-center",
                },
                Runs = new List<KurokoSubsTextRun> { new() { Text = c.Text } },
            });
        return text;
    }

    // "00:01:02,345"（, の代わりに . も受ける）→ ms
    private static double Ms(string s)
    {
        var p = s.Replace('.', ',').Split(':', ',');
        return int.Parse(p[0], CultureInfo.InvariantCulture) * 3600000.0 + int.Parse(p[1], CultureInfo.InvariantCulture) * 60000.0
            + int.Parse(p[2], CultureInfo.InvariantCulture) * 1000.0 + int.Parse(p[3].PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"(\d+:\d{1,2}:\d{1,2}[,.]\d{1,3})\s*-->\s*(\d+:\d{1,2}:\d{1,2}[,.]\d{1,3})")]
    private static partial Regex TimeLine();

    [GeneratedRegex(@"</?[a-zA-Z][^>]*>|\{\\[^}]*\}")]
    private static partial Regex Tag();
}
