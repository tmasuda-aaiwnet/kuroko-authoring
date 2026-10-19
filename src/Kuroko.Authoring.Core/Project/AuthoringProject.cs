using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuroko.Authoring.Project;

// オーサリングのプロジェクト（作りかけの状態）。.kap（JSON）として保存する。
// krk に書く中身（ナビゲーション・メニュー）は SDK の kuroko-tiny のモデルをそのまま持ち、それに加えて
// 「元になるファイルがどこにあるか」と「符号化の設定」を持つ。書き出しは Export/KrkExporter。
public sealed class AuthoringProject
{
    public const string FileExtension = ".kap";
    public int Version { get; set; } = 1;

    public string Name { get; set; } = "新しいディスク";
    public List<AuthoringTitle> Titles { get; set; } = new();
    public List<AuthoringMenu> Menus { get; set; } = new();
    public KurokoTinyNav Nav { get; set; } = new();
    public AuthoringEncodeSettings Encode { get; set; } = new();

    // 次に足すタイトルの番号（disc.json の titles[].id。1 始まり）。
    public int NextTitleId() => Titles.Count == 0 ? 1 : Titles.Max(t => t.Id) + 1;

    // プロジェクトの中の動作をすべて（firstPlay・onEnd・timeout・ボタン）。
    public IEnumerable<KurokoTinyAction> AllActions()
    {
        if (Nav.FirstPlay is { } fp) yield return fp;
        foreach (var t in Nav.Titles) if (t.OnEnd is { } e) yield return e;
        foreach (var m in Menus)
        {
            if (m.Menu.Timeout?.Action is { } ta) yield return ta;
            foreach (var b in m.Menu.Buttons) yield return b.Action;
        }
    }

    // メニューの識別子を変える（参照している動作・キーも直す）。
    public void RenameMenu(string oldId, string newId)
    {
        foreach (var m in Menus) if (m.Menu.Id == oldId) m.Menu.Id = newId;
        foreach (var a in AllActions()) if (a.ShowMenu == oldId) a.ShowMenu = newId;
        if (Nav.Keys is { } k)
        {
            if (k.Menu == oldId) k.Menu = newId;
            if (k.TitleMenu == oldId) k.TitleMenu = newId;
        }
    }

    // 新しいメニューの識別子（main、menu2、menu3…）。
    public string NewMenuId()
    {
        if (Menus.All(m => m.Menu.Id != "main")) return "main";
        for (int i = 2; ; i++)
            if (Menus.All(m => m.Menu.Id != $"menu{i}")) return $"menu{i}";
    }

    // 検査と書き出し用に、krk に書く形（kuroko-tiny のパッケージ）へ組み立てる。nav.json の menus はメニューの並びから作る。
    public KurokoTinyPackage ToPackage()
    {
        var pkg = new KurokoTinyPackage
        {
            Disc = new KurokoNoneDisc
            {
                Titles = Titles.Select(t => new KurokoNoneTitleEntry
                {
                    Id = t.Id,
                    Name = t.Name,
                    Video = KurokoLayout.TitleDir(t.Id).TrimStart('/'),
                }).ToList(),
            },
            Nav = Nav,
        };
        Nav.Menus = Menus.Select(m => m.Menu.Id).ToList();
        foreach (var m in Menus) pkg.Menus.TryAdd(m.Menu.Id, m.Menu);
        return pkg;
    }

    // 参照の食い違いと、元のファイルが見つからないものを調べる。
    public List<string> Validate()
    {
        var problems = new List<string>();
        if (Titles.Count == 0) problems.Add("タイトルがありません。");
        foreach (var t in Titles)
        {
            if (string.IsNullOrEmpty(t.VideoFile) || !File.Exists(t.VideoFile))
                problems.Add($"タイトル {t.Id}：映像のファイルが見つかりません（{t.VideoFile}）。");
            foreach (var a in t.Audio.Where(a => !a.IsSource && !File.Exists(a.Path)))
                problems.Add($"タイトル {t.Id}：音声のファイルが見つかりません（{a.Path}）。");
            foreach (var s in t.Subtitles.Where(s => !s.IsSource && !File.Exists(s.Path)))
                problems.Add($"タイトル {t.Id}：字幕のファイルが見つかりません（{s.Path}）。");
        }
        var byId = Menus.GroupBy(m => m.Menu.Id).ToDictionary(g => g.Key, g => g.First());
        problems.AddRange(ToPackage().Validate((menuId, name) =>
            byId.TryGetValue(menuId, out var m) && m.Assets.TryGetValue(name, out var src) && File.Exists(src)));
        return problems;
    }

    // JSON はソース生成（AuthoringJsonContext）で読み書きする（SDK と同じく、反射に頼らない）。
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, AuthoringJsonContext.Default.AuthoringProject));

    public static AuthoringProject Load(string path)
    {
        var p = JsonSerializer.Deserialize(File.ReadAllText(path), AuthoringJsonContext.Default.AuthoringProject)
                ?? throw new InvalidDataException("プロジェクトを読めません。");
        p.Migrate();
        return p;
    }

    // 古いプロジェクトの項目を今の形へ移す：タイトルの映像の設定（無ければ書き出しの設定の値。インターレースの扱いは
    // タイトルにあったもの）、外部の音声・字幕のファイル（ビットレートは書き出しの設定の値）。元のファイルの中の
    // 音声・字幕は、ffprobe で調べてから ListSourceTracks で先頭へ入れる（前の作りと同じく、中の音声が先に並ぶ）。
    public void Migrate()
    {
        foreach (var t in Titles)
        {
            if (t.Video is null)
            {
                t.Video = Encode.CopyVideo();
                if (t.Interlace is not null) t.Video.Interlace = AuthoringVideoSettings.NormalizeInterlace(t.Interlace);
            }
            t.Interlace = null;
            if (t.AudioFiles is { } af)
                t.Audio.AddRange(af.Select(a => new AuthoringAudioTrack
                    { Path = a.Path, Language = a.Language, Name = a.Name, BitrateKbps = Encode.AudioBitrateKbps }));
            if (t.SubtitleFiles is { } sf)
                t.Subtitles.AddRange(sf.Select(x => new AuthoringSubtitleTrack
                    { Kind = AuthoringSubtitleTrack.KindSrt, Path = x.Path, Language = x.Language, Name = x.Name }));
            t.AudioFiles = null;
            t.SubtitleFiles = null;
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AuthoringProject))]
internal sealed partial class AuthoringJsonContext : JsonSerializerContext
{
}

public sealed class AuthoringTitle
{
    public int Id { get; set; }
    public string? Name { get; set; }
    // 元の映像のファイル（中の音声・字幕も使える）。
    public string VideoFile { get; set; } = "";
    // 映像の符号化の設定（足したときに「書き出しの設定」の値を写す）。古いプロジェクトは無い（読み込みで埋める）。
    public AuthoringVideoSettings? Video { get; set; }
    // 音声（この並びが krk の音声の番号になる）。元のファイルの中の音声と、外部の音声のファイル。
    public List<AuthoringAudioTrack> Audio { get; set; } = new();
    // 字幕（この並びが krk の字幕の番号になる）。元のファイルの中の ARIB 字幕と、外部の SRT。
    public List<AuthoringSubtitleTrack> Subtitles { get; set; } = new();
    // 元のファイルの中の音声・字幕を一覧に入れたか。ffprobe が要るので、足したとき・開いたとき・書き出しのときに
    // ListSourceTracks で入れる（古いプロジェクトはまだ入れていない）。
    public bool SourceTracksListed { get; set; }
    public List<KurokoNoneChapter> Chapters { get; set; } = new();

    // 古いプロジェクト（.kap）の項目。読み込みだけ（Migrate で上の項目へ移し、null にして書かない）。
    public List<AuthoringAudioFile>? AudioFiles { get; set; }
    public List<AuthoringSubtitleFile>? SubtitleFiles { get; set; }
    public string? Interlace { get; set; }

    // 元のファイルの中の音声・字幕を、一覧の先頭へ元の並びで入れる（音声のビットレートは「書き出しの設定」の値）。
    // 字幕は、まず ARIB 字幕（TS）だけ。
    public void ListSourceTracks(IEnumerable<SourceAudio> audio, IEnumerable<SourceSubtitle> subtitles, AuthoringEncodeSettings defaults)
    {
        if (SourceTracksListed) return;
        Audio.InsertRange(0, audio.Select(a => new AuthoringAudioTrack
        {
            SourceIndex = a.Index,
            Codec = a.Codec,
            Channels = a.Channels,
            Language = a.Language,
            Name = a.Title,
            BitrateKbps = defaults.AudioBitrateKbps,
        }));
        Subtitles.InsertRange(0, subtitles.Where(s => s.Codec == AuthoringSubtitleTrack.KindArib && s.Pid >= 0).Select(s => new AuthoringSubtitleTrack
        {
            Kind = AuthoringSubtitleTrack.KindArib,
            SourceIndex = s.Index,
            Pid = s.Pid,
            Language = s.Language,
            Name = "字幕",
        }));
        SourceTracksListed = true;
    }

    public sealed record SourceAudio(int Index, string? Codec, int Channels, string? Language, string? Title);
    public sealed record SourceSubtitle(int Index, string? Codec, int Pid, string? Language);
}

// 音声 1 本。元のファイルの中の音声（SourceIndex）か、外部の音声のファイル（Path）。
public sealed class AuthoringAudioTrack
{
    public int? SourceIndex { get; set; }       // 元のファイルの中のストリームの番号（ffprobe の index）
    public string? Path { get; set; }           // 外部の音声のファイル
    public bool Enabled { get; set; } = true;   // 元のファイルの中の音声は、外すと krk に入れない（外部のファイルは常に有効）
    public string? Codec { get; set; }          // 表示用（元のファイルの中の音声）
    public int Channels { get; set; }           // 表示用（元のファイルの中の音声）
    public string? Language { get; set; }       // ISO 639-2（3 文字）
    public string? Name { get; set; }
    public int BitrateKbps { get; set; } = 128; // Opus（2ch あたり）
    public bool IsSource => SourceIndex is not null;
}

// 字幕 1 本。元のファイルの中の ARIB 字幕（Kind＝arib_caption・SourceIndex・Pid）か、外部の SRT（Kind＝srt・Path）。
public sealed class AuthoringSubtitleTrack
{
    public const string KindSrt = "srt", KindArib = "arib_caption";

    public string Kind { get; set; } = KindSrt;
    public int? SourceIndex { get; set; }
    public int? Pid { get; set; }
    public string? Path { get; set; }
    public bool Enabled { get; set; } = true;   // 元のファイルの中の字幕は、外すと krk に入れない
    public string? Language { get; set; }
    public string? Name { get; set; }
    public bool IsSource => SourceIndex is not null;
}

// 古いプロジェクトの外部の音声・字幕（読み込みだけ）。
public sealed class AuthoringAudioFile
{
    public string Path { get; set; } = "";
    public string? Language { get; set; }
    public string? Name { get; set; }
}

public sealed class AuthoringSubtitleFile
{
    public string Path { get; set; } = "";
    public string? Language { get; set; }
    public string? Name { get; set; }
}

public sealed class AuthoringMenu
{
    // krk の menu.json に書く中身。ファイル名は krk の MENU/<menuId>/ の中の名前。
    public KurokoTinyMenu Menu { get; set; } = new();
    // krk の中の名前 → 元のファイル（背景の画像・動画、BGM、ボタンの画像）。
    public Dictionary<string, string> Assets { get; set; } = new(StringComparer.Ordinal);
}

// 映像の符号化の設定。タイトルごとに持つ（AuthoringTitle.Video）。「書き出しの設定」（AuthoringEncodeSettings）は
// これを継いで既定値として持ち、タイトルを足したときに写す。
public class AuthoringVideoSettings
{
    public const string Av1 = "av1", H265 = "h265", H264 = "h264";

    public string VideoCodec { get; set; } = Av1;         // "av1"（SVT-AV1）・"h265"（x265）・"h264"（x264）
    public int Crf { get; set; } = 32;                    // 画質（小さいほど高画質・大きい）。既定はコーデックごと（DefaultCrf）
    public int Preset { get; set; } = 6;                  // SVT-AV1 の速さの段階（0〜13。大きいほど速く粗い）
    public string X26xPreset { get; set; } = "medium";    // x264・x265 の preset（ultrafast〜veryslow）

    // インターレースの映像の扱い（素材がインターレースのときだけ効く。プログレッシブの素材はそのまま）。
    //   fields＝フィールド分離（上下のフィールドを半分の高さの 2 本の映像にして持つ。再生器が元の表示に戻す。既定）
    //   weave ＝1 枚に織り込んだまま持つ（H.264 はインターレース符号化。H.265・AV1 はそのまま符号化）
    //   yadif・bwdif＝書き出しの時に解除する（フィールドごとに 1 枚。倍のフレームレートのプログレッシブ）
    public string Interlace { get; set; } = InterlaceFields;

    public const string InterlaceFields = "fields", InterlaceWeave = "weave", InterlaceYadif = "yadif", InterlaceBwdif = "bwdif";
    public static readonly string[] InterlaceModes = { InterlaceFields, InterlaceWeave, InterlaceYadif, InterlaceBwdif };

    public static string NormalizeInterlace(string? mode)
        => InterlaceModes.FirstOrDefault(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase)) ?? InterlaceFields;

    public static readonly string[] X26xPresets =
        { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" };

    // コーデックごとの CRF の既定（AV1 は変換器の既定、H.265・H.264 は見た目の画質がほぼそろう目安）。
    public static int DefaultCrf(string codec) => codec switch { H265 => 22, H264 => 20, _ => 32 };

    // 映像の設定だけを写す（タイトルを足すとき・全タイトルへ適用）。
    public AuthoringVideoSettings CopyVideo() => new()
    {
        VideoCodec = VideoCodec, Crf = Crf, Preset = Preset, X26xPreset = X26xPreset, Interlace = NormalizeInterlace(Interlace),
    };

    // ffmpeg の符号化器の名前。
    public string Encoder => VideoCodec switch { H265 => "libx265", H264 => "libx264", _ => "libsvtav1" };

    // 映像の符号化の指定（-c:v 以降）。keyint＝キーフレームの間隔（枚数）。
    // h264InterlacedTff：インターレースのまま（weave）H.264 で符号化するときのフィールドの順（null＝プログレッシブ）。
    public IEnumerable<string> VideoArgs(int keyint, bool? h264InterlacedTff = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // H.264・H.265 は各キーフレームにパラメータセット（SPS/PPS、H.265 は VPS も）を入れる（repeat-headers）。
        // 再生器は mkv の CodecPrivate に頼らずストリームをそのまま復号器へ渡す（どこからでもシークして復号できる）。
        if (VideoCodec == H265)
            return new[] { "-c:v", Encoder, "-preset", X26xPresets.Contains(X26xPreset) ? X26xPreset : "medium",
                "-crf", Crf.ToString(inv), "-pix_fmt", "yuv420p", "-g", keyint.ToString(inv),
                "-x265-params", "repeat-headers=1:log-level=error" };
        if (VideoCodec == H264 && h264InterlacedTff is bool tff)
            return new[] { "-c:v", Encoder, "-preset", X26xPresets.Contains(X26xPreset) ? X26xPreset : "medium",
                "-crf", Crf.ToString(inv), "-pix_fmt", "yuv420p", "-g", keyint.ToString(inv),
                "-flags", "+ilme+ildct", "-x264-params", "repeat-headers=1:" + (tff ? "tff=1" : "bff=1") };
        if (VideoCodec == H264)
            return new[] { "-c:v", Encoder, "-preset", X26xPresets.Contains(X26xPreset) ? X26xPreset : "medium",
                "-crf", Crf.ToString(inv), "-pix_fmt", "yuv420p", "-g", keyint.ToString(inv),
                "-x264-params", "repeat-headers=1" };
        return new[] { "-c:v", Encoder, "-preset", Preset.ToString(inv), "-crf", Crf.ToString(inv),
            "-pix_fmt", "yuv420p", "-g", keyint.ToString(inv) };
    }

    // 符号化の条件（manifest の comment）。
    public string Describe() => VideoCodec is H265 or H264
        ? $"video={VideoCodec} crf={Crf} preset={X26xPreset}"
        : $"video={VideoCodec} crf={Crf} preset={Preset}";
}

// 書き出しの設定：映像・音声の既定値（タイトル・音声を足したときに写す）と、ffmpeg の場所。
// メニューの背景の動画・BGM も、この値で符号化する（背景の動画のインターレースは常に bwdif で解除）。
public sealed class AuthoringEncodeSettings : AuthoringVideoSettings
{
    public int AudioBitrateKbps { get; set; } = 128;      // Opus（2ch あたり）
    // ffmpeg の実行ファイル。空＝PATH から探す。
    public string? FfmpegPath { get; set; }

    // 全タイトルの映像の設定と、全音声のビットレートを、この値にする。
    public void ApplyToAll(AuthoringProject project)
    {
        foreach (var t in project.Titles)
        {
            t.Video = CopyVideo();
            foreach (var a in t.Audio) a.BitrateKbps = AudioBitrateKbps;
        }
    }
}
