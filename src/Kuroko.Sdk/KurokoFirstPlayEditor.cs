using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;


namespace Kuroko;

// 既存の .krk のファーストプレイ（§6.1 の `CONTROL/disc.json` → `firstPlayback`）を読み書きする。
// 変換器の「ファーストプレイ設定…」から使う（再生器は読むだけ）。
//
// 書き込みは **JsonNode で firstPlayback だけを差し替える非破壊編集**にする。disc.json は controller で
// モデルが異なる（none/dvd-video-vm）ので、型に落として書き戻すと未知フィールド（menuDomains・VM 命令など）が
// 欠落して壊れる。node 編集なら他フィールドは完全保持できる（KurokoAudioRecovery と同じ方針）。
public static class KurokoFirstPlayEditor
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語を素の文字で出す
    };

    // 選択肢1つぶん（アングル/音声/字幕に共用）。Number は 1 始まりの論理番号（字幕は 0＝なしを別に足す）。
    public sealed record Choice(int Number, string Label);

    public sealed class TitleInfo
    {
        public int Id { get; init; }
        public string Label { get; init; } = "";
        public List<Choice> Angles { get; } = new();
        public List<Choice> Audio { get; } = new();
        public List<Choice> Subtitle { get; } = new();   // 0＝字幕なし を先頭に含む
    }

    public sealed class Document
    {
        public string Path { get; init; } = "";
        public string Controller { get; init; } = "";
        public int Version { get; init; }
        public List<TitleInfo> Titles { get; } = new();
        public KurokoFirstPlayback? Current { get; init; }
    }

    // .krk を開いて、タイトルと各タイトルのアングル/音声/字幕の一覧＋現在のファーストプレイを読む。
    public static Document Read(string krkPath)
    {
        using var src = KurokoTitleSource.Open(krkPath);
        var doc = new Document
        {
            Path = krkPath,
            Controller = src.Manifest.Controller ?? "",
            Version = src.Manifest.Version,
            Current = src.Disc.FirstPlayback,
        };

        foreach (var entry in src.Disc.Titles.OrderBy(t => t.Id))
        {
            // 音声・字幕は title.json の番号（audio[].number・subtitle[].number）で指す（レイアウト version 5）。
            // 旧 krk（version 4 以前）は「並びの何番目か」で、読み出し時に Number がその並び順になっている。読めないタイトルは既定だけ出す。
            KurokoTitle? title = null;
            try { title = src.ReadTitle(entry.Id); } catch { }

            var info = new TitleInfo { Id = entry.Id, Label = TitleLabel(entry, title) };

            // アングル（§10.1）。持たないタイトルは Count=1（＝アングル1のみ）。
            var (angleCount, _, angleNames) = src.ReadTitleAngles(entry.Id);
            for (int i = 1; i <= Math.Max(1, angleCount); i++)
            {
                string? nm = i - 1 < angleNames.Count ? angleNames[i - 1] : null;
                info.Angles.Add(new Choice(i, string.IsNullOrWhiteSpace(nm) ? $"アングル{i}" : $"アングル{i}：{nm}"));
            }

            if (title is not null)
            {
                for (int i = 0; i < title.Audio.Count; i++)
                    info.Audio.Add(new Choice(title.Audio[i].Number, AudioLabel(title.Audio[i], i)));
                info.Subtitle.Add(new Choice(0, "字幕なし"));
                for (int i = 0; i < title.Subtitle.Count; i++)
                    info.Subtitle.Add(new Choice(title.Subtitle[i].Number, SubtitleLabel(title.Subtitle[i], i)));
            }
            if (info.Audio.Count == 0) info.Audio.Add(new Choice(1, "音声1"));
            if (info.Subtitle.Count == 0) info.Subtitle.Add(new Choice(0, "字幕なし"));

            doc.Titles.Add(info);
        }
        return doc;
    }

    // ファーストプレイを書き込む（null なら削除＝既定へ戻す）。krk 全体を詰め直すので時間がかかる。
    // 戻り値：退避した .bak のパス。
    public static string Write(string krkPath, KurokoFirstPlayback? fp,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        byte[] discBytes;
        using (var reader = KurokoContainerReader.Open(krkPath))
        {
            var e = reader.FindEntry(KurokoLayout.DiscJson)
                ?? throw new InvalidDataException("CONTROL/disc.json が見つかりません（Kuroko ではない可能性）。");
            discBytes = reader.ReadAllBytes(e);
        }

        var node = JsonNode.Parse(discBytes)?.AsObject()
            ?? throw new InvalidDataException("CONTROL/disc.json を解析できません。");

        if (fp is null) node.Remove("firstPlayback");
        else
            node["firstPlayback"] = new JsonObject
            {
                ["title"] = fp.Title,
                ["angle"] = fp.Angle,
                ["audio"] = fp.Audio,
                ["subtitle"] = fp.Subtitle,
            };

        var newBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [KurokoLayout.DiscJson] = Encoding.UTF8.GetBytes(node.ToJsonString(JsonOpts)),
        };
        return KurokoRepacker.ReplaceEntries(krkPath, newBytes, progress, ct, ".firstplay.tmp");
    }

    // 「タイトル3：本編（01:23:45）」。名前が既定（"タイトル3"）なら重ねない。尺は disc.json に無ければ
    // title.json のクリーン実尺（timing.totalMs）で補う（BD 由来の krk は disc.json 側が 0 のことがある）。
    private static string TitleLabel(KurokoTitleEntry t, KurokoTitle? title)
    {
        var dur = t.DurationValue;
        if (dur <= TimeSpan.Zero && title?.Timing?.TotalMs > 0)
            dur = TimeSpan.FromMilliseconds(title.Timing.TotalMs);
        string d = dur > TimeSpan.Zero ? $"（{(int)dur.TotalHours:00}:{dur.Minutes:00}:{dur.Seconds:00}）" : "";
        string name = string.IsNullOrWhiteSpace(t.Name) || string.Equals(t.Name, $"タイトル{t.Id}", StringComparison.Ordinal)
            ? "" : $"：{t.Name}";
        return $"タイトル{t.Id}{name}{d}";
    }

    private static string AudioLabel(KurokoAudio a, int index)
    {
        string lang = string.IsNullOrWhiteSpace(a.Language) ? "" : $" [{a.Language}]";
        string dual = a.DualMonoRanges is { Count: > 0 } ? "・デュアルモノ（主音声で開始）" : "";
        return $"音声{index + 1}{lang}：{CodecDisplay(a.Codec)} {ChannelDisplay(a.Channels)}{dual}";
    }

    private static string SubtitleLabel(KurokoSubtitle s, int index)
    {
        string lang = string.IsNullOrWhiteSpace(s.Language) ? "" : $" [{s.Language}]";
        return $"字幕{index + 1}{lang}";
    }

    // 表示名（再生器の音声一覧と同じ見え方にする）。
    private static string CodecDisplay(string? codec) => (codec ?? "").ToLowerInvariant() switch
    {
        "opus" => "Opus",
        "ac3" => "AC-3",
        "dts" => "DTS",
        "pcm" or "lpcm" => "LPCM",
        "mp2" or "mpeg" or "mp1" => "MPEG Audio",
        "" => "Audio",
        _ => codec!.ToUpperInvariant(),
    };

    private static string ChannelDisplay(int ch) => ch switch
    {
        <= 0 => "",
        1 => "1ch",
        2 => "2ch",
        6 => "5.1ch",
        7 => "6.1ch",
        8 => "7.1ch",
        _ => ch.ToString(CultureInfo.InvariantCulture) + "ch",
    };
}
