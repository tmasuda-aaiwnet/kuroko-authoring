using System.Globalization;
using System.Text;
using Kuroko.Arib;
using Kuroko.Authoring.Project;

namespace Kuroko.Authoring.Export;

// プロジェクトを krk に書き出す。タイトルの映像は ffmpeg で AV1＋Opus に符号化し、none と同じ器
//（disc.json・VIDEO/Tnn/title.json・SEGMENTS/S####/）に入れる。メニューがあれば controller="kuroko-tiny" にして
// nav.json・MENU/<menuId>/ を足す（メニューが無ければ controller="none"）。
//
// 1 タイトル＝1 セグメント。映像の符号化はタイトルごとの設定（AuthoringTitle.Video）。インターレースの素材は
// フィールド分離・weave のまま持つか、yadif・bwdif で解除する。音声・字幕はタイトルの一覧の順に、有効なものだけ入れる
//（元のファイルの中の音声・ARIB 字幕と、外部の音声・SRT）。ARIB 字幕は変換器と同じテキスト型（charset "arib"）。
public sealed class KrkExporter
{
    public sealed record Progress(double Fraction, string Phase);

    private readonly AuthoringProject _project;
    private readonly FfmpegTools _tools;
    private readonly StringBuilder _log = new();

    public KrkExporter(AuthoringProject project, FfmpegTools tools)
    {
        _project = project;
        _tools = tools;
    }

    public string Log => _log.ToString();

    public void Export(string outPath, IProgress<Progress>? progress = null, CancellationToken ct = default)
    {
        var problems = _project.Validate();
        if (problems.Count > 0)
            throw new InvalidOperationException("検査で問題が見つかったので書き出せません：\n" + string.Join('\n', problems));
        bool menuEncodes = _project.Menus.Any(m => m.Assets.Keys.Any(NeedsEncode));
        var needed = _project.Titles.Select(t => VideoOf(t).Encoder)
            .Concat(menuEncodes ? new[] { _project.Encode.Encoder } : Array.Empty<string>())
            .Append("libopus").Distinct().ToArray();
        var missing = _tools.MissingEncoders(needed);
        if (missing.Count > 0)
            throw new InvalidOperationException($"ffmpeg に符号化器がありません：{string.Join("、", missing)}");

        bool tiny = _project.Menus.Count > 0;
        string work = Path.Combine(Path.GetTempPath(), "kuroko_export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // 進み具合の重み：タイトルは元の長さ、メニューの動画・音声は 1 本 1 分ぶんとみなす。
            var titleInfos = _project.Titles.Select(t => MediaInfo.Probe(_tools, t.VideoFile)).ToList();
            // 元のファイルの中の音声・字幕をまだ一覧に入れていないタイトル（古いプロジェクト）は、ここで入れる。
            for (int i = 0; i < _project.Titles.Count; i++)
                _project.Titles[i].ListSourceTracks(titleInfos[i].SourceAudio(), titleInfos[i].SourceSubtitles(), _project.Encode);
            double menuWeight = 60000;
            double total = titleInfos.Sum(i => Math.Max(1000, i.DurationMs))
                + _project.Menus.Sum(m => m.Assets.Keys.Count(NeedsEncode)) * menuWeight + 1;
            double done = 0;
            Action<double> Step(double weight, string phase) => f =>
                progress?.Report(new Progress(Math.Clamp((done + weight * f) / total, 0, 0.99), phase));

            var writer = new KurokoZipWriter();
            writer.AddBytes(KurokoLayout.ManifestFile, KurokoJson.Serialize(new KurokoManifest
            {
                Controller = tiny ? KurokoLayout.ControllerKurokoTiny : KurokoLayout.ControllerNone,
                Version = KurokoLayout.CleanLayoutVersion,
                CreatedBy = "Kuroko Authoring",
                Comment = string.Join(" / ", _project.Titles.Select(t => VideoOf(t).Describe()).Distinct()) + " audio=opus",
            }));

            var disc = new KurokoNoneDisc();
            for (int i = 0; i < _project.Titles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = _project.Titles[i];
                var info = titleInfos[i];
                double w = Math.Max(1000, info.DurationMs);
                string label = $"タイトル {t.Id}（{i + 1}/{_project.Titles.Count}）";
                var entry = ExportTitle(t, info, $"S{i + 1:0000}", work, writer, Step(w, label + "：符号化中"), ct);
                disc.Titles.Add(entry);
                done += w;
            }
            writer.AddBytes(KurokoLayout.DiscJson, KurokoJson.Serialize(disc));

            if (tiny)
            {
                var pkg = _project.ToPackage();
                writer.AddBytes(KurokoLayout.TinyNavJson, KurokoJson.Serialize(pkg.Nav));
                foreach (var m in _project.Menus)
                {
                    ct.ThrowIfCancellationRequested();
                    writer.AddBytes(KurokoLayout.TinyMenuJson(m.Menu.Id), KurokoJson.Serialize(m.Menu));
                    foreach (var (name, src) in m.Assets)
                    {
                        string dst = Path.Combine(work, $"menu_{m.Menu.Id}_{name}");
                        if (NeedsEncode(name))
                        {
                            EncodeMenuAsset(name, src, dst, Step(menuWeight, $"メニュー「{m.Menu.Id}」：{name} を符号化中"), ct);
                            done += menuWeight;
                        }
                        else if (string.Equals(Path.GetExtension(name), Path.GetExtension(src), StringComparison.OrdinalIgnoreCase))
                            dst = src; // 画像は元のまま入れる
                        else
                            _tools.RunFfmpeg(new[] { "-i", src, "-frames:v", "1", "-update", "1", dst }, 0, null, ct);
                        writer.AddFile(KurokoLayout.TinyMenuFile(m.Menu.Id, name), dst);
                    }
                }
            }

            progress?.Report(new Progress(0.99, "krk にまとめています"));
            writer.Write(outPath);
            _log.AppendLine($"書き出し：{outPath}（{new FileInfo(outPath).Length:N0} バイト、タイトル {disc.Titles.Count}、メニュー {_project.Menus.Count}）");
            progress?.Report(new Progress(1, "完了"));
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    // 符号化して入れるもの（メニューの背景の動画・BGM）。画像はそのまま入れる。
    private static bool NeedsEncode(string name)
        => Path.GetExtension(name).ToLowerInvariant() is ".mkv" or ".mka";

    // タイトルの映像の設定（無ければ書き出しの設定）。
    private AuthoringVideoSettings VideoOf(AuthoringTitle t) => t.Video ?? _project.Encode;

    private KurokoNoneTitleEntry ExportTitle(AuthoringTitle t, MediaInfo info, string segId, string work,
        KurokoZipWriter writer, Action<double> progress, CancellationToken ct)
    {
        var v = info.Video ?? throw new InvalidOperationException($"タイトル {t.Id}：映像がありません（{t.VideoFile}）。");
        var vs = VideoOf(t);
        string av = Path.Combine(work, segId + ".mkv");

        // 入力：0＝映像のファイル、1..＝有効な外部の音声（一覧の順）。
        var audioTracks = t.Audio.Where(a => a.Enabled || !a.IsSource).ToList();
        var externals = audioTracks.Where(a => !a.IsSource).ToList();
        var args = new List<string> { "-i", t.VideoFile };
        foreach (var a in externals) { args.Add("-i"); args.Add(a.Path!); }

        // インターレースの扱い（素材がインターレースのときだけ。仕様の映像のケイデンス・インターレースの格納方式）。
        string mode = v.Interlaced ? AuthoringVideoSettings.NormalizeInterlace(vs.Interlace) : "progressive";
        bool fields = mode == AuthoringVideoSettings.InterlaceFields;
        bool weave = mode == AuthoringVideoSettings.InterlaceWeave;
        bool deint = mode is AuthoringVideoSettings.InterlaceYadif or AuthoringVideoSettings.InterlaceBwdif;
        bool tff = v.FieldOrder is not ("bb" or "bt");
        const string Even = "scale=trunc(iw/2)*2:trunc(ih/2)*2"; // 4:2:0 なので幅・高さは偶数にそろえる
        bool odd = v.Width % 2 != 0 || v.Height % 2 != 0;
        if (fields)
        {
            // 上下のフィールドを 2 本の映像トラックへ（0＝上・1＝下。どちらも半分の高さ・元のフレームレート・同じ時刻）。
            // どちらが先に表示されるか（フィールドの順）は seg.json の tff で再生器が付ける。
            args.AddRange(new[] { "-filter_complex",
                $"[0:{v.Index}]field=type=top,{Even}[vt];[0:{v.Index}]field=type=bottom,{Even}[vb]",
                "-map", "[vt]", "-map", "[vb]" });
        }
        else
            args.AddRange(new[] { "-map", $"0:{v.Index}" });
        // 音声：一覧の順に、有効なもの（元のファイルの中の音声は外したものを入れない）。
        var audioOut = new List<(int Channels, string? Language, string? Name, int Kbps)>();
        foreach (var a in audioTracks)
        {
            if (a.SourceIndex is int si)
            {
                var src = info.Audio.FirstOrDefault(x => x.Index == si)
                    ?? throw new InvalidOperationException($"タイトル {t.Id}：映像のファイルの中に音声（ストリーム {si}）がありません（ファイルが変わったかもしれません）。");
                args.AddRange(new[] { "-map", $"0:{si}" });
                audioOut.Add((src.Channels, a.Language, a.Name, a.BitrateKbps));
            }
            else
            {
                int k = externals.IndexOf(a) + 1;
                var ext = MediaInfo.Probe(_tools, a.Path!);
                if (ext.Audio.Count == 0) throw new InvalidOperationException($"タイトル {t.Id}：音声がありません（{a.Path}）。");
                args.AddRange(new[] { "-map", $"{k}:{ext.Audio[0].Index}" });
                audioOut.Add((ext.Audio[0].Channels, a.Language ?? ext.Audio[0].Language, a.Name ?? ext.Audio[0].Title, a.BitrateKbps));
            }
        }

        // 解除（yadif・bwdif）はフィールドごとに 1 枚（倍のフレームレート）。weave・プログレッシブはそのまま。
        var vf = new List<string>();
        if (deint) vf.Add($"{mode}=mode=send_field");
        if (!fields && odd) vf.Add(Even);
        if (vf.Count > 0) { args.Add("-vf"); args.Add(string.Join(',', vf)); }
        double outFps = FrameRateValue(v.FrameRate) * (deint ? 2 : 1);
        int keyint = Math.Max(1, (int)Math.Round(outFps * 2)); // シークのためキーフレームは約 2 秒ごと
        args.AddRange(vs.VideoArgs(keyint, weave && vs.VideoCodec == AuthoringVideoSettings.H264 ? tff : null));

        args.AddRange(new[] { "-c:a", "libopus" });
        for (int n = 0; n < audioOut.Count; n++)
        {
            int ch = Math.Max(1, audioOut[n].Channels);
            // ビットレートは 2ch あたりの値。5.1・7.1 は Opus の決まった並びへそろえる（5.1(side) などは受け付けないため）。
            args.AddRange(new[] { $"-b:a:{n}", $"{Math.Max(6, audioOut[n].Kbps) * Math.Max(1, (ch + 1) / 2)}k" });
            if (ch == 6) args.AddRange(new[] { $"-filter:a:{n}", "aformat=channel_layouts=5.1" });
            else if (ch == 8) args.AddRange(new[] { $"-filter:a:{n}", "aformat=channel_layouts=7.1" });
            if (!string.IsNullOrEmpty(audioOut[n].Language))
                args.AddRange(new[] { $"-metadata:s:a:{n}", $"language={audioOut[n].Language}" });
        }
        args.AddRange(new[] { "-map_metadata", "-1", "-map_chapters", "-1", "-sn", "-dn", av });
        _tools.RunFfmpeg(args, info.DurationMs, progress, ct);

        var outInfo = MediaInfo.Probe(_tools, av);
        var ov = outInfo.Video!;
        double totalMs = Math.Round(outInfo.DurationMs, 3);
        _log.AppendLine($"タイトル {t.Id}：{ov.Width}x{ov.Height} {ov.FrameRate} {totalMs / 1000:F1}秒 音声 {audioOut.Count} 本"
            + (v.Interlaced ? $"（インターレース：{mode}・{(tff ? "上が先" : "下が先")}）" : ""));

        // seg.json（メディアの属性）。フィールド分離は半分の高さの 2 本（表示の高さも半分で書く。再生器が全高へ戻す）。
        bool keepInterlace = fields || weave;
        int displayWidth = fields ? v.DisplayWidth : ov.DisplayWidth;
        double ratio = fields ? v.DisplayWidth / (double)Math.Max(1, v.Height) : ov.DisplayWidth / (double)Math.Max(1, ov.Height);
        var seg = new KurokoNoneSegment
        {
            Video = new KurokoNoneVideo
            {
                Codec = vs.VideoCodec,
                Mode = "transcode",
                PixelWidth = ov.Width,
                PixelHeight = ov.Height,
                DisplayWidth = displayWidth,
                DisplayHeight = ov.Height,
                Aspect = ratio >= 1.55 ? "16:9" : "4:3",
                // 出力の実測（avg_frame_rate）は端数が出るので、素材の値から（解除は倍）。
                FrameRate = deint ? DoubleRate(v.FrameRate) : keepInterlace ? v.FrameRate : ov.FrameRate,
                Cadence = keepInterlace ? "interlaced" : "progressive",
                Tff = tff,
                InterlaceMode = fields ? "fields" : "weave",
                FieldLayout = fields ? "dual" : "single",
                // 全体が本当のインターレース（素材の途中でケイデンスが変わるものは、ここでは扱わない）。
                InterlacedRanges = keepInterlace ? new List<KurokoRange> { new() { StartMs = 0, EndMs = totalMs } } : null,
            },
            Timing = new KurokoNoneTiming { TotalMs = totalMs },
        };
        var span = new KurokoNoneAudioSpan { StartMs = 0, EndMs = totalMs };
        for (int n = 0; n < audioOut.Count; n++)
            span.Tracks.Add(new KurokoNoneAudioTrack
            {
                Track = n + (fields ? 2 : 1),   // 映像の後ろ（フィールド分離は映像が 2 本）
                Source = n,
                Codec = "opus",
                Channels = outInfo.Audio.Count > n ? outInfo.Audio[n].Channels : audioOut[n].Channels,
                Language = KurokoLanguage.Normalize(audioOut[n].Language),
                Name = audioOut[n].Name,
            });
        if (span.Tracks.Count > 0) seg.Audio.Add(span);

        // title.json（論理）。
        var title = new KurokoNoneTitle { Id = t.Id, Name = t.Name, Timing = new KurokoNoneTiming { TotalMs = totalMs } };
        title.Playlist.Add(new KurokoNonePlaylistItem { Seg = segId, StartMs = 0 });
        for (int n = 0; n < audioOut.Count; n++)
            title.Audio.Add(new KurokoNoneAudioLogical { Number = n + 1, Source = n, Language = KurokoLanguage.Normalize(audioOut[n].Language) });

        // 字幕：一覧の順に、有効なもの（テキスト型）。source は入れた順の番号。
        int sn = 0;
        double originMs = double.NaN;   // ARIB 字幕の時刻の基準（krk の映像の 0 秒目にあたる元の時刻）
        foreach (var st in t.Subtitles.Where(x => x.Enabled || !x.IsSource))
        {
            ct.ThrowIfCancellationRequested();
            KurokoSubsText text;
            if (st.Kind == AuthoringSubtitleTrack.KindArib)
            {
                if (double.IsNaN(originMs))
                {
                    // 元の最初の絵の時刻 − 書き出した mkv の最初の絵の時刻（ffmpeg は元の時刻を起点だけずらして書く）。
                    double srcFirst = MediaInfo.FirstVideoFrameMs(_tools, t.VideoFile, v.Index);
                    double outFirst = MediaInfo.FirstVideoFrameMs(_tools, av, ov.Index);
                    if (double.IsNaN(srcFirst) || double.IsNaN(outFirst))
                        throw new InvalidOperationException($"タイトル {t.Id}：字幕の時刻の基準（最初の絵の時刻）が取れません。");
                    originMs = srcFirst - outFirst;
                }
                var built = BuildAribCaption(t, st, info, originMs, totalMs, ct);
                if (built is null) continue;
                text = built;
            }
            else
            {
                var cues = SrtReader.Read(st.Path!);
                text = SrtReader.ToKuroko(cues, st.Language, st.Name ?? Path.GetFileNameWithoutExtension(st.Path), ov.DisplayWidth, ov.Height);
                _log.AppendLine($"タイトル {t.Id}：字幕 {Path.GetFileName(st.Path)} {cues.Count} 件");
            }
            writer.AddBytes(KurokoLayout.SubsTextJson(segId, sn), KurokoJson.Serialize(text));
            seg.Subtitle.Add(new KurokoNoneSubtitle { Source = sn, Track = -1, Type = "text" });
            title.Subtitle.Add(new KurokoNoneSubtitleLogical { Number = sn + 1, Source = sn, Type = "text", Language = text.Lang, Name = text.Name });
            sn++;
        }

        // 章：プロジェクトで決めたもの。無ければ元のファイルの章。
        var chapters = t.Chapters.Count > 0
            ? t.Chapters.Select(c => (c.StartMs, c.Name)).ToList()
            : info.Chapters.Select(c => (c.StartMs, Name: c.Title)).ToList();
        int no = 1;
        foreach (var (startMs, name) in chapters.Where(c => c.StartMs < totalMs).OrderBy(c => c.StartMs))
            title.Chapters.Add(new KurokoNoneChapter { Number = no++, StartMs = Math.Max(0, startMs), Name = name });

        writer.AddFile(KurokoLayout.SegmentAvMkv(segId), av);
        writer.AddBytes(KurokoLayout.SegmentJson(segId), KurokoJson.Serialize(seg));
        writer.AddBytes(KurokoLayout.TitleJson(t.Id), KurokoJson.Serialize(title));
        return new KurokoNoneTitleEntry
        {
            Id = t.Id,
            Name = t.Name,
            DurationMs = totalMs,
            Video = KurokoLayout.TitleDir(t.Id).TrimStart('/'),
        };
    }

    // 元のファイル（TS）の中の ARIB 字幕を、テキスト型（charset "arib"）へ写す。時刻は krk の映像の 0 秒目＝originMs を引く。
    // 一つの字幕の PID の第 1 言語だけ（第 2 言語は、まだ一覧に出さない）。表示が一つも無ければ null（入れない）。
    private KurokoSubsText? BuildAribCaption(AuthoringTitle t, AuthoringSubtitleTrack st, MediaInfo info, double originMs,
        double totalMs, CancellationToken ct)
    {
        int pid = st.Pid ?? -1;
        if (!info.Subtitles.Any(x => x.Pid == pid))
            throw new InvalidOperationException($"タイトル {t.Id}：映像のファイルの中に字幕（PID 0x{pid:X4}）がありません（ファイルが変わったかもしれません）。");
        var pes = new List<(long Pts, byte[] Data)>();
        using (var fs = File.OpenRead(t.VideoFile))
            AribCaptionExtractor.Extract(fs, pid, (pts, data) => pes.Add((pts, data)), ct);
        long origin90k = (long)Math.Round(originMs * 90.0);
        var dec = new AribCaptionDecoder(1);
        var conv = new AribCaptionToKuroko();
        // 時刻は届いた順に連続値へ直す（26.5 時間ごとの PTS のラップを跨ぐ長時間録画のため）。
        var unwrap = new AribCaptionExtractor.Unwrapper(origin90k);
        foreach (var (pts, data) in pes)
        {
            long cont = unwrap.Next(pts);
            var c = dec.Decode(data);
            if (c is null) continue;
            conv.Add((cont - origin90k) / 90.0, c);
        }
        string? lang = st.Language ?? dec.Languages[0];
        var text = conv.Build(totalMs, lang, st.Name ?? "字幕");
        _log.AppendLine($"タイトル {t.Id}：字幕 PID 0x{pid:X4}（ARIB）：PES {pes.Count} 件 → 表示 {conv.CueCount} 回／DRCS {conv.DrcsCount} 字"
            + $"（時刻の基準 {originMs:F1}ms）");
        return text.Cues.Count > 0 ? text : null;
    }

    // メニューの背景の動画（.mkv：映像＋音声があれば音声）と BGM（.mka：音声だけ）。
    private void EncodeMenuAsset(string name, string src, string dst, Action<double> progress, CancellationToken ct)
    {
        var info = MediaInfo.Probe(_tools, src);
        var enc = _project.Encode;
        var args = new List<string> { "-i", src };
        if (Path.GetExtension(name).Equals(".mka", StringComparison.OrdinalIgnoreCase))
        {
            if (info.Audio.Count == 0) throw new InvalidOperationException($"音声がありません（{src}）。");
            args.AddRange(new[] { "-map", $"0:{info.Audio[0].Index}", "-c:a", "libopus", "-b:a", $"{enc.AudioBitrateKbps}k", "-ac", "2", "-vn" });
        }
        else
        {
            var v = info.Video ?? throw new InvalidOperationException($"映像がありません（{src}）。");
            args.AddRange(new[] { "-map", $"0:{v.Index}" });
            if (info.Audio.Count > 0)
                args.AddRange(new[] { "-map", $"0:{info.Audio[0].Index}", "-c:a", "libopus", "-b:a", $"{enc.AudioBitrateKbps}k", "-ac", "2" });
            var vf = new List<string>();
            if (v.Interlaced) vf.Add("bwdif=mode=send_frame");
            if (v.Width % 2 != 0 || v.Height % 2 != 0) vf.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
            if (vf.Count > 0) { args.Add("-vf"); args.Add(string.Join(',', vf)); }
            int keyint = Math.Max(1, (int)Math.Round(FrameRateValue(v.FrameRate) * 2));
            args.AddRange(enc.VideoArgs(keyint));
        }
        args.AddRange(new[] { "-map_metadata", "-1", "-map_chapters", "-1", "-sn", "-dn", dst });
        _tools.RunFfmpeg(args, info.DurationMs, progress, ct);
    }

    private static double FrameRateValue(string rate)
    {
        var p = rate.Split('/');
        if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
            && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0)
            return n / d;
        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : 30;
    }

    // 「分子/分母」のフレームレートを倍に（30000/1001 → 60000/1001）。
    private static string DoubleRate(string rate)
    {
        var p = rate.Split('/');
        if (p.Length == 2 && long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
            return $"{n * 2}/{p[1]}";
        return (FrameRateValue(rate) * 2).ToString(CultureInfo.InvariantCulture);
    }

    private static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);
}
