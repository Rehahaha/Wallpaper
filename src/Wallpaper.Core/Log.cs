namespace Wallpaper.Core;

public static class Log
{
    private static readonly object Gate = new();

    public static string LogDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Wallpaper", "logs");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message;
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);
                // 带 BOM 的 UTF-8，Windows 自带记事本/PowerShell 才能正确显示中文
                File.AppendAllText(Path.Combine(LogDir, "wallpaper.log"), line + Environment.NewLine,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
