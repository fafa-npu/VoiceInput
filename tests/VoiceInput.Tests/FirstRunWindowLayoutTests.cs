using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        var window = CreateWindow(() => completed++, () => settingsOpened++);
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
    public void GuideCompletesOnlyForExplicitExitActions() => RunOnSta(() =>
    {
        var calls = new List<string>();
        var closeOnly = CreateWindow(() => calls.Add("complete"), () => calls.Add("settings"));
        closeOnly.Show();
        closeOnly.Close();
        Assert.Empty(calls);

        var skip = CreateWindow(() => calls.Add("complete"), () => calls.Add("settings"));
        skip.Show();
        Assert.IsType<Button>(skip.FindName("SkipButton"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["complete"], calls);
        Assert.False(skip.IsVisible);

        calls.Clear();
        var finish = CreateWindow(() => calls.Add("complete"), () => calls.Add("settings"));
        finish.Show();
        Assert.IsType<TextBox>(finish.FindName("PracticeTextBox")).Text = "Done";
        Assert.IsType<Button>(finish.FindName("ContinueButton"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsType<Button>(finish.FindName("FinishButton"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["complete"], calls);
        Assert.False(finish.IsVisible);

        calls.Clear();
        FirstRunWindow? settings = null;
        settings = CreateWindow(
            () => calls.Add("complete"),
            () =>
            {
                Assert.False(settings!.IsVisible);
                calls.Add("settings");
            });
        settings.Show();
        Assert.IsType<Hyperlink>(settings.FindName("OpenSettingsLink"))
            .RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Assert.Equal(["complete", "settings"], calls);
    });

    private static FirstRunWindow CreateWindow(Action complete, Action openSettings) => new(
        "RightCtrl",
        "Right Ctrl",
        "Windows 听写 · 简体中文 · Right Ctrl · 模型按需下载",
        complete,
        openSettings)
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
