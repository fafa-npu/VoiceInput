using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VoiceInput.Models;
using VoiceInput.Services;
using VoiceInput.Views;

namespace VoiceInput.Tests;

public sealed class SettingsWindowLayoutTests
{
    [Fact]
    public void SettingsPagesLayOutAndUpdateResultsDisplay() => RunOnSta(() =>
    {
        VerifyPages();
        VerifyUpdateResults();
    });

    private static void RunOnSta(Action verify)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                verify();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF layout verification timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyUpdateResults()
    {
        EnsureWindowsDirectoryEnvironment();
        string root = Path.Combine(Path.GetTempPath(), "VoiceInput.Tests", Guid.NewGuid().ToString("N"));
        using var manager = new FunAsrRuntimeManager(
            root,
            new HttpClient(new OfflineHandler()),
            () => long.MaxValue,
            (_, _) => Task.CompletedTask);
        var result = new UpdateService.CheckResult(
            UpdateService.CheckOutcome.UpToDate,
            $"v{UpdateService.CurrentVersion}",
            UpdateService.CurrentVersion,
            null);
        string? requestedUpdate = null;
        var actions = new SettingsWindowActions(
            () => false,
            _ => { },
            () => Task.FromResult(result),
            tag => requestedUpdate = tag,
            () => { },
            _ => Task.CompletedTask,
            _ => { },
            () => null);
        var window = new SettingsWindow(new AppSettings(), _ => { }, manager, actions)
        {
            ShowInTaskbar = false,
        };
        window.Show();
        var button = Assert.IsType<Button>(window.FindName("CheckUpdatesButton"));
        var installButton = Assert.IsType<Button>(window.FindName("InstallUpdateButton"));
        var status = Assert.IsType<TextBlock>(window.FindName("StatusText"));

        foreach ((UpdateService.CheckResult checkResult, string expected, bool canInstall) in new[]
        {
            (new UpdateService.CheckResult(
                UpdateService.CheckOutcome.UpdateAvailable,
                "v9.9.9",
                new Version(9, 9, 9),
                "https://api.github.com/update"), "v9.9.9 is available", true),
            (result, "latest version", false),
            (new UpdateService.CheckResult(
                UpdateService.CheckOutcome.CheckFailed,
                null,
                null,
                null), "Update check failed. Please try again.", false),
        })
        {
            result = checkResult;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Assert.Contains(expected, status.Text);
            Assert.True(button.IsEnabled);
            Assert.Equal(canInstall ? Visibility.Visible : Visibility.Collapsed, installButton.Visibility);
            if (canInstall)
            {
                Assert.Equal("Update to v9.9.9", installButton.Content);
                installButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("v9.9.9", requestedUpdate);
            }
        }

        window.Close();
    }

    private static void VerifyPages()
    {
        EnsureWindowsDirectoryEnvironment();
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        string root = Path.Combine(Path.GetTempPath(), "VoiceInput.Tests", Guid.NewGuid().ToString("N"));
        using var manager = new FunAsrRuntimeManager(
            root,
            new HttpClient(new OfflineHandler()),
            () => long.MaxValue,
            (_, _) => Task.CompletedTask);
        var actions = new SettingsWindowActions(
            () => false,
            _ => { },
            () => Task.FromResult(new UpdateService.CheckResult(
                UpdateService.CheckOutcome.UpToDate,
                $"v{UpdateService.CurrentVersion}",
                UpdateService.CurrentVersion,
                null)),
            _ => { },
            () => { },
            _ => Task.CompletedTask,
            _ => { },
            () => null);
        AppSettings? savedSettings = null;
        var window = new SettingsWindow(new AppSettings(), settings => savedSettings = settings, manager, actions)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10_000,
            Top = -10_000,
            ShowInTaskbar = false,
        };
        window.Show();
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        string? captureDirectory = Environment.GetEnvironmentVariable("VOICEINPUT_UI_CAPTURE_DIR");

        foreach (string page in new[] { "Overview", "Speech", "FunAsr", "Refinement", "App" })
        {
            var navigation = Assert.IsType<RadioButton>(window.FindName(page + "Nav"));
            navigation.IsChecked = true;
            Layout(window, content, 900, 650);
            Assert.True(content.ActualWidth >= Math.Min(900, window.ActualWidth) - 40);
            Assert.True(content.ActualHeight >= 580);
            Capture(window, captureDirectory, page.ToLowerInvariant() + "-900x650.png");
        }

        Assert.IsType<RadioButton>(window.FindName("FunAsrNav")).IsChecked = true;
        Layout(window, content, 720, 520);
        Assert.True(content.ActualWidth >= Math.Min(720, window.ActualWidth) - 40);
        Assert.True(content.ActualHeight >= 450);
        Capture(window, captureDirectory, "funasr-720x520.png");

        Assert.IsType<RadioButton>(window.FindName("AppNav")).IsChecked = true;
        Layout(window, content, 720, 520);
        var appPage = Assert.IsType<ScrollViewer>(window.FindName("AppPage"));
        var pttMode = Assert.IsType<ComboBox>(window.FindName("PttModeCombo"));
        Rect pttModeBounds = pttMode.TransformToAncestor(appPage).TransformBounds(new Rect(pttMode.RenderSize));
        Assert.True(
            pttModeBounds.Top >= 0 && pttModeBounds.Bottom <= appPage.ViewportHeight,
            $"Activation mode selector is outside the App viewport: {pttModeBounds}, viewport height {appPage.ViewportHeight}.");
        Capture(window, captureDirectory, "app-720x520.png");

        Assert.Equal(0, pttMode.SelectedIndex);
        pttMode.SelectedIndex = 1;
        Assert.Equal(
            "Right Ctrl · press to start/stop",
            Assert.IsType<TextBlock>(window.FindName("OverviewPttText")).Text);

        var save = Assert.IsType<Button>(window.FindName("SaveButton"));
        Assert.True(save.IsEnabled);
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(PttMode.Toggle, Assert.IsType<AppSettings>(savedSettings).PttMode);
    }

    private static void EnsureWindowsDirectoryEnvironment()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
            return;
        string windowsDirectory = Environment.GetEnvironmentVariable("SystemRoot")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Environment.SetEnvironmentVariable("windir", windowsDirectory, EnvironmentVariableTarget.Process);
    }

    private static void Layout(Window window, FrameworkElement content, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        window.UpdateLayout();
        content.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
    }

    private static void Capture(FrameworkElement content, string? directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;
        Directory.CreateDirectory(directory);
        int width = Math.Max(1, checked((int)Math.Ceiling(content.ActualWidth)));
        int height = Math.Max(1, checked((int)Math.Ceiling(content.ActualHeight)));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, width, height);
            drawing.DrawRectangle(Brushes.White, null, bounds);
            drawing.DrawRectangle(new VisualBrush(content), null, bounds);
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
