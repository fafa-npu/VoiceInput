using System.IO;

namespace VoiceInput.Services;

/// <summary>
/// Minimal append-only file logger at %APPDATA%\VoiceInput\log.txt for diagnosing the
/// dictation lifecycle. Best-effort: never throws into the caller.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceInput");
    public static readonly string FilePath = Path.Combine(Dir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }

    public static void Error(string context, Exception ex) =>
        Write($"ERROR {context}: {ex.GetType().Name}: {ex.Message}");
}
