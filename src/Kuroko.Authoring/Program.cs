using Avalonia;

namespace Kuroko.Authoring;

internal static class Program
{
    // Avalonia の初期化（AppMain）より前に、Avalonia や SynchronizationContext に頼る処理を置かないこと。
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // 画面の設計ツール（プレビュー）からも使われる。消さないこと。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
