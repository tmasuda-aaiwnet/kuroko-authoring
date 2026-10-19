namespace Kuroko;

// krk のファイルを開く口。既定は FileStream。再生器がネットワーク上のファイル（SMB 等）を独自の方法で開くときは
// OpenReadOverride を差し込む（null を返したら既定の FileStream で開く）。
public static class KurokoIo
{
    public static Func<string, Stream?>? OpenReadOverride { get; set; }

    public static Stream OpenRead(string path, int bufferSize = 4096)
        => OpenReadOverride?.Invoke(path)
           ?? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
}
