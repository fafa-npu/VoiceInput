using System.Threading;
using System.Windows;
using VoiceInput.Services;

namespace VoiceInput;

public partial class App : Application
{
    private AppController? _controller;
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        EnsureWindowsDirectoryEnvironment();
        base.OnStartup(e);

        // Single instance per user session: if one is already running, exit silently before
        // installing the keyboard hook / tray icon (a second instance would fight the first).
        _instanceMutex = new Mutex(initiallyOwned: true, "VoiceInput_SingleInstance_8b1c2d", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"ERROR Unhandled dispatcher exception: {args.Exception}");
            MessageBox.Show(args.Exception.Message, "gujiguji error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _controller = new AppController();
        _controller.Start();
    }

    private static void EnsureWindowsDirectoryEnvironment()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir"))) return;

        string? windowsDirectory = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(windowsDirectory))
            windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
            Environment.SetEnvironmentVariable("windir", windowsDirectory, EnvironmentVariableTarget.Process);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
