#:project ../src/Kuroko.Authoring.Core/Kuroko.Authoring.Core.csproj
// Kuroko の仕様のサンプル（kuroko-spec の samples/）を作る。素材（試験の映像・音・字幕・メニューの画像）も ffmpeg で作るので、
// 手元に素材は要らない。使い方：
//   dotnet run samples/make-samples.cs -- <出力フォルダ> [ffmpeg の場所]
// ffmpeg は SVT-AV1（libsvtav1）・Opus（libopus）入りのもの。メニューのボタンの文字は ffmpeg の drawtext（freetype）があれば入れる
//（無ければ色の箱だけ）。文字の書体は環境変数 SAMPLE_FONT（無ければ OS の標準の書体を探す）。
using System.Diagnostics;
using Kuroko;
using Kuroko.Authoring.Export;
using Kuroko.Authoring.Project;

if (args.Length < 1) { Console.Error.WriteLine("usage: make-samples.cs <outdir> [ffmpeg]"); return 1; }
string outDir = Path.GetFullPath(args[0]);
Directory.CreateDirectory(outDir);
var tools = FfmpegTools.Locate(args.Length > 1 ? args[1] : null);
string work = Path.Combine(Path.GetTempPath(), "kuroko_samples_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
string W(string name) => Path.Combine(work, name);

void Ff(params string[] a)
{
    var psi = new ProcessStartInfo(tools.FfmpegPath) { RedirectStandardError = true, UseShellExecute = false };
    foreach (var x in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(a)) psi.ArgumentList.Add(x);
    using var p = Process.Start(psi)!;
    string err = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg: " + err);
}

// ── 素材 ──
// 1 本目：640x360・30fps・6 秒のプログレッシブ、音声 1 本。
Ff("-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=6", "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
   "-c:v", "ffv1", "-c:a", "pcm_s16le", W("basic.mkv"));
// 2 本目：音声 2 本（日本語・英語の想定）。3 本目：別の絵。
Ff("-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=5", "-f", "lavfi", "-i", "sine=frequency=440:duration=5",
   "-f", "lavfi", "-i", "sine=frequency=660:duration=5", "-map", "0", "-map", "1", "-map", "2",
   "-c:v", "ffv1", "-c:a", "pcm_s16le", "-metadata:s:a:0", "language=jpn", "-metadata:s:a:1", "language=eng", W("multi1.mkv"));
Ff("-f", "lavfi", "-i", "smptebars=size=640x360:rate=30:duration=5", "-f", "lavfi", "-i", "sine=frequency=550:duration=5",
   "-c:v", "ffv1", "-c:a", "pcm_s16le", W("multi2.mkv"));
File.WriteAllText(W("multi1.ja.srt"), "1\n00:00:00,500 --> 00:00:02,500\nこれは日本語の字幕です。\n\n2\n00:00:03,000 --> 00:00:04,800\nKuroko のサンプル\n");
File.WriteAllText(W("multi1.en.srt"), "1\n00:00:00,500 --> 00:00:02,500\nThis is an English subtitle.\n\n2\n00:00:03,000 --> 00:00:04,800\nA Kuroko sample\n");
// インターレース：720x480・29.97i（上のフィールドが先）。MPEG-2 でインターレースとして符号化して、フィールドの順を持たせる。
Ff("-f", "lavfi", "-i", "testsrc2=size=720x480:rate=60000/1001:duration=4", "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
   "-vf", "tinterlace=mode=interleave_top,setfield=tff", "-c:v", "mpeg2video", "-q:v", "2", "-flags", "+ilme+ildct", "-top", "1",
   "-c:a", "mp2", "-b:a", "192k", W("interlaced.ts"));
// メニューの画像：背景と、ボタン 2 つ（ふだん・選ばれているとき）。
string? font = Environment.GetEnvironmentVariable("SAMPLE_FONT");
if (font is null)
    foreach (var f in new[] { @"C:\Windows\Fonts\arial.ttf", "/System/Library/Fonts/Supplemental/Arial.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf" })
        if (File.Exists(f)) { font = f; break; }
string Text(string t, int size, string color)
{
    if (font is null) return "";
    string ff = font.Replace("\\", "/").Replace(":", "\\:");
    return $",drawtext=fontfile='{ff}':text='{t}':fontsize={size}:fontcolor={color}:x=(w-text_w)/2:y=(h-text_h)/2";
}
bool hasText = true;
try { Ff("-f", "lavfi", "-i", "color=c=#1F3A5F:s=1280x720", "-frames:v", "1", "-vf", "format=rgb24" + Text("Kuroko sample menu", 48, "white").Replace("y=(h-text_h)/2", "y=120"), W("bg.png")); }
catch { hasText = false; Ff("-f", "lavfi", "-i", "color=c=#1F3A5F:s=1280x720", "-frames:v", "1", W("bg.png")); }
foreach (var (n, label) in new[] { ("b1", "Title 1"), ("b2", "Title 2") })
{
    string t1 = hasText ? Text(label, 36, "#1B1B1B") : "";
    Ff("-f", "lavfi", "-i", "color=c=#E6E8EC:s=360x96", "-frames:v", "1", "-vf", "format=rgba" + t1, W($"{n}_n.png"));
    Ff("-f", "lavfi", "-i", "color=c=#F2A33A:s=360x96", "-frames:v", "1", "-vf", "format=rgba" + t1, W($"{n}_s.png"));
}

// ── 書き出し ──
AuthoringProject NewProject(string name)
{
    var p = new AuthoringProject { Name = name };
    p.Encode.VideoCodec = AuthoringVideoSettings.Av1;
    p.Encode.Crf = 40;          // 小さく（サンプルなので画質より大きさ）
    p.Encode.Preset = 8;
    p.Encode.AudioBitrateKbps = 64;
    return p;
}

AuthoringTitle AddTitle(AuthoringProject p, string file, string name)
{
    var t = new AuthoringTitle { Id = p.NextTitleId(), Name = name, VideoFile = file, Video = p.Encode.CopyVideo() };
    var info = MediaInfo.Probe(tools, file);
    t.ListSourceTracks(info.SourceAudio(), info.SourceSubtitles(), p.Encode);
    p.Titles.Add(t);
    return t;
}

void Export(AuthoringProject p, string file)
{
    var ex = new KrkExporter(p, tools);
    string path = Path.Combine(outDir, file);
    ex.Export(path);
    Console.WriteLine($"{file}: {new FileInfo(path).Length:N0} bytes");
}

// none-basic：1 タイトル・章 2 つ。
{
    var p = NewProject("none-basic");
    var t = AddTitle(p, W("basic.mkv"), "Basic");
    t.Audio[0].Language = "und";
    t.Chapters.Add(new KurokoNoneChapter { StartMs = 0, Name = "Start" });
    t.Chapters.Add(new KurokoNoneChapter { StartMs = 3000, Name = "Middle" });
    Export(p, "none-basic.krk");
}

// none-multi：2 タイトル。1 つ目は音声 2 本・字幕 2 本。
AuthoringProject MultiProject(string name)
{
    var p = NewProject(name);
    var t1 = AddTitle(p, W("multi1.mkv"), "Title 1");
    t1.Audio[0].Name = "日本語"; t1.Audio[0].Language = "jpn";
    t1.Audio[1].Name = "English"; t1.Audio[1].Language = "eng";
    t1.Subtitles.Add(new AuthoringSubtitleTrack { Kind = AuthoringSubtitleTrack.KindSrt, Path = W("multi1.ja.srt"), Language = "jpn", Name = "日本語" });
    t1.Subtitles.Add(new AuthoringSubtitleTrack { Kind = AuthoringSubtitleTrack.KindSrt, Path = W("multi1.en.srt"), Language = "eng", Name = "English" });
    var t2 = AddTitle(p, W("multi2.mkv"), "Title 2");
    t2.Audio[0].Language = "und";
    return p;
}
Export(MultiProject("none-multi"), "none-multi.krk");

// none-interlaced：29.97i をフィールド分離で持つ。
{
    var p = NewProject("none-interlaced");
    var t = AddTitle(p, W("interlaced.ts"), "Interlaced");
    t.Video!.Interlace = AuthoringVideoSettings.InterlaceFields;
    t.Audio[0].Language = "und";
    Export(p, "none-interlaced.krk");
}

// tiny-menu：none-multi の 2 タイトルに、ボタン 2 つのメニュー（kuroko-tiny）。
{
    var p = MultiProject("tiny-menu");
    var menu = new AuthoringMenu
    {
        Menu = new KurokoTinyMenu
        {
            Id = "main",
            Canvas = new KurokoTinySize { Width = 1280, Height = 720 },
            Background = new KurokoTinyBackground { Image = "background.png" },
            DefaultButton = "b1",
            Buttons =
            {
                new KurokoTinyButton { Id = "b1", Rect = new KurokoTinyRect { X = 460, Y = 300, W = 360, H = 96 },
                    Images = new KurokoTinyButtonImages { Normal = "b1_n.png", Selected = "b1_s.png" },
                    Nav = new KurokoTinyButtonNav { Down = "b2" }, Action = KurokoTinyAction.PlayTitleAction(1) },
                new KurokoTinyButton { Id = "b2", Rect = new KurokoTinyRect { X = 460, Y = 430, W = 360, H = 96 },
                    Images = new KurokoTinyButtonImages { Normal = "b2_n.png", Selected = "b2_s.png" },
                    Nav = new KurokoTinyButtonNav { Up = "b1" }, Action = KurokoTinyAction.PlayTitleAction(2) },
            },
        },
    };
    menu.Assets["background.png"] = W("bg.png");
    foreach (var n in new[] { "b1_n", "b1_s", "b2_n", "b2_s" }) menu.Assets[n + ".png"] = W(n + ".png");
    p.Menus.Add(menu);
    p.Nav.FirstPlay = KurokoTinyAction.ShowMenuAction("main");
    p.Nav.Keys = new KurokoTinyKeys { Menu = "main", TitleMenu = "main" };
    foreach (var t in p.Titles) p.Nav.Titles.Add(new KurokoTinyTitle { Title = t.Id, OnEnd = KurokoTinyAction.ShowMenuAction("main") });
    Export(p, "tiny-menu.krk");
}

try { Directory.Delete(work, true); } catch { }
return 0;
