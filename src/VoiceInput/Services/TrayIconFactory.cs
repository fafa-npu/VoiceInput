using System.Drawing;
using System.IO;

namespace VoiceInput.Services;

/// <summary>Loads the multi-size gujiguji voice-cursor icon embedded in the application.</summary>
internal static class TrayIconFactory
{
    public static Icon CreateVoiceCursorIcon(int size = 32)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));

        using Stream stream = typeof(TrayIconFactory).Assembly.GetManifestResourceStream("gujiguji.ico")
            ?? throw new InvalidOperationException("Embedded gujiguji icon was not found.");
        using var source = new Icon(stream, size, size);
        return (Icon)source.Clone();
    }
}
