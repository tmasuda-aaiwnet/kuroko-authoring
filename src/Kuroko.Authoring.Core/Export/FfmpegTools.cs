using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kuroko.Authoring.Export;

// ffmpeg・ffprobe を別の実行ファイルとして呼ぶ（このツールには組み込まない。使う人が入れた ffmpeg を使う）。
// 必要なもの：映像の符号化器（libsvtav1・libx265・libx264 のうち使うもの）・libopus（Opus）・matroska の muxer。
public sealed class FfmpegTools
{
    public string FfmpegPath { get; }
    public string FfprobePath { get; }

    private FfmpegTools(string ffmpeg, string ffprobe)
    {
        FfmpegPath = ffmpeg;
        FfprobePath = ffprobe;
    }

    // ffmpegPath：ffmpeg の実行ファイル（空なら PATH から探す）。ffprobe は同じフォルダのものを使う。
    public static FfmpegTools Locate(string? ffmpegPath)
    {
        string exe = OperatingSystem.IsWindows() ? ".exe" : "";
        string ffmpeg = string.IsNullOrWhiteSpace(ffmpegPath) ? FindOnPath("ffmpeg" + exe) ?? "" : ffmpegPath;
        if (!File.Exists(ffmpeg))
            throw new FileNotFoundException("ffmpeg が見つかりません。設定で ffmpeg の場所を指定してください。", ffmpegPath ?? "ffmpeg");
        string ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe" + exe);
        if (!File.Exists(ffprobe)) ffprobe = FindOnPath("ffprobe" + exe) ?? "";
        if (!File.Exists(ffprobe))
            throw new FileNotFoundException("ffprobe が見つかりません（ffmpeg と同じフォルダに置いてください）。", ffprobe);
        return new FfmpegTools(ffmpeg, ffprobe);
    }

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string p = Path.Combine(dir.Trim(), name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // 符号化器が揃っているか。足りないものの名前を返す（空＝揃っている）。
    public List<string> MissingEncoders(params string[] needed)
    {
        var (_, output, _) = Run(FfmpegPath, new[] { "-hide_banner", "-encoders" }, CancellationToken.None);
        var missing = new List<string>();
        foreach (var name in needed)
            if (!output.Contains(" " + name + " ", StringComparison.Ordinal)) missing.Add(name);
        return missing;
    }

    // ffmpeg を実行する。durationMs>0 なら -progress の out_time から進み具合（0..1）を返す。
    // 失敗（終了コード≠0）は例外。標準エラーの末尾を例外の文に入れる。
    public void RunFfmpeg(IReadOnlyList<string> args, double durationMs, Action<double>? progress, CancellationToken ct)
    {
        var all = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };
        all.AddRange(args);
        var (code, _, err) = Run(FfmpegPath, all, ct, line =>
        {
            if (progress is null || durationMs <= 0 || !line.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
            if (long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us))
                progress(Math.Clamp(us / 1000.0 / durationMs, 0, 1));
        });
        if (code != 0) throw new InvalidOperationException($"ffmpeg が失敗しました（終了コード {code}）。\n{Tail(err)}");
    }

    // ffprobe の JSON を読む。
    public JsonDocument Probe(string path, params string[] what)
    {
        var args = new List<string> { "-v", "error", "-of", "json" };
        args.AddRange(what);
        args.Add(path);
        var (code, output, err) = Run(FfprobePath, args, CancellationToken.None);
        if (code != 0) throw new InvalidOperationException($"ffprobe が失敗しました：{path}\n{Tail(err)}");
        return JsonDocument.Parse(output);
    }

    private static string Tail(string s, int lines = 12)
    {
        var all = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }

    // 引数は ArgumentList で渡す（ファイル名の空白・日本語をそのまま渡せる）。
    private static (int Code, string Out, string Err) Run(string exe, IEnumerable<string> args, CancellationToken ct,
        Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
            p.WaitForExit();
        ct.ThrowIfCancellationRequested();
        return (p.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
