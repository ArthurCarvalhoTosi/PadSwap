namespace PadSwap;

/// <summary>Small append-only log in %LOCALAPPDATA%\PadSwap, for diagnosing start-up and device problems.</summary>
internal static class Log
{
    private const long MaxSizeBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PadSwap", "padswap.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxSizeBytes)
                    File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Logging must never take the remapper down.
        }
    }
}
