using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using VoiceInput.Models;
using VoiceInput.Services;

namespace VoiceInput.Views;

internal sealed record SettingsWindowActions(
    Func<bool> IsAutoStartEnabled,
    Action<bool> SetAutoStart,
    Func<Task<UpdateService.CheckResult>> CheckForUpdates,
    Action<string> InstallUpdate,
    Action OpenLog,
    Func<string, Task> InstallFunAsr,
    Action<string> CancelFunAsr,
    Func<string?> ActiveFunAsrModelId);

public partial class SettingsWindow : Window
{
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(107, 107, 107));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(166, 49, 49));
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(47, 118, 88));
    private static readonly Brush AttentionBrush = new SolidColorBrush(Color.FromRgb(140, 87, 9));

    private readonly AppSettings _original;
    private readonly AppSettings _draft;
    private readonly Action<AppSettings> _onSave;
    private readonly FunAsrRuntimeManager _funAsr;
    private readonly SettingsWindowActions _actions;
    private readonly LlmRefiner _refiner = new();
    private readonly Dictionary<string, FunAsrInstallProgress> _modelProgress =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelRow> _modelRows =
        new(StringComparer.OrdinalIgnoreCase);
    private string _selectedFunAsrModelId;
    private string? _availableUpdateTag;
    private bool _loading = true;
    private bool _closed;

    internal SettingsWindow(
        AppSettings current,
        Action<AppSettings> onSave,
        FunAsrRuntimeManager funAsr,
        SettingsWindowActions actions)
    {
        InitializeComponent();
        _original = current.Clone();
        _draft = current.Clone();
        _onSave = onSave;
        _funAsr = funAsr;
        _actions = actions;
        _selectedFunAsrModelId = FunAsrModelCatalog.NormalizeId(_draft.FunAsrModelId);

        VersionText.Text = $"Version {UpdateService.CurrentVersion}";
        EngineCombo.SelectedIndex = EngineIndex(_draft.Engine);
        AzureAuthCombo.SelectedIndex = _draft.AzureAuthMode == AzureAuthMode.EntraId ? 1 : 0;
        TranscribeAuthCombo.SelectedIndex = _draft.TranscribeAuthMode == AzureAuthMode.EntraId ? 1 : 0;
        AzureKeyBox.Password = _draft.AzureKey;
        AzureRegionBox.Text = _draft.AzureRegion;
        AzureEndpointBox.Text = _draft.AzureEndpoint;
        AzureTenantIdBox.Text = _draft.AzureTenantId;
        TranscribeEndpointBox.Text = _draft.TranscribeEndpoint;
        TranscribeModelBox.Text = _draft.TranscribeModel;
        TranscribeApiKeyBox.Password = _draft.TranscribeApiKey;
        TranscribeTenantIdBox.Text = _draft.TranscribeTenantId;
        LlmEnabledBox.IsChecked = _draft.LlmEnabled;
        LlmBaseUrlBox.Text = _draft.LlmBaseUrl;
        LlmApiKeyBox.Password = _draft.LlmApiKey;
        LlmModelBox.Text = _draft.LlmModel;
        LlmPromptBox.Text = _draft.LlmPrompt;
        LanguageCombo.SelectedValue = _draft.Language;
        PttCombo.SelectedValue = _draft.PttKey;
        UseContextBox.IsChecked = _draft.UseContext;
        LearnFromEditsBox.IsChecked = _draft.LearnFromEdits;
        DiagnosticLoggingBox.IsChecked = _draft.DiagnosticLogging;
        StartAtLoginBox.IsChecked = _actions.IsAutoStartEnabled();

        BuildModelRows();
        AttachDraftHandlers();
        _funAsr.ProgressChanged += OnFunAsrProgress;
        Closed += OnClosed;
        _loading = false;

        UpdateFieldVisibility();
        RefreshAll();
    }

    private void AttachDraftHandlers()
    {
        foreach (TextBox box in new[]
        {
            AzureRegionBox,
            AzureEndpointBox,
            AzureTenantIdBox,
            TranscribeEndpointBox,
            TranscribeModelBox,
            TranscribeTenantIdBox,
            LlmBaseUrlBox,
            LlmModelBox,
            LlmPromptBox,
        })
        {
            box.TextChanged += OnDraftValueChanged;
        }

        foreach (PasswordBox box in new[] { AzureKeyBox, TranscribeApiKeyBox, LlmApiKeyBox })
            box.PasswordChanged += OnDraftValueChanged;

        LanguageCombo.SelectionChanged += OnDraftValueChanged;
        PttCombo.SelectionChanged += OnDraftValueChanged;
        LlmEnabledBox.Click += OnDraftValueChanged;
        UseContextBox.Click += OnSensitiveSettingChanged;
        LearnFromEditsBox.Click += OnSensitiveSettingChanged;
        DiagnosticLoggingBox.Click += OnSensitiveSettingChanged;
    }

    private void OnNavigationChanged(object sender, RoutedEventArgs e)
    {
        if (OverviewPage is null || SpeechPage is null || FunAsrPage is null
            || RefinementPage is null || AppPage is null)
        {
            return;
        }

        OverviewPage.Visibility = sender == OverviewNav ? Visibility.Visible : Visibility.Collapsed;
        SpeechPage.Visibility = sender == SpeechNav ? Visibility.Visible : Visibility.Collapsed;
        FunAsrPage.Visibility = sender == FunAsrNav ? Visibility.Visible : Visibility.Collapsed;
        RefinementPage.Visibility = sender == RefinementNav ? Visibility.Visible : Visibility.Collapsed;
        AppPage.Visibility = sender == AppNav ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AzureFieldsPanel is null)
            return;
        UpdateFieldVisibility();
        OnDraftValueChanged(sender, e);
    }

    private void OnAzureAuthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AzureKeyPanel is null)
            return;
        UpdateFieldVisibility();
        OnDraftValueChanged(sender, e);
    }

    private void OnTranscribeAuthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TranscribeApiKeyBox is null)
            return;
        UpdateFieldVisibility();
        OnDraftValueChanged(sender, e);
    }

    private void OnDraftValueChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        Collect();
        UpdateFieldVisibility();
        RefreshAll();
    }

    private void OnSensitiveSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var checkBox = (CheckBox)sender;
        if (checkBox.IsChecked == true)
        {
            string warning = checkBox == UseContextBox
                ? "VoiceInput will read text from the focused app with UI Automation and send it to your configured LLM."
                : checkBox == LearnFromEditsBox
                    ? "After insertion, Enter may capture the same input control. Up to 100 encrypted correction samples are stored locally."
                    : "Full transcripts and LLM output may contain sensitive data and will be written in plaintext to the diagnostic log.";
            if (MessageBox.Show(
                    warning,
                    "Enable this setting?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                _loading = true;
                checkBox.IsChecked = false;
                _loading = false;
            }
        }
        OnDraftValueChanged(sender, e);
    }

    private void UpdateFieldVisibility()
    {
        bool azure = EngineCombo.SelectedIndex == 1;
        bool transcribe = EngineCombo.SelectedIndex == 2;
        bool funAsr = EngineCombo.SelectedIndex == 3;
        bool azureEntra = AzureAuthCombo.SelectedIndex == 1;
        bool transcribeEntra = TranscribeAuthCombo.SelectedIndex == 1;

        AzureFieldsPanel.Visibility = azure ? Visibility.Visible : Visibility.Collapsed;
        AzureKeyPanel.Visibility = azure && !azureEntra ? Visibility.Visible : Visibility.Collapsed;
        AzureEntraPanel.Visibility = azure && azureEntra ? Visibility.Visible : Visibility.Collapsed;
        TranscribeFieldsPanel.Visibility = transcribe ? Visibility.Visible : Visibility.Collapsed;
        TranscribeApiKeyBox.Visibility = transcribe && !transcribeEntra
            ? Visibility.Visible
            : Visibility.Collapsed;
        TranscribeApiKeyLabel.Visibility = TranscribeApiKeyBox.Visibility;
        TranscribeTenantIdBox.Visibility = transcribe && transcribeEntra
            ? Visibility.Visible
            : Visibility.Collapsed;
        TranscribeTenantLabel.Visibility = TranscribeTenantIdBox.Visibility;
        FunAsrSpeechPanel.Visibility = funAsr ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Collect()
    {
        _draft.Engine = EngineCombo.SelectedIndex switch
        {
            1 => SpeechEngineKind.Azure,
            2 => SpeechEngineKind.GptTranscribe,
            3 => SpeechEngineKind.FunAsr,
            _ => SpeechEngineKind.Windows,
        };
        _draft.FunAsrModelId = _selectedFunAsrModelId;
        _draft.AzureAuthMode = AzureAuthCombo.SelectedIndex == 1 ? AzureAuthMode.EntraId : AzureAuthMode.Key;
        _draft.TranscribeAuthMode = TranscribeAuthCombo.SelectedIndex == 1
            ? AzureAuthMode.EntraId
            : AzureAuthMode.Key;
        _draft.AzureKey = AzureKeyBox.Password;
        _draft.AzureRegion = AzureRegionBox.Text.Trim();
        _draft.AzureEndpoint = AzureEndpointBox.Text.Trim();
        _draft.AzureTenantId = AzureTenantIdBox.Text.Trim();
        _draft.TranscribeEndpoint = TranscribeEndpointBox.Text.Trim();
        _draft.TranscribeModel = TranscribeModelBox.Text.Trim();
        _draft.TranscribeApiKey = TranscribeApiKeyBox.Password;
        _draft.TranscribeTenantId = TranscribeTenantIdBox.Text.Trim();
        _draft.LlmEnabled = LlmEnabledBox.IsChecked == true;
        _draft.LlmBaseUrl = LlmBaseUrlBox.Text.Trim();
        _draft.LlmApiKey = LlmApiKeyBox.Password;
        _draft.LlmModel = LlmModelBox.Text.Trim();
        _draft.LlmPrompt = LlmPromptBox.Text.Trim();
        _draft.Language = LanguageCombo.SelectedValue as string ?? _draft.Language;
        _draft.PttKey = PttCombo.SelectedValue as string ?? _draft.PttKey;
        _draft.UseContext = UseContextBox.IsChecked == true;
        _draft.LearnFromEdits = LearnFromEditsBox.IsChecked == true;
        _draft.DiagnosticLogging = DiagnosticLoggingBox.IsChecked == true;
    }

    private void RefreshAll()
    {
        RefreshOverview();
        RefreshSpeechSummary();
        RefreshModelRows();
        RefreshDirtyState();
    }

    private void RefreshOverview()
    {
        FunAsrModelDefinition selected = FunAsrModelCatalog.Get(_selectedFunAsrModelId);
        bool installed = _funAsr.IsInstalled(selected.Id);
        bool compatible = selected.Supports(_draft.Language);
        bool localReady = installed && compatible;
        int installedCount = FunAsrModelCatalog.Models.Count(model => _funAsr.IsInstalled(model.Id));

        OverviewActiveEngineText.Text = EngineDisplay(_draft.Engine);
        OverviewModelText.Text = selected.DisplayName;
        OverviewLlmText.Text = _draft.LlmEnabled ? "On" : "Off";
        OverviewLocalStatusText.Text = installedCount == 0
            ? "Not installed"
            : $"{installedCount} of {FunAsrModelCatalog.Models.Count} installed";
        OverviewPttText.Text = PttDisplay(_draft.PttKey);
        OverviewLanguageText.Text = LanguageDisplay(_draft.Language);

        if (_draft.Engine == SpeechEngineKind.FunAsr && !localReady)
        {
            OverviewReadinessTitle.Text = "Local speech needs attention";
            OverviewReadinessText.Text = installed
                ? $"{selected.DisplayName} does not support {_draft.Language}."
                : $"{selected.DisplayName} must be downloaded before local dictation can be used.";
            OverviewBanner.Background = new SolidColorBrush(Color.FromRgb(255, 250, 240));
            OverviewBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(228, 212, 183));
        }
        else
        {
            OverviewReadinessTitle.Text = "VoiceInput is ready";
            OverviewReadinessText.Text = $"{EngineDisplay(_draft.Engine)} is selected for new dictation sessions.";
            OverviewBanner.Background = new SolidColorBrush(Color.FromRgb(244, 248, 245));
            OverviewBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(191, 210, 199));
        }

        bool defaultInstalled = _funAsr.IsInstalled(FunAsrModelCatalog.DefaultId);
        SetUpFunAsrButton.Content = defaultInstalled ? "Manage FunASR" : "Set up FunASR";
        FunAsrSummaryText.Text =
            $"Runtime {FunAsrModelCatalog.RuntimeVersion} - {installedCount} of {FunAsrModelCatalog.Models.Count} models installed";
    }

    private void RefreshSpeechSummary()
    {
        FunAsrModelDefinition model = FunAsrModelCatalog.Get(_selectedFunAsrModelId);
        bool installed = _funAsr.IsInstalled(model.Id);
        bool compatible = model.Supports(_draft.Language);
        SpeechFunAsrModelText.Text = model.DisplayName;
        SpeechFunAsrStatusText.Text = !compatible
            ? $"Not compatible with {_draft.Language}. Choose another model or language."
            : installed
                ? "Installed and ready on this device."
                : "Not installed. Download it from the FunASR page before saving.";
        SpeechFunAsrStatusText.Foreground = installed && compatible ? SuccessBrush : AttentionBrush;
    }

    private void RefreshDirtyState()
    {
        bool dirty = !SettingsEqual(_draft, _original);
        SaveButton.IsEnabled = dirty;
        SetStatus(dirty ? "Unsaved changes" : "Settings saved", MutedBrush);
    }

    private void BuildModelRows()
    {
        FunAsrModelsPanel.Children.Clear();
        _modelRows.Clear();
        foreach (FunAsrModelDefinition model in FunAsrModelCatalog.Models)
        {
            var selectedText = new TextBlock
            {
                Text = "Selected",
                FontSize = 11,
                Foreground = SuccessBrush,
                Margin = new Thickness(9, 2, 0, 0),
            };
            var titlePanel = new StackPanel { Orientation = Orientation.Horizontal };
            titlePanel.Children.Add(new TextBlock
            {
                Text = model.DisplayName,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
            });
            titlePanel.Children.Add(selectedText);

            var description = new TextBlock
            {
                Text = model.Description,
                Foreground = MutedBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 18, 0),
            };
            var meta = new TextBlock
            {
                Foreground = MutedBrush,
                FontSize = 11,
                Margin = new Thickness(0, 8, 18, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            meta.Inlines.Add(new Run($"{FormatSize(model.DownloadSize)}  |  {LanguageList(model)}  |  "));
            AddLink(meta, "Source", model.Source);
            meta.Inlines.Add(new Run("  "));
            AddLink(meta, "License", model.License);

            var status = new TextBlock
            {
                Foreground = MutedBrush,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 8, 0, 0),
            };
            var progress = new ProgressBar
            {
                Height = 4,
                Minimum = 0,
                Maximum = 100,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 12, 0, 0),
                Foreground = SuccessBrush,
            };

            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(titlePanel, 0);
            Grid.SetColumn(titlePanel, 0);
            Grid.SetRow(status, 0);
            Grid.SetColumn(status, 1);
            Grid.SetRow(description, 1);
            Grid.SetColumn(description, 0);
            Grid.SetRow(meta, 2);
            Grid.SetColumn(meta, 0);
            Grid.SetRow(buttons, 1);
            Grid.SetRowSpan(buttons, 2);
            Grid.SetColumn(buttons, 1);
            Grid.SetRow(progress, 3);
            Grid.SetColumnSpan(progress, 2);
            content.Children.Add(titlePanel);
            content.Children.Add(status);
            content.Children.Add(description);
            content.Children.Add(meta);
            content.Children.Add(buttons);
            content.Children.Add(progress);

            var border = new Border
            {
                Background = Brushes.White,
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 14, 16, 13),
                Margin = new Thickness(0, 0, 0, 10),
                Child = content,
            };
            FunAsrModelsPanel.Children.Add(border);
            _modelRows[model.Id] = new(selectedText, status, progress, buttons);
        }
    }

    private void RefreshModelRows()
    {
        string? activeModelId = _actions.ActiveFunAsrModelId();
        foreach (FunAsrModelDefinition model in FunAsrModelCatalog.Models)
        {
            ModelRow row = _modelRows[model.Id];
            bool selected = model.Id == _selectedFunAsrModelId;
            bool installed = _funAsr.IsInstalled(model.Id);
            bool compatible = model.Supports(_draft.Language);
            _modelProgress.TryGetValue(model.Id, out FunAsrInstallProgress? progress);
            bool terminal = progress?.Stage is FunAsrInstallStage.Installed
                or FunAsrInstallStage.Failed
                or FunAsrInstallStage.NotInstalled;
            bool installing = model.Id == activeModelId && !terminal;

            row.Selected.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            row.Actions.Children.Clear();
            row.Progress.Visibility = Visibility.Collapsed;
            row.Progress.IsIndeterminate = false;

            if (installing)
            {
                row.Status.Text = ProgressText(progress);
                row.Status.Foreground = AttentionBrush;
                row.Progress.Visibility = Visibility.Visible;
                if (progress?.TotalBytes > 0 && progress.Stage == FunAsrInstallStage.Downloading)
                {
                    row.Progress.Value = Math.Clamp(
                        progress.DownloadedBytes * 100d / progress.TotalBytes.Value, 0, 100);
                }
                else
                {
                    row.Progress.IsIndeterminate = true;
                }
                row.Actions.Children.Add(ActionButton("Cancel", false, () => _actions.CancelFunAsr(model.Id)));
                continue;
            }

            if (installed)
            {
                row.Status.Text = compatible ? "Installed" : $"Installed - not available for {_draft.Language}";
                row.Status.Foreground = compatible ? SuccessBrush : AttentionBrush;
                bool inUse = selected && _draft.Engine == SpeechEngineKind.FunAsr;
                if (inUse)
                {
                    Button inUseButton = ActionButton("In use", true, () => { });
                    inUseButton.IsEnabled = false;
                    row.Actions.Children.Add(inUseButton);
                }
                else
                {
                    Button use = ActionButton("Use", true, () => UseModel(model.Id));
                    use.IsEnabled = compatible;
                    row.Actions.Children.Add(use);
                    Button remove = ActionButton("Remove", false, () => RemoveModel(model.Id));
                    remove.IsEnabled = activeModelId is null;
                    row.Actions.Children.Add(remove);
                }
                continue;
            }

            bool failed = progress?.Stage == FunAsrInstallStage.Failed;
            row.Status.Text = !compatible
                ? $"Not available for {_draft.Language}"
                : failed
                    ? "Installation failed"
                    : "Not installed";
            row.Status.Foreground = failed || !compatible ? AttentionBrush : MutedBrush;
            row.Actions.Children.Add(ActionButton(
                failed ? "Retry" : "Download",
                failed,
                () => _ = InstallModelAsync(model.Id, useAfterInstall: false)));
        }
    }

    private Button ActionButton(string text, bool primary, Action action)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource(primary ? "PrimaryButton" : "SecondaryButton"),
            MinWidth = 76,
            Margin = new Thickness(8, 0, 0, 0),
        };
        button.Click += (_, _) => action();
        return button;
    }

    private async Task InstallModelAsync(string modelId, bool useAfterInstall)
    {
        FunAsrModelDefinition model = FunAsrModelCatalog.Get(modelId);
        try
        {
            SetStatus($"Installing {model.DisplayName}...", AttentionBrush);
            await _actions.InstallFunAsr(modelId);
            if (_closed)
                return;

            if (useAfterInstall)
            {
                _selectedFunAsrModelId = modelId;
                EngineCombo.SelectedIndex = 3;
                Collect();
            }
            RefreshAll();
            SetStatus($"{model.DisplayName} is installed.", SuccessBrush);
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                RefreshAll();
                SetStatus($"{model.DisplayName} download canceled. It can resume later.", MutedBrush);
            }
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                RefreshAll();
                SetStatus(exception.Message, ErrorBrush);
            }
        }
    }

    private void UseModel(string modelId)
    {
        FunAsrModelDefinition model = FunAsrModelCatalog.Get(modelId);
        if (!model.Supports(_draft.Language))
        {
            SetStatus($"{model.DisplayName} does not support {_draft.Language}.", ErrorBrush);
            return;
        }
        if (!_funAsr.IsInstalled(modelId))
        {
            SetStatus($"Download {model.DisplayName} before selecting it.", ErrorBrush);
            return;
        }

        _selectedFunAsrModelId = modelId;
        EngineCombo.SelectedIndex = 3;
        Collect();
        RefreshAll();
    }

    private void RemoveModel(string modelId)
    {
        FunAsrModelDefinition model = FunAsrModelCatalog.Get(modelId);
        bool persistedActive = _original.Engine == SpeechEngineKind.FunAsr
            && FunAsrModelCatalog.NormalizeId(_original.FunAsrModelId) == modelId;
        if (persistedActive)
        {
            SetStatus(
                "Save another speech engine or installed model, then reopen Setup before removing the previously active model.",
                ErrorBrush);
            return;
        }
        if (_draft.Engine == SpeechEngineKind.FunAsr && _selectedFunAsrModelId == modelId)
        {
            SetStatus("Select another speech engine or installed model before removing the active model.", ErrorBrush);
            return;
        }
        if (MessageBox.Show(
                $"Remove {model.DisplayName} from this device?",
                "Remove FunASR model",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _funAsr.Remove(modelId);
            _modelProgress.Remove(modelId);
            RefreshAll();
            SetStatus($"{model.DisplayName} was removed.", MutedBrush);
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, ErrorBrush);
        }
    }

    private void OnFunAsrProgress(FunAsrInstallProgress progress)
    {
        if (string.IsNullOrWhiteSpace(progress.ModelId))
            return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_closed)
                return;
            _modelProgress[progress.ModelId] = progress;
            RefreshOverview();
            RefreshSpeechSummary();
            RefreshModelRows();
            if (progress.Stage == FunAsrInstallStage.Failed)
                SetStatus(progress.Error ?? "FunASR installation failed.", ErrorBrush);
        });
    }

    private async void OnSetUpFunAsr(object sender, RoutedEventArgs e)
    {
        if (_funAsr.IsInstalled(FunAsrModelCatalog.DefaultId))
        {
            FunAsrNav.IsChecked = true;
            return;
        }
        await InstallModelAsync(FunAsrModelCatalog.DefaultId, useAfterInstall: true);
    }

    private void OnManageFunAsr(object sender, RoutedEventArgs e) => FunAsrNav.IsChecked = true;

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        Collect();
        LlmTestStatusText.Foreground = MutedBrush;
        LlmTestStatusText.Text = "Testing...";
        TestButton.IsEnabled = false;
        try
        {
            var (ok, message) = await _refiner.TestAsync(_draft);
            LlmTestStatusText.Foreground = ok ? SuccessBrush : ErrorBrush;
            LlmTestStatusText.Text = message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        _availableUpdateTag = null;
        SetStatus("Checking for updates...", MutedBrush);
        try
        {
            UpdateService.CheckResult result = await _actions.CheckForUpdates();
            switch (result.Outcome)
            {
                case UpdateService.CheckOutcome.UpdateAvailable:
                    _availableUpdateTag = result.LatestTag;
                    InstallUpdateButton.Content = $"Update to {result.LatestTag}";
                    InstallUpdateButton.Visibility = Visibility.Visible;
                    SetStatus($"{result.LatestTag} is available.", AttentionBrush);
                    break;
                case UpdateService.CheckOutcome.UpToDate:
                    SetStatus($"You're using the latest version (v{UpdateService.CurrentVersion}).", SuccessBrush);
                    break;
                case UpdateService.CheckOutcome.CheckFailed:
                    SetStatus("Update check failed. Please try again.", ErrorBrush);
                    break;
            }
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, ErrorBrush);
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_availableUpdateTag is { } tag)
            _actions.InstallUpdate(tag);
    }

    private void OnStartAtLoginChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        bool requested = StartAtLoginBox.IsChecked == true;
        _actions.SetAutoStart(requested);
        bool actual = _actions.IsAutoStartEnabled();
        if (actual != requested)
        {
            _loading = true;
            StartAtLoginBox.IsChecked = actual;
            _loading = false;
            SetStatus("Start-at-login could not be changed. See the log for details.", ErrorBrush);
        }
        else
        {
            SetStatus(actual ? "Start-at-login enabled." : "Start-at-login disabled.", SuccessBrush);
        }
    }

    private void OnOpenLog(object sender, RoutedEventArgs e) => _actions.OpenLog();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Collect();
        string? validation = ValidateDraft();
        if (validation is not null)
        {
            SetStatus(validation, ErrorBrush);
            if (_draft.Engine == SpeechEngineKind.FunAsr)
                FunAsrNav.IsChecked = true;
            return;
        }

        _onSave(_draft.Clone());
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private string? ValidateDraft()
    {
        if (_draft.Engine == SpeechEngineKind.Azure)
        {
            if (_draft.AzureAuthMode == AzureAuthMode.Key
                && (string.IsNullOrWhiteSpace(_draft.AzureKey) || string.IsNullOrWhiteSpace(_draft.AzureRegion)))
            {
                return "Azure Speech key authentication requires both Key and Region.";
            }
            if (_draft.AzureAuthMode == AzureAuthMode.EntraId && !ValidEndpoint(_draft.AzureEndpoint))
                return "Azure Speech Entra authentication requires a valid HTTPS Endpoint.";
        }
        if (_draft.Engine == SpeechEngineKind.GptTranscribe)
        {
            if (!ValidEndpoint(_draft.TranscribeEndpoint) || string.IsNullOrWhiteSpace(_draft.TranscribeModel))
                return "Foundry transcription requires a valid HTTPS Endpoint and Deployment.";
            if (_draft.TranscribeAuthMode == AzureAuthMode.Key
                && string.IsNullOrWhiteSpace(_draft.TranscribeApiKey))
            {
                return "Foundry key authentication requires an API Key.";
            }
        }
        if (_draft.Engine == SpeechEngineKind.FunAsr)
        {
            FunAsrModelDefinition model = FunAsrModelCatalog.Get(_draft.FunAsrModelId);
            if (!model.Supports(_draft.Language))
                return $"{model.DisplayName} does not support {_draft.Language}.";
            if (!_funAsr.IsInstalled(model.Id))
                return $"Download {model.DisplayName} before selecting FunASR.";
        }
        if (_draft.LlmEnabled
            && (!ValidEndpoint(_draft.LlmBaseUrl) || string.IsNullOrWhiteSpace(_draft.LlmModel)))
        {
            return "LLM refinement requires a valid HTTPS Base URL and Model.";
        }
        return null;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _funAsr.ProgressChanged -= OnFunAsrProgress;
    }

    private void SetStatus(string message, Brush brush)
    {
        StatusText.Text = message;
        StatusText.Foreground = brush;
    }

    private static bool ValidEndpoint(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static int EngineIndex(SpeechEngineKind engine) => engine switch
    {
        SpeechEngineKind.Azure => 1,
        SpeechEngineKind.GptTranscribe => 2,
        SpeechEngineKind.FunAsr => 3,
        _ => 0,
    };

    private static string EngineDisplay(SpeechEngineKind engine) => engine switch
    {
        SpeechEngineKind.Azure => "Azure Speech",
        SpeechEngineKind.GptTranscribe => "gpt-4o-transcribe",
        SpeechEngineKind.FunAsr => "FunASR (local)",
        _ => "Windows dictation",
    };

    private static string PttDisplay(string key) => key switch
    {
        "RightCtrl" => "Right Ctrl",
        "LeftCtrl" => "Left Ctrl",
        "CapsLock" => "Caps Lock",
        "RightAlt" => "Right Alt",
        "RightShift" => "Right Shift",
        _ => key,
    };

    private static string LanguageDisplay(string language) =>
        AppSettings.SupportedLanguages.FirstOrDefault(item => item.Code == language).Display ?? language;

    private static string LanguageList(FunAsrModelDefinition model) => string.Join(
        ", ",
        AppSettings.SupportedLanguages
            .Where(item => model.Supports(item.Code))
            .Select(item => item.Code.Split('-')[0].ToUpperInvariant()));

    private static string FormatSize(long size) => size >= 1_000_000_000
        ? $"{size / 1_000_000_000d:F1} GB"
        : $"{size / 1_000_000d:F0} MB";

    private static string ProgressText(FunAsrInstallProgress? progress) => progress?.Stage switch
    {
        FunAsrInstallStage.Downloading => $"Downloading {progress.Artifact}",
        FunAsrInstallStage.Verifying => "Verifying download",
        FunAsrInstallStage.Testing => "Testing local runtime",
        _ => "Installing",
    };

    private static void AddLink(TextBlock target, string text, Uri uri)
    {
        var link = new Hyperlink(new Run(text)) { NavigateUri = uri };
        link.RequestNavigate += OpenLink;
        target.Inlines.Add(link);
    }

    private static void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { }
        e.Handled = true;
    }

    private static bool SettingsEqual(AppSettings left, AppSettings right) =>
        left.Language == right.Language
        && left.PttKey == right.PttKey
        && left.Engine == right.Engine
        && left.FunAsrModelId == right.FunAsrModelId
        && left.AzureKey == right.AzureKey
        && left.AzureRegion == right.AzureRegion
        && left.AzureAuthMode == right.AzureAuthMode
        && left.AzureEndpoint == right.AzureEndpoint
        && left.AzureTenantId == right.AzureTenantId
        && left.TranscribeEndpoint == right.TranscribeEndpoint
        && left.TranscribeModel == right.TranscribeModel
        && left.TranscribeAuthMode == right.TranscribeAuthMode
        && left.TranscribeApiKey == right.TranscribeApiKey
        && left.TranscribeTenantId == right.TranscribeTenantId
        && left.LlmEnabled == right.LlmEnabled
        && left.LlmBaseUrl == right.LlmBaseUrl
        && left.LlmApiKey == right.LlmApiKey
        && left.LlmModel == right.LlmModel
        && left.LlmPrompt == right.LlmPrompt
        && left.LlmLearnedRules == right.LlmLearnedRules
        && left.LearnFromEdits == right.LearnFromEdits
        && left.DiagnosticLogging == right.DiagnosticLogging
        && left.UseContext == right.UseContext;

    private sealed record ModelRow(
        TextBlock Selected,
        TextBlock Status,
        ProgressBar Progress,
        StackPanel Actions);
}
