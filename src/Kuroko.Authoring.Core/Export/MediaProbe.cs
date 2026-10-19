using System.Globalization;
using System.Text.Json;

namespace Kuroko.Authoring.Export;

// 元のファイル（または書き出した mkv）の中身を ffprobe で調べた結果。
public sealed class MediaInfo
{
    public double DurationMs { get; set; }
    public VideoInfo? Video { get; set; }
    public List<AudioInfo> Audio { get; set; } = new();
    public List<SubtitleInfo> Subtitles { get; set; } = new();
    public List<(double StartMs, string? Title)> Chapters { get; set; } = new();

    public sealed class VideoInfo
    {
        public int Index { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int SarNum { get; set; } = 1;
        public int SarDen { get; set; } = 1;
        public string FrameRate { get; set; } = "30000/1001";
        public string? FieldOrder { get; set; }
        // 本当にインターレースか（field_order が tt/bb/tb/bt）。
        public bool Interlaced => FieldOrder is "tt" or "bb" or "tb" or "bt";
        // 表示の幅（画素の縦横比を当てたもの）。
        public int DisplayWidth => SarNum > 0 && SarDen > 0 ? (int)Math.Round(Width * (double)SarNum / SarDen) : Width;
    }

    public sealed class AudioInfo
    {
        public int Index { get; set; }
        public string? Codec { get; set; }
        public int Channels { get; set; }
        public string? Language { get; set; }
        public string? Title { get; set; }
    }

    public sealed class SubtitleInfo
    {
        public int Index { get; set; }
        public string? Codec { get; set; }     // "arib_caption" など
        public int Pid { get; set; } = -1;     // TS の PID（ffprobe の id。"0x130" など）
        public string? Language { get; set; }
    }

    // タイトルの一覧に入れる、元のファイルの中の音声・字幕。
    public IEnumerable<Kuroko.Authoring.Project.AuthoringTitle.SourceAudio> SourceAudio()
        => Audio.Select(a => new Kuroko.Authoring.Project.AuthoringTitle.SourceAudio(a.Index, a.Codec, a.Channels, a.Language, a.Title));

    public IEnumerable<Kuroko.Authoring.Project.AuthoringTitle.SourceSubtitle> SourceSubtitles()
        => Subtitles.Select(x => new Kuroko.Authoring.Project.AuthoringTitle.SourceSubtitle(x.Index, x.Codec, x.Pid, x.Language));

    public static MediaInfo Probe(FfmpegTools tools, string path)
    {
        using var doc = tools.Probe(path, "-show_format", "-show_streams", "-show_chapters");
        var root = doc.RootElement;
        var info = new MediaInfo();
        if (root.TryGetProperty("format", out var fmt) && TryDouble(fmt, "duration", out double d)) info.DurationMs = d * 1000;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                string type = Str(s, "codec_type") ?? "";
                int index = s.TryGetProperty("index", out var ix) ? ix.GetInt32() : -1;
                bool attachedPic = s.TryGetProperty("disposition", out var disp)
                    && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video" && info.Video is null && !attachedPic)
                {
                    var v = new VideoInfo
                    {
                        Index = index,
                        Width = Int(s, "width"),
                        Height = Int(s, "height"),
                        FieldOrder = Str(s, "field_order"),
                    };
                    string rate = Str(s, "avg_frame_rate") ?? "0/0";
                    if (rate is "0/0" or "0/1") rate = Str(s, "r_frame_rate") ?? "30000/1001";
                    v.FrameRate = rate;
                    var sar = (Str(s, "sample_aspect_ratio") ?? "1:1").Split(':');
                    if (sar.Length == 2 && int.TryParse(sar[0], out int sn) && int.TryParse(sar[1], out int sd) && sn > 0 && sd > 0)
                    { v.SarNum = sn; v.SarDen = sd; }
                    info.Video = v;
                }
                else if (type == "audio")
                {
                    var tags = s.TryGetProperty("tags", out var t) ? t : default;
                    info.Audio.Add(new AudioInfo
                    {
                        Index = index,
                        Codec = Str(s, "codec_name"),
                        Channels = Int(s, "channels"),
                        Language = tags.ValueKind == JsonValueKind.Object ? Str(tags, "language") : null,
                        Title = tags.ValueKind == JsonValueKind.Object ? Str(tags, "title") : null,
                    });
                }
                else if (type == "subtitle")
                {
                    var tags = s.TryGetProperty("tags", out var t) ? t : default;
                    string? id = Str(s, "id");
                    int pid = id is null ? -1
                        : id.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            ? (int.TryParse(id.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hx) ? hx : -1)
                            : (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dc) ? dc : -1);
                    info.Subtitles.Add(new SubtitleInfo
                    {
                        Index = index,
                        Codec = Str(s, "codec_name"),
                        Pid = pid,
                        Language = tags.ValueKind == JsonValueKind.Object ? Str(tags, "language") : null,
                    });
                }
                if (info.DurationMs <= 0 && TryDouble(s, "duration", out double sdur)) info.DurationMs = Math.Max(info.DurationMs, sdur * 1000);
            }
        }
        if (root.TryGetProperty("chapters", out var chapters))
            foreach (var c in chapters.EnumerateArray())
                if (TryDouble(c, "start_time", out double st))
                    info.Chapters.Add((st * 1000, c.TryGetProperty("tags", out var ct) ? Str(ct, "title") : null));
        return info;
    }

    // 映像の枚数（書き出した mkv の検査用。数えるので遅い）。
    public static int CountVideoFrames(FfmpegTools tools, string path)
    {
        using var doc = tools.Probe(path, "-select_streams", "v:0", "-count_packets", "-show_entries", "stream=nb_read_packets");
        var s = doc.RootElement.GetProperty("streams");
        return s.GetArrayLength() > 0 && int.TryParse(Str(s[0], "nb_read_packets"), out int n) ? n : 0;
    }

    // 最初に復号できた絵の時刻（ms。ストリームの時刻のまま）。取れなければ NaN。
    // 字幕の時刻を krk の映像の 0 秒目へそろえるのに使う（変換器と同じ取り方）。
    public static double FirstVideoFrameMs(FfmpegTools tools, string path, int streamIndex)
    {
        try
        {
            using var doc = tools.Probe(path, "-select_streams", streamIndex.ToString(CultureInfo.InvariantCulture),
                "-show_entries", "frame=best_effort_timestamp_time", "-read_intervals", "%+5");
            if (doc.RootElement.TryGetProperty("frames", out var frames))
                foreach (var f in frames.EnumerateArray())
                    if (TryDouble(f, "best_effort_timestamp_time", out double sec)) return sec * 1000;
        }
        catch (InvalidOperationException) { }
        return double.NaN;
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

    private static int Int(JsonElement e, string name)
        => int.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

    private static bool TryDouble(JsonElement e, string name, out double v)
        => double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
