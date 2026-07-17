using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VoiceInput.Models;
using VoiceInput.Services;
using VoiceInput.Views;

namespace VoiceInput.Tests;

public sealed class FirstRunWindowLayoutTests
{
    [Fact]
    public void GuideLaysOutAtSupportedSizesAndRequiresRealTextToContinue() => RunOnSta(() =>
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        int completed = 0;
        int settingsOpened = 0;
        var window = CreateWindow(
            _ => { completed++; return true; },
            () => settingsOpened++);
        window.Show();

        try
        {
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            string[] names =
            [
                "PracticePage",
                "CompletionPage",
                "PracticeTextBox",
                "PracticeStatusText",
                "ContinueButton",
                "SkipButton",
                "OpenSettingsLink",
                "TrayMenuMock",
                "CompletionOpenSettingsButton",
                "FinishButton",
                "PracticeAgainButton",
                "PracticeFooter",
                "CompletionFooter",
                "LocalModelSetupPanel",
                "InstallLocalModelButton",
                "CancelLocalModelInstallButton",
                "LocalModelProgressBar",
                "LocalModelProgressText",
                "HoldModeRadio",
                "ToggleModeRadio",
                "LocalModelInlineStatusText",
                "FocusStepNumberText",
                "TalkStepNumberText",
                "ReleaseStepNumberText",
            ];
            string[] missing = names.Where(name => window.FindName(name) is null).ToArray();
            Assert.True(missing.Length == 0, $"Missing named elements: {string.Join(", ", missing)}");

            var practicePage = Assert.IsType<ScrollViewer>(window.FindName("PracticePage"));
            var completionPage = Assert.IsType<ScrollViewer>(window.FindName("CompletionPage"));
            var practiceText = Assert.IsType<TextBox>(window.FindName("PracticeTextBox"));
            var continueButton = Assert.IsType<Button>(window.FindName("ContinueButton"));
            var practiceFooter = Assert.IsType<Grid>(window.FindName("PracticeFooter"));
            var completionFooter = Assert.IsType<Grid>(window.FindName("CompletionFooter"));
            string? captureDirectory = Environment.GetEnvironmentVariable("VOICEINPUT_UI_CAPTURE_DIR");

            Assert.False(continueButton.IsEnabled);
            Assert.Equal(Visibility.Visible, practicePage.Visibility);
            Assert.Equal(Visibility.Collapsed, completionPage.Visibility);
            foreach ((double width, double height) in SupportedSizes())
            {
                Layout(window, content, width, height);
                AssertPageAndFooterDoNotOverlap(content, practicePage, practiceFooter);
                Capture(window, captureDirectory, $"first-run-practice-{width:0}x{height:0}.png");
            }

            Assert.Equal(0, completed);
            Assert.Equal(0, settingsOpened);
            practiceText.Text = "这段文字已输入到当前文本框。";
            PumpDispatcher();
            Assert.True(continueButton.IsEnabled);
            Assert.Contains("可以继续", Assert.IsType<TextBlock>(window.FindName("PracticeStatusText")).Text);

            continueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher();
            Assert.Equal(Visibility.Collapsed, practicePage.Visibility);
            Assert.Equal(Visibility.Visible, completionPage.Visibility);
            Assert.Equal(0, completed);
            Assert.Equal(0, settingsOpened);
            foreach ((double width, double height) in SupportedSizes())
            {
                Layout(window, content, width, height);
                AssertPageAndFooterDoNotOverlap(content, completionPage, completionFooter);
                Capture(window, captureDirectory, $"first-run-complete-{width:0}x{height:0}.png");
            }

            Assert.IsType<Button>(window.FindName("PracticeAgainButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher();
            Assert.Equal(Visibility.Visible, practicePage.Visibility);
            Assert.Equal(string.Empty, practiceText.Text);
            Assert.False(continueButton.IsEnabled);
            Assert.Equal(0, completed);
            Assert.Equal(0, settingsOpened);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void GuideLetsTheUserChooseActivationModeBeforePractice() => RunOnSta(() =>
    {
        var selectedModes = new List<PttMode>();
        var window = CreateWindow(
            pttMode: PttMode.Hold,
            setPttMode: selectedModes.Add);
        window.Show();

        try
        {
            var hold = Assert.IsType<RadioButton>(window.FindName("HoldModeRadio"));
            var toggle = Assert.IsType<RadioButton>(window.FindName("ToggleModeRadio"));
            Assert.True(hold.IsChecked);
            Assert.False(toggle.IsChecked);

            toggle.IsChecked = true;
            PumpDispatcher();

            Assert.Equal([PttMode.Toggle], selectedModes);
            Assert.Contains("按一下", Assert.IsType<TextBlock>(window.FindName("PracticePttStepText")).Text);
            Assert.False(Assert.IsType<Button>(window.FindName("ContinueButton")).IsEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ToggleModeExplainsAndTracksTwoPresses() => RunOnSta(() =>
    {
        var window = CreateWindow(pttMode: PttMode.Toggle);
        window.Show();

        try
        {
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            var practiceText = Assert.IsType<TextBox>(window.FindName("PracticeTextBox"));
            var listeningBars = Assert.IsType<StackPanel>(window.FindName("ListeningBars"));
            var status = Assert.IsType<TextBlock>(window.FindName("PracticeStatusText"));
            var focusNumber = Assert.IsType<TextBlock>(window.FindName("FocusStepNumberText"));
            var talkNumber = Assert.IsType<TextBlock>(window.FindName("TalkStepNumberText"));
            var releaseNumber = Assert.IsType<TextBlock>(window.FindName("ReleaseStepNumberText"));
            string? captureDirectory = Environment.GetEnvironmentVariable("VOICEINPUT_UI_CAPTURE_DIR");
            Assert.Contains("按一下", Assert.IsType<TextBlock>(window.FindName("PracticePttStepText")).Text);
            Assert.Contains("再按一下", Assert.IsType<TextBlock>(window.FindName("PracticeReleaseStepText")).Text);
            Assert.Equal("1", focusNumber.Text);
            Layout(window, content, 720, 560);

            practiceText.Focus();
            PumpDispatcher();
            Assert.Equal("✓", focusNumber.Text);
            Assert.Equal("2", talkNumber.Text);
            RaiseKey(window, Keyboard.PreviewKeyDownEvent, Key.RightCtrl);
            RaiseKey(window, Keyboard.PreviewKeyUpEvent, Key.RightCtrl);
            PumpDispatcher();

            Assert.Equal(Visibility.Visible, listeningBars.Visibility);
            Assert.Contains("正在聆听", status.Text);
            Capture(window, captureDirectory, "first-run-toggle-listening-720x560.png");

            RaiseKey(window, Keyboard.PreviewKeyDownEvent, Key.RightCtrl);
            RaiseKey(window, Keyboard.PreviewKeyUpEvent, Key.RightCtrl);
            PumpDispatcher();

            Assert.Equal(Visibility.Collapsed, listeningBars.Visibility);
            Assert.Contains("正在处理", status.Text);
            Assert.Equal("✓", talkNumber.Text);
            Assert.Equal("3", releaseNumber.Text);

            practiceText.Text = "Toggle mode completed.";
            PumpDispatcher();
            Assert.Equal("✓", releaseNumber.Text);
            Capture(window, captureDirectory, "first-run-toggle-complete-720x560.png");

            practiceText.Clear();
            PumpDispatcher();
            Assert.Equal("2", talkNumber.Text);
            Assert.Equal("3", releaseNumber.Text);
            Assert.False(Assert.IsType<Button>(window.FindName("ContinueButton")).IsEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ToggleModeIgnoresAKeyUpWhenKeyDownWasRejected() => RunOnSta(() =>
    {
        var window = CreateWindow(pttMode: PttMode.Toggle);
        window.Show();

        try
        {
            var listeningBars = Assert.IsType<StackPanel>(window.FindName("ListeningBars"));
            var focusNumber = Assert.IsType<TextBlock>(window.FindName("FocusStepNumberText"));

            RaiseKey(window, Keyboard.PreviewKeyDownEvent, Key.RightCtrl);
            RaiseKey(window, Keyboard.PreviewKeyUpEvent, Key.RightCtrl);
            PumpDispatcher();

            Assert.Equal(Visibility.Collapsed, listeningBars.Visibility);
            Assert.Equal("1", focusNumber.Text);
            Assert.DoesNotContain(
                "正在聆听",
                Assert.IsType<TextBlock>(window.FindName("PracticeStatusText")).Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ToggleModeIgnoresAChordedPttCycle() => RunOnSta(() =>
    {
        var window = CreateWindow(pttMode: PttMode.Toggle);
        window.Show();

        try
        {
            var practiceText = Assert.IsType<TextBox>(window.FindName("PracticeTextBox"));
            var listeningBars = Assert.IsType<StackPanel>(window.FindName("ListeningBars"));
            var talkNumber = Assert.IsType<TextBlock>(window.FindName("TalkStepNumberText"));

            practiceText.Focus();
            PumpDispatcher();
            RaiseKey(window, Keyboard.PreviewKeyDownEvent, Key.RightCtrl);
            RaiseKey(window, Keyboard.PreviewKeyDownEvent, Key.C);
            RaiseKey(window, Keyboard.PreviewKeyUpEvent, Key.RightCtrl);
            PumpDispatcher();

            Assert.Equal(Visibility.Collapsed, listeningBars.Visibility);
            Assert.Equal("2", talkNumber.Text);
            Assert.DoesNotContain(
                "正在聆听",
                Assert.IsType<TextBlock>(window.FindName("PracticeStatusText")).Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ToggleModeIgnoresAPreHeldChord() => RunOnSta(() =>
    {
        var window = CreateWindow(
            pttMode: PttMode.Toggle,
            isPttGestureChorded: () => true);
        window.Show();

        try
        {
            var practiceText = Assert.IsType<TextBox>(window.FindName("PracticeTextBox"));
            var listeningBars = Assert.IsType<StackPanel>(window.FindName("ListeningBars"));

            practiceText.Focus();
            PumpDispatcher();
            RaiseKey(window, Keyboard.PreviewKeyDownEvent, Key.RightCtrl);
            RaiseKey(window, Keyboard.PreviewKeyUpEvent, Key.RightCtrl);
            PumpDispatcher();

            Assert.Equal(Visibility.Collapsed, listeningBars.Visibility);
            Assert.DoesNotContain(
                "正在聆听",
                Assert.IsType<TextBlock>(window.FindName("PracticeStatusText")).Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void GuideInstallsTheDefaultLocalModelBeforePractice() => RunOnSta(() =>
    {
        int installs = 0;
        var installed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = CreateWindow(
            localModelInstalled: false,
            pttMode: PttMode.Toggle,
            installLocalModel: report =>
            {
                installs++;
                report(new(
                    FunAsrModelCatalog.DefaultId,
                    FunAsrInstallStage.Downloading,
                    "sensevoice-small-q8.gguf",
                    127_000_000,
                    254_000_000));
                return installed.Task;
            });
        window.Show();

        try
        {
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            var practiceText = Assert.IsType<TextBox>(window.FindName("PracticeTextBox"));
            var continueButton = Assert.IsType<Button>(window.FindName("ContinueButton"));
            var installButton = Assert.IsType<Button>(window.FindName("InstallLocalModelButton"));
            var setupPanel = Assert.IsType<Border>(window.FindName("LocalModelSetupPanel"));
            string? captureDirectory = Environment.GetEnvironmentVariable("VOICEINPUT_UI_CAPTURE_DIR");

            Assert.False(practiceText.IsEnabled);
            Assert.False(continueButton.IsEnabled);
            Assert.Equal(Visibility.Visible, setupPanel.Visibility);
            Assert.Contains("SenseVoiceSmall", Assert.IsType<TextBlock>(window.FindName("LocalModelStatusText")).Text);
            Layout(window, content, 720, 560);
            Capture(window, captureDirectory, "first-run-local-download-720x560.png");

            installButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher();

            Assert.Equal(1, installs);
            var progress = Assert.IsType<ProgressBar>(window.FindName("LocalModelProgressBar"));
            Assert.Equal(Visibility.Visible, progress.Visibility);
            Assert.Equal(50, progress.Value);
            Assert.Contains("127.0 MB / 254.0 MB", Assert.IsType<TextBlock>(window.FindName("LocalModelProgressText")).Text);
            Capture(window, captureDirectory, "first-run-local-progress-720x560.png");

            installed.SetResult();
            PumpDispatcherUntil(() => practiceText.IsEnabled);

            Assert.True(practiceText.IsEnabled);
            Assert.Contains("已就绪", Assert.IsType<TextBlock>(window.FindName("LocalModelTitleText")).Text);
            Assert.Equal(Visibility.Collapsed, installButton.Visibility);
            Assert.Equal(Visibility.Collapsed, setupPanel.Visibility);
            Assert.Contains("已就绪", Assert.IsType<TextBlock>(window.FindName("LocalModelInlineStatusText")).Text);
            Assert.Contains("按一下", Assert.IsType<TextBlock>(window.FindName("PracticeStatusDetailText")).Text);
            Capture(window, captureDirectory, "first-run-local-ready-720x560.png");

            practiceText.Text = "本地模型已经可以识别。";
            PumpDispatcher();
            Assert.True(continueButton.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void GuidePreservesAndTestsRecognitionChosenInFullSettings() => RunOnSta(() =>
    {
        FirstRunCompletionChoice? completedWith = null;
        var window = CreateWindow(
            complete: choice => { completedWith = choice; return true; },
            useConfiguredRecognition: true,
            recognitionSummary: "GPT-4o Transcribe 已配置");
        window.Show();

        try
        {
            var practiceText = Assert.IsType<TextBox>(window.FindName("PracticeTextBox"));
            Assert.True(practiceText.IsEnabled);
            Assert.Equal(
                Visibility.Collapsed,
                Assert.IsType<Border>(window.FindName("LocalModelSetupPanel")).Visibility);
            Assert.Contains(
                "GPT-4o Transcribe",
                Assert.IsType<TextBlock>(window.FindName("LocalModelInlineStatusText")).Text);

            Assert.IsType<Button>(window.FindName("SkipButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(FirstRunCompletionChoice.Configured, completedWith);
            Assert.False(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void GuideCompletesOnlyForExplicitExitActions() => RunOnSta(() =>
    {
        var calls = new List<string>();
        var closeOnly = CreateWindow(
            choice => { calls.Add(choice.ToString()); return true; },
            () => calls.Add("settings"));
        closeOnly.Show();
        closeOnly.Close();
        Assert.Empty(calls);

        var skip = CreateWindow(
            choice => { calls.Add(choice.ToString()); return true; },
            () => calls.Add("settings"));
        skip.Show();
        Assert.IsType<Button>(skip.FindName("SkipButton"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["DefaultLocal"], calls);
        Assert.False(skip.IsVisible);

        calls.Clear();
        var finish = CreateWindow(
            choice => { calls.Add(choice.ToString()); return true; },
            () => calls.Add("settings"));
        finish.Show();
        Assert.IsType<TextBox>(finish.FindName("PracticeTextBox")).Text = "Done";
        Assert.IsType<Button>(finish.FindName("ContinueButton"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsType<Button>(finish.FindName("FinishButton"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["DefaultLocal"], calls);
        Assert.False(finish.IsVisible);

        calls.Clear();
        FirstRunWindow? settings = null;
        settings = CreateWindow(
            choice => { calls.Add(choice.ToString()); return true; },
            () =>
            {
                Assert.True(settings!.IsVisible);
                calls.Add("settings");
            });
        settings.Show();
        Assert.IsType<Hyperlink>(settings.FindName("OpenSettingsLink"))
            .RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Assert.Equal(["settings"], calls);
        settings.Close();

        calls.Clear();
        int warnings = 0;
        var warningRejected = CreateWindow(
            choice => { calls.Add(choice.ToString()); return true; },
            () => calls.Add("settings"),
            localModelInstalled: false,
            confirmWindowsFallback: () => { warnings++; return false; });
        warningRejected.Show();
        var warningSkip = Assert.IsType<Button>(warningRejected.FindName("SkipButton"));
        Assert.Contains("准确率较低", warningSkip.Content?.ToString());
        warningSkip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, warnings);
        Assert.Empty(calls);
        Assert.True(warningRejected.IsVisible);
        warningRejected.Close();

        calls.Clear();
        var useWindows = CreateWindow(
            choice => { calls.Add(choice.ToString()); return true; },
            () => calls.Add("settings"),
            localModelInstalled: false);
        useWindows.Show();
        Assert.IsType<Button>(useWindows.FindName("SkipButton"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["WindowsFallback"], calls);
        Assert.False(useWindows.IsVisible);

        calls.Clear();
        var rejected = CreateWindow(
            choice => { calls.Add(choice.ToString()); return false; },
            () => calls.Add("settings"));
        rejected.Show();
        Assert.IsType<Button>(rejected.FindName("SkipButton"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["DefaultLocal"], calls);
        Assert.True(rejected.IsVisible);
        rejected.Close();
    });

    private static FirstRunWindow CreateWindow(
        Func<FirstRunCompletionChoice, bool>? complete = null,
        Action? openSettings = null,
        bool localModelInstalled = true,
        Func<Action<FunAsrInstallProgress>, Task>? installLocalModel = null,
        Func<bool>? confirmWindowsFallback = null,
        Func<bool>? isPttGestureChorded = null,
        PttMode pttMode = PttMode.Hold,
        Action<PttMode>? setPttMode = null,
        bool useConfiguredRecognition = false,
        string recognitionSummary = "FunASR 本地 · SenseVoiceSmall") => new(
        "RightCtrl",
        "Right Ctrl",
        pttMode,
        new FirstRunWindowActions(
            localModelInstalled,
            useConfiguredRecognition,
            recognitionSummary,
            installLocalModel ?? (_ => Task.CompletedTask),
            () => { },
            confirmWindowsFallback ?? (() => true),
            isPttGestureChorded ?? (() => false),
            setPttMode ?? (_ => { }),
            complete ?? (_ => true),
            openSettings ?? (() => { })))
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -10_000,
        Top = -10_000,
        ShowInTaskbar = false,
    };

    private static (double Width, double Height)[] SupportedSizes() =>
    [
        (900, 650),
        (720, 560),
    ];

    private static void RunOnSta(Action verify)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureWindowsDirectoryEnvironment();
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
        PumpDispatcher();
        double availableWidth = Math.Min(width, window.ActualWidth);
        double availableHeight = Math.Min(height, window.ActualHeight);
        Assert.True(content.ActualWidth >= availableWidth - 40, $"Content width was {content.ActualWidth} at {width}x{height}.");
        Assert.True(content.ActualHeight >= availableHeight - 70, $"Content height was {content.ActualHeight} at {width}x{height}.");
    }

    private static void AssertPageAndFooterDoNotOverlap(
        FrameworkElement content,
        FrameworkElement page,
        FrameworkElement footer)
    {
        Rect pageBounds = page.TransformToAncestor(content).TransformBounds(new Rect(page.RenderSize));
        Rect footerBounds = footer.TransformToAncestor(content).TransformBounds(new Rect(footer.RenderSize));
        Assert.True(pageBounds.Bottom <= footerBounds.Top + 0.5,
            $"Page ends at {pageBounds.Bottom}, but footer starts at {footerBounds.Top}.");
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));

    private static void PumpDispatcherUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
            Dispatcher.CurrentDispatcher.Invoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => { }));
        }
    }

    private static void RaiseKey(Window window, RoutedEvent routedEvent, Key key)
    {
        var source = PresentationSource.FromVisual(window)
            ?? throw new InvalidOperationException("Window has no presentation source.");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = routedEvent,
        };
        window.RaiseEvent(args);
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
}
