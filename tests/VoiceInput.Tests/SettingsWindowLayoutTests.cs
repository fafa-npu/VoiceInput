using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
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
            () => null,
            () => 0,
            () => { },
            _ => Task.FromResult(new CorrectionLearningReview(string.Empty, [])));
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
                var pttMode = Assert.IsType<ComboBox>(window.FindName("Profile1ModeCombo"));
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
                        content,
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
        var suggestions = new TaskCompletionSource<CorrectionLearningReview>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        AppSettings? vocabularyRequestSettings = null;
        int clearCalls = 0;
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
            () => null,
            () => 3,
            () => clearCalls++,
            requestSettings =>
            {
                vocabularyRequestSettings = requestSettings;
                return suggestions.Task;
            });
        AppSettings? savedSettings = null;
        var settings = new AppSettings
        {
            Engine = SpeechEngineKind.GptTranscribe,
            TranscribeEndpoint = "https://example.test/",
            TranscribeModel = "custom-deployment",
            TranscribeModelKind = TranscribeModelKind.Gpt4oTranscribe,
            FunAsrModelId = FunAsrModelCatalog.Qwen3AsrId,
            RecognitionVocabulary = ["Existing term"],
        };
        var window = new SettingsWindow(settings, saved => savedSettings = saved, manager, actions)
        {
            ShowInTaskbar = false,
        };

        try
        {
            window.Show();
            var engine = Assert.IsType<ComboBox>(window.FindName("EngineList"));
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
            var reviewLearning = Assert.IsType<Button>(window.FindName("ReviewLearningButton"));
            var learningStatus = Assert.IsType<TextBlock>(window.FindName("LearningStatusText"));
            var correctionStatus = Assert.IsType<TextBlock>(window.FindName("CorrectionHistoryStatusText"));
            var learningReview = Assert.IsType<StackPanel>(window.FindName("LearningReviewPanel"));
            var suggestedTerms = Assert.IsType<StackPanel>(window.FindName("VocabularySuggestionsList"));
            var applyLearning = Assert.IsType<Button>(window.FindName("ApplyLearningButton"));
            var useContext = Assert.IsType<CheckBox>(window.FindName("UseContextBox"));
            var learnFromEdits = Assert.IsType<CheckBox>(window.FindName("LearnFromEditsBox"));
            var diagnosticPrivacy = Assert.IsType<TextBlock>(window.FindName("DiagnosticPrivacyText"));
            var diagnosticLogging = Assert.IsType<CheckBox>(window.FindName("DiagnosticLoggingBox"));
            var llmEnabled = Assert.IsType<CheckBox>(window.FindName("LlmEnabledBox"));
            var llmModel = Assert.IsType<TextBox>(window.FindName("LlmModelBox"));
            var overviewLocalModel = Assert.IsType<StackPanel>(window.FindName("OverviewLocalModelPanel"));
            var overviewLocalReadiness = Assert.IsType<StackPanel>(window.FindName("OverviewLocalReadinessPanel"));
            var overviewLlm = Assert.IsType<StackPanel>(window.FindName("OverviewLlmPanel"));
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
            Assert.Contains("Qwen3-ASR", supported.Text, StringComparison.Ordinal);
            Assert.Contains("first 10", supported.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("96", supported.Text, StringComparison.Ordinal);
            Assert.Contains("500", supported.Text, StringComparison.Ordinal);
            Assert.Contains("Windows dictation", unsupported.Text, StringComparison.Ordinal);
            Assert.Contains("GPT-4o Transcribe Diarize", unsupported.Text, StringComparison.Ordinal);
            Assert.Contains("local models", unsupported.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "vocabulary",
                Assert.IsType<string>(diagnosticLogging.Content),
                StringComparison.OrdinalIgnoreCase);
            Assert.False(useContext.IsEnabled);
            Assert.True(learnFromEdits.IsEnabled);
            Assert.True(reviewLearning.IsEnabled);
            Assert.Contains("3 saved corrections", correctionStatus.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("never uploaded", diagnosticPrivacy.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Visibility.Collapsed, overviewLocalModel.Visibility);
            Assert.Equal(Visibility.Collapsed, overviewLocalReadiness.Visibility);
            Assert.Equal(0, Grid.GetColumn(overviewLlm));
            Assert.Equal(3, Grid.GetColumnSpan(overviewLlm));

            llmEnabled.IsChecked = true;
            llmEnabled.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Assert.True(useContext.IsEnabled);
            Assert.True(learnFromEdits.IsEnabled);
            llmEnabled.IsChecked = false;
            llmEnabled.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));

            reviewLearning.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(reviewLearning.IsEnabled);
            vocabulary.Text = "Existing term, Added while waiting";
            string[] learnedTerms = Enumerable.Range(1, 24).Select(index => $"Learned term {index}").ToArray();
            suggestions.SetResult(new CorrectionLearningReview(
                "- Prefer gujiguji for this recurring correction.",
                ["Existing term", .. learnedTerms]));
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Assert.False(Assert.IsType<AppSettings>(vocabularyRequestSettings).LlmEnabled);
            Assert.Equal(Visibility.Visible, learningReview.Visibility);
            Assert.Equal(24, suggestedTerms.Children.Count);
            Assert.Equal(
                ["Existing term", "Added while waiting"],
                RecognitionVocabulary.Parse(vocabulary.Text).Entries);
            Assert.All(suggestedTerms.Children.OfType<CheckBox>(), item => Assert.True(item.IsChecked));
            Assert.IsType<CheckBox>(suggestedTerms.Children[0]).IsChecked = false;
            applyLearning.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(
                ["Existing term", "Added while waiting", .. learnedTerms.Skip(1)],
                RecognitionVocabulary.Parse(vocabulary.Text).Entries);
            Assert.Contains("staged", learningStatus.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Null(savedSettings);
            Assert.True(reviewLearning.IsEnabled);
            Assert.Equal(0, clearCalls);

            vocabulary.Text = "Existing term";
            llmModel.Text = string.Empty;
            Assert.False(useContext.IsEnabled);
            Assert.True(learnFromEdits.IsEnabled);
            Assert.True(reviewLearning.IsEnabled);
            llmModel.Text = "gpt-4.1-mini";

            Assert.Equal("Existing term", vocabulary.Text);
            Assert.Equal("1 term", count.Text);
            Assert.Contains("transcription prompt", mode.Text, StringComparison.OrdinalIgnoreCase);
            Assert.True(save.IsEnabled);
            vocabularyNav.IsChecked = true;
            Assert.Equal(Visibility.Visible, page.Visibility);

            modelKind.SelectedIndex = 1;
            Assert.Equal("custom-deployment", deployment.Text);
            Assert.True(save.IsEnabled);
            modelKind.SelectedIndex = 0;
            Assert.True(save.IsEnabled);
            vocabulary.Text = "Existing term\r\nSecond term";
            Assert.True(save.IsEnabled);
            vocabulary.Text = "Existing term";
            Assert.True(save.IsEnabled);

            engine.SelectedIndex = 1;
            Assert.Contains("Azure Phrase List", mode.Text, StringComparison.Ordinal);
            vocabulary.Text = string.Join(",", Enumerable.Range(1, 501).Select(index => $"Term {index}"));
            Assert.Contains("500 of 501", mode.Text, StringComparison.Ordinal);
            vocabulary.Text = "Existing term";
            engine.SelectedIndex = 0;
            Assert.Contains("not supported", mode.Text, StringComparison.OrdinalIgnoreCase);
            engine.SelectedIndex = 3;
            Assert.Contains("Qwen prompt", mode.Text, StringComparison.OrdinalIgnoreCase);
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
            Assert.Contains("gujiguji", saved.LlmLearnedRules, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, clearCalls);
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
            () => activeModel,
            () => 0,
            () => { },
            _ => Task.FromResult(new CorrectionLearningReview(string.Empty, [])));
        var window = new SettingsWindow(new AppSettings(), _ => { }, manager, actions)
        {
            ShowInTaskbar = false,
        };

        try
        {
            window.Show();
            Assert.IsType<RadioButton>(window.FindName("ModelSelectionNav")).IsChecked = true;
            Assert.IsType<ComboBox>(window.FindName("EngineList")).SelectedIndex = 3;
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            Layout(window, content, 720, 520);
            var modelCards = Assert.IsType<StackPanel>(window.FindName("FunAsrModelsPanel"));
            TextBlock qwenTitle = Descendants<TextBlock>(modelCards)
                .Single(text => text.Text == "Qwen3-ASR 0.6B");
            Border qwenCard = Ancestor<Border>(qwenTitle);
            string qwenMetadata = string.Join(" ", Descendants<TextBlock>(qwenCard).Select(VisibleText));
            Assert.Contains("987 MB", qwenMetadata, StringComparison.Ordinal);
            foreach (string language in new[] { "EN", "ZH", "JA", "KO", "VI" })
                Assert.Contains(language, qwenMetadata, StringComparison.Ordinal);
            Assert.Contains("Recommended", qwenMetadata, StringComparison.Ordinal);
            Assert.DoesNotContain("ZH, ZH", qwenMetadata, StringComparison.Ordinal);
            TextBlock qwen17Title = Descendants<TextBlock>(modelCards)
                .Single(text => text.Text == "Qwen3-ASR 1.7B");
            Border qwen17Card = Ancestor<Border>(qwen17Title);
            string qwen17Metadata = string.Join(
                " ", Descendants<TextBlock>(qwen17Card).Select(VisibleText));
            Assert.Contains("2.4 GB", qwen17Metadata, StringComparison.Ordinal);
            foreach (string language in new[] { "EN", "ZH", "JA", "KO", "VI" })
                Assert.Contains(language, qwen17Metadata, StringComparison.Ordinal);
            Assert.DoesNotContain("Recommended", qwen17Metadata, StringComparison.Ordinal);
            Button download = Descendants<Button>(qwenCard)
                .Single(button => button.Content is string label
                    && label.StartsWith("Download", StringComparison.Ordinal));

            var stopwatch = Stopwatch.StartNew();
            download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            stopwatch.Stop();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250));
            Assert.False(Assert.IsType<Button>(window.FindName("SaveButton")).IsEnabled);
            Assert.False(Assert.IsType<Button>(window.FindName("CancelButton")).IsEnabled);
            Assert.Contains(
                "download",
                Assert.IsType<TextBlock>(window.FindName("StatusText")).Text,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                Descendants<Button>(window),
                button => Equals(button.Content, "Cancel download"));
            Assert.All(
                Descendants<Button>(Assert.IsType<StackPanel>(window.FindName("LocalModelsPanel")))
                    .Where(button => !Equals(button.Content, "Cancel download")),
                button => Assert.False(button.IsEnabled));
            ProgressBar visibleProgress = Descendants<ProgressBar>(window)
                .First(progress => progress.Visibility == Visibility.Visible);
            Assert.Contains(Descendants<TextBlock>(window), text =>
                text.Visibility == Visibility.Visible
                && text.Text.Contains("conv_frontend.onnx")
                && text.Text.Contains("MB"));
            Layout(window, content, 720, 520);
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            var modelSelectionPage = Assert.IsType<ScrollViewer>(window.FindName("ModelSelectionPage"));
            Rect progressBounds = visibleProgress.TransformToAncestor(modelSelectionPage)
                .TransformBounds(new Rect(visibleProgress.RenderSize));
            Assert.True(
                progressBounds.Top >= 0 && progressBounds.Bottom <= modelSelectionPage.ViewportHeight,
                $"Download progress is outside the minimum viewport: {progressBounds}, "
                + $"viewport height {modelSelectionPage.ViewportHeight:F1}px.");
            Assert.All(
                Descendants<Button>(Assert.IsType<StackPanel>(window.FindName("LocalModelsPanel")))
                    .Where(button => button.Visibility == Visibility.Visible),
                button => Assert.InRange(button.ActualHeight, 32, 44));
            Capture(content, Environment.GetEnvironmentVariable("VOICEINPUT_UI_CAPTURE_DIR"),
                "model-selection-download-progress-720x520.png");
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
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
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
            () => null,
            () => 0,
            () => { },
            _ => Task.FromResult(new CorrectionLearningReview(string.Empty, [])));
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
        int installCalls = 0;
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
            _ =>
            {
                installCalls++;
                return Task.CompletedTask;
            },
            _ => { },
            () => null,
            () => 0,
            () => { },
            _ => Task.FromResult(new CorrectionLearningReview(string.Empty, [])));
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

        foreach (string page in new[] { "Overview", "ModelSelection", "Profiles", "Vocabulary", "App" })
        {
            var navigation = Assert.IsType<RadioButton>(window.FindName(page + "Nav"));
            navigation.IsChecked = true;
            Layout(window, content, 900, 650);
            Assert.True(content.ActualWidth >= Math.Min(900, window.ActualWidth) - 40);
            Assert.True(content.ActualHeight >= 580);
            Capture(content, captureDirectory, page.ToLowerInvariant() + "-900x650.png");
        }

        Assert.Null(window.FindName("SpeechNav"));
        Assert.Null(window.FindName("FunAsrNav"));
        Assert.Null(window.FindName("SpeechPage"));
        Assert.Null(window.FindName("FunAsrPage"));

        var modelSelectionNav = Assert.IsType<RadioButton>(window.FindName("ModelSelectionNav"));
        Assert.Equal("Model Selection", modelSelectionNav.Content);
        var engineList = Assert.IsType<ComboBox>(window.FindName("EngineList"));
        Assert.Same(
            Assert.IsType<TextBlock>(window.FindName("EngineListLabel")),
            AutomationProperties.GetLabeledBy(engineList));
        Assert.Equal(
            ["Windows", "Azure", "GptTranscribe", "FunAsr"],
            engineList.Items.Cast<ComboBoxItem>()
                .Select(item => Assert.IsType<string>(item.Tag))
                .ToArray());
        var localModels = Assert.IsType<StackPanel>(window.FindName("LocalModelsPanel"));
        var modelSelectionPage = Assert.IsType<ScrollViewer>(window.FindName("ModelSelectionPage"));
        var azureFields = Assert.IsType<StackPanel>(window.FindName("AzureFieldsPanel"));
        var transcribeFields = Assert.IsType<StackPanel>(window.FindName("TranscribeFieldsPanel"));
        var selectionStatus = Assert.IsType<TextBlock>(window.FindName("ModelSelectionStatusText"));
        var selectionStatusBorder = Assert.IsType<Border>(window.FindName("ModelSelectionStatusBorder"));

        modelSelectionNav.IsChecked = true;
        Assert.Contains("Windows dictation is active", selectionStatus.Text, StringComparison.Ordinal);
        Assert.Equal(
            Color.FromRgb(244, 248, 245),
            Assert.IsType<SolidColorBrush>(selectionStatusBorder.Background).Color);
        Assert.Equal(Visibility.Collapsed, localModels.Visibility);
        engineList.SelectedIndex = 1;
        Assert.Equal(Visibility.Visible, azureFields.Visibility);
        Assert.Equal(Visibility.Collapsed, transcribeFields.Visibility);
        Assert.Contains("Configure Azure Speech", selectionStatus.Text, StringComparison.Ordinal);
        engineList.SelectedIndex = 2;
        Assert.Equal(Visibility.Collapsed, azureFields.Visibility);
        Assert.Equal(Visibility.Visible, transcribeFields.Visibility);
        Assert.Contains("Configure GPT-4o Transcribe", selectionStatus.Text, StringComparison.Ordinal);
        Layout(window, content, 720, 520);
        Assert.Equal(0, modelSelectionPage.ScrollableHeight);
        engineList.SelectedIndex = 3;
        Assert.Equal(Visibility.Visible, localModels.Visibility);
        Assert.Contains("Download Qwen3-ASR 0.6B", selectionStatus.Text, StringComparison.Ordinal);
        var overviewLocalModel = Assert.IsType<StackPanel>(window.FindName("OverviewLocalModelPanel"));
        var overviewLocalReadiness = Assert.IsType<StackPanel>(window.FindName("OverviewLocalReadinessPanel"));
        var overviewLlm = Assert.IsType<StackPanel>(window.FindName("OverviewLlmPanel"));
        Assert.Equal(Visibility.Visible, overviewLocalModel.Visibility);
        Assert.Equal(Visibility.Visible, overviewLocalReadiness.Visibility);
        Assert.Equal(1, Grid.GetColumn(overviewLlm));
        Assert.Equal(1, Grid.GetColumnSpan(overviewLlm));
        Assert.Contains(
            "Download Qwen3-ASR 0.6B to continue",
            Assert.IsType<TextBlock>(window.FindName("StatusText")).Text,
            StringComparison.Ordinal);
        Assert.False(Assert.IsType<Button>(window.FindName("SaveButton")).IsEnabled);
        Assert.Equal(
            Color.FromRgb(255, 250, 240),
            Assert.IsType<SolidColorBrush>(selectionStatusBorder.Background).Color);
        Assert.Equal(5, Assert.IsType<StackPanel>(window.FindName("FunAsrModelsPanel")).Children.Count);
        Assert.DoesNotContain(
            Descendants<TextBlock>(localModels).Where(text => text.Visibility == Visibility.Visible),
            text => text.Text == "Selected");

        Layout(window, content, 720, 520);
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        Rect localModelsBounds = localModels.TransformToAncestor(modelSelectionPage)
            .TransformBounds(new Rect(localModels.RenderSize));
        Assert.True(
            localModelsBounds.Top >= 0 && localModelsBounds.Top < modelSelectionPage.ViewportHeight,
            $"Local model details were not brought into view: {localModelsBounds}, "
            + $"viewport height {modelSelectionPage.ViewportHeight:F1}px.");
        Assert.All(
            Descendants<Button>(localModels).Where(button => button.Visibility == Visibility.Visible),
            button => Assert.InRange(button.ActualHeight, 32, 44));
        Assert.True(content.ActualWidth >= Math.Min(720, window.ActualWidth) - 40);
        Assert.True(content.ActualHeight >= 450);
        Capture(content, captureDirectory, "model-selection-720x520.png");

        engineList.SelectedIndex = 0;
        Assert.IsType<RadioButton>(window.FindName("OverviewNav")).IsChecked = true;
        var chooseModel = Assert.IsType<Button>(window.FindName("ChooseModelButton"));
        Assert.Equal("Choose a model", chooseModel.Content);
        chooseModel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(modelSelectionNav.IsChecked);
        Assert.Equal(0, engineList.SelectedIndex);
        Assert.Equal(0, installCalls);

        Assert.IsType<RadioButton>(window.FindName("VocabularyNav")).IsChecked = true;
        Layout(window, content, 720, 520);
        Capture(content, captureDirectory, "vocabulary-720x520.png");

        Assert.IsType<RadioButton>(window.FindName("ProfilesNav")).IsChecked = true;
        Layout(window, content, 720, 520);
        var profilesPage = Assert.IsType<ScrollViewer>(window.FindName("ProfilesPage"));
        var pttMode = Assert.IsType<ComboBox>(window.FindName("Profile1ModeCombo"));
        Assert.Equal(0, profilesPage.ScrollableHeight);
        Rect pttModeBounds = pttMode.TransformToAncestor(profilesPage).TransformBounds(new Rect(pttMode.RenderSize));
        Assert.True(
            pttModeBounds.Top >= 0 && pttModeBounds.Bottom <= profilesPage.ViewportHeight,
            $"Profile controls are outside the initial viewport: {pttModeBounds}, viewport height {profilesPage.ViewportHeight}.");
        Capture(content, captureDirectory, "profiles-720x520.png");

        Assert.Equal(0, pttMode.SelectedIndex);
        pttMode.SelectedIndex = 1;
        Assert.Equal(
            "Desktop · Right Ctrl · press to start/stop",
            Assert.IsType<TextBlock>(window.FindName("OverviewPttText")).Text);

        var save = Assert.IsType<Button>(window.FindName("SaveButton"));
        Assert.True(save.IsEnabled);
        var profile1Name = Assert.IsType<TextBox>(window.FindName("Profile1NameBox"));
        var profile2Name = Assert.IsType<TextBox>(window.FindName("Profile2NameBox"));
        var profile2Active = Assert.IsType<RadioButton>(window.FindName("Profile2ActiveRadio"));
        var profile2Overlay = Assert.IsType<ComboBox>(window.FindName("Profile2OverlayCombo"));
        Assert.Equal(InputProfile.MaxNameLength, profile1Name.MaxLength);
        Assert.Equal("Desktop", profile1Name.Text);
        Assert.Equal("Mobile", profile2Name.Text);
        Assert.Equal(0, profile2Overlay.SelectedIndex);

        profile2Name.Text = "Desktop";
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(window.IsVisible);
        Assert.Contains(
            "unique",
            Assert.IsType<TextBlock>(window.FindName("StatusText")).Text,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(Assert.IsType<RadioButton>(window.FindName("ProfilesNav")).IsChecked);

        profile2Name.Text = "Phone";
        profile2Active.IsChecked = true;
        Assert.Equal(
            "Phone · Left Ctrl · press to start/stop",
            Assert.IsType<TextBlock>(window.FindName("OverviewPttText")).Text);
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        AppSettings saved = Assert.IsType<AppSettings>(savedSettings);
        Assert.Equal(InputProfile.Profile2Id, saved.ActiveProfileId);
        Assert.Equal("Phone", saved.ActiveProfile.Name);
        Assert.Equal("LeftCtrl", saved.PttKey);
        Assert.Equal(PttMode.Toggle, saved.PttMode);
        Assert.Equal(OverlayPosition.Top, saved.ActiveProfile.OverlayPosition);
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
        DpiScale dpi = VisualTreeHelper.GetDpi(content);
        int width = Math.Max(1, checked((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX)));
        int height = Math.Max(1, checked((int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY)));
        var rendered = new RenderTargetBitmap(
            width,
            height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        rendered.Render(content);

        var bitmap = new RenderTargetBitmap(
            width,
            height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            drawing.DrawRectangle(Brushes.White, null, bounds);
            drawing.DrawImage(rendered, bounds);
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

    private static string VisibleText(TextBlock text) =>
        new TextRange(text.ContentStart, text.ContentEnd).Text;

    private static T Ancestor<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? current = child;
        while ((current = VisualTreeHelper.GetParent(current)) is not null)
        {
            if (current is T match)
                return match;
        }
        throw new InvalidOperationException($"No {typeof(T).Name} ancestor was found.");
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
