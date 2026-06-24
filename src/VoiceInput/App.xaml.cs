using System.Threading;
using System.Windows;

namespace VoiceInput;

public partial class App : Application
{
    private AppController? _controller;
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
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
            MessageBox.Show(args.Exception.Message, "VoiceInput error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _controller = new AppController();
        _controller.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
