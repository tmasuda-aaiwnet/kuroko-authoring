using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace Kuroko;

// 既存の .krk（ZIP）の中の**小さな JSON だけを差し替えて詰め直す**共通処理。
// 音声定義の復旧（KurokoAudioRecovery）とファーストプレイ設定（KurokoFirstPlayEditor）で共用する。
//
// ZipArchive の **Update モードは全エントリをメモリに載せる**（巨大な av.mkv で OOM）ため、Read→Create で
// 1エントリずつコピーする。Stored（av.mkv 等）は NoCompression でコピー＝**再圧縮しない**ので ffmpeg も不要。
// 差し替えるのは JSON だけでもコンテナ全体を書き直すことになる（ZIP の中央ディレクトリの都合）。
// 元ファイルは `.bak` へ退避してから置き換えるので、失敗しても元は残る。
public static class KurokoRepacker
{
    // newBytes：エントリ名（"CONTROL/disc.json" 形式。大小・区切りは吸収）→ 新しい中身。
    // progress：0..1（コピー済みバイト÷全体）。null なら報告しない。
    // 戻り値：退避した .bak のパス。
    public static string ReplaceEntries(
        string krkPath,
        IReadOnlyDictionary<string, byte[]> newBytes,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        string tempSuffix = ".repack.tmp")
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in newBytes) map[Norm(kv.Key)] = kv.Value;

        string tmp = krkPath + tempSuffix;
        try
        {
            using (var srcFs = new FileStream(krkPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var srcZip = new ZipArchive(srcFs, ZipArchiveMode.Read))
            using (var dstFs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var dstZip = new ZipArchive(dstFs, ZipArchiveMode.Create))
            {
                long total = 0;
                foreach (var e in srcZip.Entries) total += e.Length;
                long done = 0;

                foreach (var e in srcZip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (map.TryGetValue(Norm(e.FullName), out var data))
                    {
                        var ne = dstZip.CreateEntry(e.FullName, CompressionLevel.Optimal);
                        using var os = ne.Open();
                        os.Write(data, 0, data.Length);
                    }
                    else
                    {
                        var ne = dstZip.CreateEntry(e.FullName,
                            ShouldStore(e.FullName) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                        using var es = e.Open();
                        using var os = ne.Open();
                        es.CopyTo(os, 1 << 20);
                    }
                    done += e.Length;
                    if (total > 0) progress?.Report(Math.Clamp(done / (double)total, 0, 1));
                }
            }
            string bak = krkPath + ".bak";
            ReplaceWithRetry(tmp, krkPath, bak);
            return bak;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    private static string Norm(string p) => p.Replace('\\', '/').TrimStart('/');

    // KurokoZipWriter.ShouldStore と同一：メディア/画像/マニフェストは無圧縮（Stored）。
    private static bool ShouldStore(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.EndsWith("kuroko.manifest")) return true;
        foreach (var ext in new[] { ".mkv", ".mka", ".mp4", ".m4a", ".png", ".jpg", ".jpeg", ".spu", ".webp" })
            if (n.EndsWith(ext)) return true;
        return false;
    }

    // 元ファイルを .bak へ退避しつつ tmp で置き換える。一時的なロック（AV/インデクサ）はリトライで吸収し、
    // 恒久的な使用中（7-Zip 等で開いている・再生器で再生中）は分かりやすいメッセージで失敗させる。
    private static void ReplaceWithRetry(string tmp, string dst, string bak)
    {
        IOException? last = null;
        for (int i = 0; i < 12; i++)
        {
            try
            {
                if (File.Exists(bak)) File.Delete(bak);
                File.Replace(tmp, dst, bak);
                return;
            }
            catch (IOException ex) { last = ex; Thread.Sleep(400); }
        }
        throw new IOException(
            $"krk を書き戻せませんでした（{Path.GetFileName(dst)} が他プロセスで使用中）。" +
            "7-Zip 等の書庫ソフトや、再生器での再生を閉じてから再試行してください。", last);
    }
}
