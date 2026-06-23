using System.IO;

namespace VoiceInput.Services;

/// <summary>
/// Minimal append-only file logger at %APPDATA%\VoiceInput\log.txt for diagnosing the
/// dictation lifecycle. Best-effort: never throws into the caller.
/// </summary>
public static class Log
{
    private const long MaxBytes = 5 * 1024 * 1024;   // rotate past 5 MB so the log can't grow unbounded
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
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var rolled = FilePath + ".1";
                    if (File.Exists(rolled)) File.Delete(rolled);
                    File.Move(FilePath, rolled);
                }
                File.AppendAllText(FilePath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }

    public static void Error(string context, Exception ex) =>
        Write($"ERROR {context}: {ex.GetType().Name}: {ex.Message}");
}
