using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Kuroko;

// Kuroko（.krk）を **ZIP** で書き出すライタ。ZIP は Windows/macOS 標準で中身を閲覧/編集でき、既存ツールも豊富。
//   ・大きな `av.mkv` 等は **Stored（無圧縮）** で入れる＝再圧縮なし＋ファイル内オフセットへ**シーク再生**できる
//     （USDZ と同じ発想。再生器は Stored エントリのデータ位置に境界付きストリームを被せる）。
//   ・PNG/SPU など既に圧縮済み/バイナリも Stored（Deflate しても縮まない）。JSON 等のテキストは Deflate 圧縮。
//   ・**先頭エントリは `KUROKO.MANIFEST`（Stored）**＝ODF/EPUB と同様、展開せず先頭を見るだけで形式判定できる。
public sealed class KurokoZipWriter
{
    private readonly List<Entry> _entries = new();
    private sealed class Entry { public string Name = ""; public byte[]? Bytes; public string? File; public bool Store; }

    public void AddBytes(string path, byte[] data)
        => _entries.Add(new Entry { Name = Norm(path), Bytes = data, Store = ShouldStore(path) });

    public void AddFile(string path, string sourcePath)
        => _entries.Add(new Entry { Name = Norm(path), File = sourcePath, Store = ShouldStore(path) });

    public void Write(string outPath)
    {
        // 先頭は KUROKO.MANIFEST（Stored）にする（形式判定用）。残りは登録順。
        _entries.Sort((a, b) =>
        {
            bool am = IsManifest(a.Name), bm = IsManifest(b.Name);
            return am == bm ? 0 : am ? -1 : 1;
        });
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        // ZipArchive は追加順に書き、seekable ストリームでは Stored のサイズをヘッダへ直接書く（データ記述子を使わない）
        // ＝リーダがローカルヘッダからデータ位置を求めやすい。
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var e in _entries)
        {
            var ze = zip.CreateEntry(e.Name, e.Store ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            using var es = ze.Open();
            if (e.Bytes is not null) es.Write(e.Bytes, 0, e.Bytes.Length);
            else if (e.File is not null) { using var src = File.OpenRead(e.File); src.CopyTo(es); }
        }
    }

    // krk のパス（"/CONTROL/disc.json"）を ZIP エントリ名（"CONTROL/disc.json"）へ。
    private static string Norm(string path) => path.Replace('\\', '/').TrimStart('/');

    private static bool IsManifest(string name)
        => string.Equals(name, "KUROKO.MANIFEST", System.StringComparison.OrdinalIgnoreCase);

    // 無圧縮(Stored)にすべきか：シーク再生する av 系、または既に圧縮済み/バイナリ（Deflate 無益）。
    private static bool ShouldStore(string path)
    {
        if (IsManifest(Norm(path))) return true;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mkv" or ".mka" or ".mp4" or ".m4a" or ".png" or ".jpg" or ".jpeg" or ".spu" or ".webp";
    }
}
