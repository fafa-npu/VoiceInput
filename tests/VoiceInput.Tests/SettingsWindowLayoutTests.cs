using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
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
        VerifyRecognitionVocabulary();
        VerifyLargeUnsupportedVocabularyPersists();
        VerifyUpdateResults();
        VerifyModelDownloadProgress();
    });

    private static void VerifyLargeUnsupportedVocabularyPersists()
    {
        EnsureWindowsDirectoryEnvironment();
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
        string[] terms = Enumerable.Range(1, 250)
            .Select(index => $"Term {index}")
            .ToArray();

        Verify(SpeechEngineKind.Windows, TranscribeModelKind.Gpt4oTranscribe, "Windows dictation");
        Verify(SpeechEngineKind.GptTranscribe, TranscribeModelKind.Unknown, "Other / unknown");

        void Verify(SpeechEngineKind engine, TranscribeModelKind modelKind, string engineName)
        {
            AppSettings? savedSettings = null;
            var settings = new AppSettings
            {
                Engine = engine,
                TranscribeEndpoint = "https://example.test/",
                TranscribeModel = "custom-deployment",
                TranscribeApiKey = "key",
                TranscribeModelKind = modelKind,
                RecognitionVocabulary = terms,
            };
            var window = new SettingsWindow(settings, saved => savedSettings = saved, manager, actions)
            {
                ShowInTaskbar = false,
            };

            try
            {
                window.Show();
                Assert.IsType<RadioButton>(window.FindName("VocabularyNav")).IsChecked = true;
                var page = Assert.IsType<ScrollViewer>(window.FindName("VocabularyPage"));
                var vocabulary = Assert.IsType<TextBox>(window.FindName("VocabularyBox"));
                var count = Assert.IsType<TextBlock>(window.FindName("VocabularyCountText"));
                var currentEngine = Assert.IsType<TextBlock>(window.FindName("VocabularyCurrentEngineText"));
                var mode = Assert.IsType<TextBlock>(window.FindName("VocabularyModeText"));
                var pttMode = Assert.IsType<ComboBox>(window.FindName("PttModeCombo"));
                var save = Assert.IsType<Button>(window.FindName("SaveButton"));

                Assert.Equal(Visibility.Visible, page.Visibility);
                Assert.Equal("250 terms", count.Text);
                Assert.Equal(engineName, currentEngine.Text);
                Assert.Contains("not supported", mode.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Azure Speech", mode.Text, StringComparison.Ordinal);
                Assert.Contains("GPT-4o Transcribe", mode.Text, StringComparison.Ordinal);
                Assert.Contains("Mini", mode.Text, StringComparison.Ordinal);

                if (engine == SpeechEngineKind.Windows)
                {
                    var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                    Layout(window, content, 720, 520);
                    Assert.True(
                        currentEngine.ActualWidth >= 130,
                        $"Current engine label is clipped at minimum width: {currentEngine.ActualWidth:F1}px.");
                    Assert.True(
                        mode.ActualHeight < 38,
                        $"Vocabulary support warning wraps beyond two lines: {mode.ActualHeight:F1}px.");
                    Rect countBounds = count.TransformToAncestor(page)
                        .TransformBounds(new Rect(count.RenderSize));
                    Assert.True(
                        countBounds.Top >= 0 && countBounds.Bottom <= page.ViewportHeight,
                        $"Vocabulary count is outside the minimum viewport: {countBounds}, "
                        + $"viewport height {page.ViewportHeight:F1}px.");
                    Capture(
                        window,
                        Environment.GetEnvironmentVariable("VOICEINPUT_UI_CAPTURE_DIR"),
                        "vocabulary-populated-720x520.png");
                }

                pttMode.SelectedIndex = 1;
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                AppSettings saved = Assert.IsType<AppSettings>(savedSettings);
                Assert.Equal(PttMode.Toggle, saved.PttMode);
                Assert.Equal(terms, saved.RecognitionVocabulary);
            }
            finally
            {
                if (window.IsVisible)
                    window.Close();
            }
        }
    }

    private static void VerifyRecognitionVocabulary()
    {
        EnsureWindowsDirectoryEnvironment();
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
        var settings = new AppSettings
        {
            Engine = SpeechEngineKind.GptTranscribe,
            TranscribeEndpoint = "https://example.test/",
            TranscribeModel = "custom-deployment",
            TranscribeModelKind = TranscribeModelKind.Gpt4oTranscribe,
            RecognitionVocabulary = ["Existing term"],
        };
        var window = new SettingsWindow(settings, saved => savedSettings = saved, manager, actions)
        {
            ShowInTaskbar = false,
        };

        try
        {
            window.Show();
            var engine = Assert.IsType<ComboBox>(window.FindName("EngineCombo"));
            var modelKind = Assert.IsType<ComboBox>(window.FindName("TranscribeModelKindCombo"));
            var deployment = Assert.IsType<TextBox>(window.FindName("TranscribeModelBox"));
            var vocabularyNav = Assert.IsType<RadioButton>(window.FindName("VocabularyNav"));
            var page = Assert.IsType<ScrollViewer>(window.FindName("VocabularyPage"));
            var vocabularyLabel = Assert.IsType<TextBlock>(window.FindName("VocabularyTermsLabel"));
            var vocabulary = Assert.IsType<TextBox>(window.FindName("VocabularyBox"));
            var count = Assert.IsType<TextBlock>(window.FindName("VocabularyCountText"));
            var mode = Assert.IsType<TextBlock>(window.FindName("VocabularyModeText"));
            var separatorHint = Assert.IsType<TextBlock>(window.FindName("VocabularySeparatorHintText"));
            var supported = Assert.IsType<TextBlock>(window.FindName("VocabularySupportedModelsText"));
            var unsupported = Assert.IsType<TextBlock>(window.FindName("VocabularyUnsupportedModelsText"));
            var diagnosticLogging = Assert.IsType<CheckBox>(window.FindName("DiagnosticLoggingBox"));
            var save = Assert.IsType<Button>(window.FindName("SaveButton"));

            Assert.Equal(
                [
                    "GPT-4o Transcribe",
                    "GPT-4o Mini Transcribe",
                    "GPT-4o Transcribe Diarize (vocabulary unavailable)",
                    "Other / unknown",
                ],
                modelKind.Items.Cast<ComboBoxItem>()
                    .Select(item => Assert.IsType<string>(item.Content))
                    .ToArray());
            Assert.Equal(2, Grid.GetRow(modelKind));
            Assert.Equal(3, Grid.GetRow(deployment));
            Assert.True(vocabulary.AcceptsReturn);
            Assert.Equal(TextWrapping.Wrap, vocabulary.TextWrapping);
            Assert.Equal(ScrollBarVisibility.Auto, vocabulary.VerticalScrollBarVisibility);
            Assert.False(double.IsNaN(vocabulary.Height));
            Assert.Same(vocabularyLabel, AutomationProperties.GetLabeledBy(vocabulary));
            Assert.Contains("comma", separatorHint.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("semicolon", separatorHint.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Azure Speech", supported.Text, StringComparison.Ordinal);
            Assert.Contains("GPT-4o Mini Transcribe", supported.Text, StringComparison.Ordinal);
            Assert.Contains("500", supported.Text, StringComparison.Ordinal);
            Assert.Contains("Windows dictation", unsupported.Text, StringComparison.Ordinal);
            Assert.Contains("GPT-4o Transcribe Diarize", unsupported.Text, StringComparison.Ordinal);
            Assert.Contains("FunASR", unsupported.Text, StringComparison.Ordinal);
            Assert.Contains(
                "vocabulary",
                Assert.IsType<string>(diagnosticLogging.Content),
                StringComparison.OrdinalIgnoreCase);

            Assert.Equal("Existing term", vocabulary.Text);
            Assert.Equal("1 term", count.Text);
            Assert.Contains("transcription prompt", mode.Text, StringComparison.OrdinalIgnoreCase);
            Assert.False(save.IsEnabled);
            vocabularyNav.IsChecked = true;
            Assert.Equal(Visibility.Visible, page.Visibility);

            modelKind.SelectedIndex = 1;
            Assert.Equal("custom-deployment", deployment.Text);
            Assert.True(save.IsEnabled);
            modelKind.SelectedIndex = 0;
            Assert.False(save.IsEnabled);
            vocabulary.Text = "Existing term\r\nSecond term";
            Assert.True(save.IsEnabled);
            vocabulary.Text = "Existing term";
            Assert.False(save.IsEnabled);

            engine.SelectedIndex = 1;
            Assert.Contains("Azure Phrase List", mode.Text, StringComparison.Ordinal);
            vocabulary.Text = string.Join(",", Enumerable.Range(1, 501).Select(index => $"Term {index}"));
            Assert.Contains("500 of 501", mode.Text, StringComparison.Ordinal);
            vocabulary.Text = "Existing term";
            engine.SelectedIndex = 0;
            Assert.Contains("not supported", mode.Text, StringComparison.OrdinalIgnoreCase);
            engine.SelectedIndex = 3;
            Assert.Contains("not supported", mode.Text, StringComparison.OrdinalIgnoreCase);
            engine.SelectedIndex = 2;
            modelKind.SelectedIndex = 0;
            Assert.Contains("transcription prompt", mode.Text, StringComparison.OrdinalIgnoreCase);
            modelKind.SelectedIndex = 1;
            Assert.Contains("transcription prompt", mode.Text, StringComparison.OrdinalIgnoreCase);
            modelKind.SelectedIndex = 2;
            Assert.Contains("not supported", mode.Text, StringComparison.OrdinalIgnoreCase);
            modelKind.SelectedIndex = 3;
            Assert.Contains("not supported", mode.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Existing term", vocabulary.Text);
            modelKind.SelectedIndex = 1;
            Assert.Equal("Existing term", vocabulary.Text);

            vocabulary.Text = " Alpha, alpha；Beta; Gamma，Delta\r\nProduct Name ";
            Assert.Equal("5 terms", count.Text);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            AppSettings saved = Assert.IsType<AppSettings>(savedSettings);
            Assert.Equal(TranscribeModelKind.Gpt4oMiniTranscribe, saved.TranscribeModelKind);
            Assert.Equal("custom-deployment", saved.TranscribeModel);
            Assert.Equal(["Alpha", "Beta", "Gamma", "Delta", "Product Name"], saved.RecognitionVocabulary);
        }
        finally
        {
            if (window.IsVisible)
                window.Close();
        }
    }

    private static void VerifyModelDownloadProgress()
    {
        EnsureWindowsDirectoryEnvironment();
        string root = Path.Combine(Path.GetTempPath(), "VoiceInput.Tests", Guid.NewGuid().ToString("N"));
        using var manager = new FunAsrRuntimeManager(
            root,
            new HttpClient(new OfflineHandler()),
            () => long.MaxValue,
            (_, _) => Task.CompletedTask);
        var pendingInstall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? activeModel = null;
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
            modelId =>
            {
                Thread.Sleep(500);
                activeModel = modelId;
                return pendingInstall.Task;
            },
            _ => pendingInstall.TrySetCanceled(),
            () => activeModel);
        var window = new SettingsWindow(new AppSettings(), _ => { }, manager, actions)
        {
            ShowInTaskbar = false,
        };

        try
        {
            window.Show();
            Assert.IsType<RadioButton>(window.FindName("FunAsrNav")).IsChecked = true;
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            Layout(window, content, 900, 650);
            Button download = Descendants<Button>(window)
                .First(button => Equals(button.Content, "Download"));

            var stopwatch = Stopwatch.StartNew();
            download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            stopwatch.Stop();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250));
            Assert.Contains(Descendants<ProgressBar>(window), progress => progress.Visibility == Visibility.Visible);
            Assert.Contains(Descendants<TextBlock>(window), text =>
                text.Visibility == Visibility.Visible
                && text.Text.Contains(Path.GetFileName(FunAsrModelCatalog.Runtime.RelativePath))
                && text.Text.Contains("MB"));
            Layout(window, content, 720, 520);
            Capture(window, Environment.GetEnvironmentVariable("VOICEINPUT_UI_CAPTURE_DIR"),
                "funasr-download-progress-720x520.png");
            Assert.IsType<RadioButton>(window.FindName("AppNav")).IsChecked = true;
            Assert.Equal(Visibility.Visible, Assert.IsType<ScrollViewer>(window.FindName("AppPage")).Visibility);
        }
        finally
        {
            pendingInstall.TrySetCanceled();
            window.Close();
        }
    }

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

        foreach (string page in new[] { "Overview", "Speech", "Vocabulary", "FunAsr", "Refinement", "App" })
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

        Assert.IsType<RadioButton>(window.FindName("VocabularyNav")).IsChecked = true;
        Layout(window, content, 720, 520);
        Capture(window, captureDirectory, "vocabulary-720x520.png");

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

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (T descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
