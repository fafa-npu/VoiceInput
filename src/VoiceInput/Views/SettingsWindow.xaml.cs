using System.Diagnostics;
using System.IO;
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
    Func<string?> ActiveFunAsrModelId,
    Func<AppSettings, Task<string[]>> ExtractVocabularyFromCorrections);

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
    private bool _extractingVocabulary;
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
        EngineList.SelectedIndex = EngineIndex(_draft.Engine);
        AzureAuthCombo.SelectedIndex = _draft.AzureAuthMode == AzureAuthMode.EntraId ? 1 : 0;
        TranscribeAuthCombo.SelectedIndex = _draft.TranscribeAuthMode == AzureAuthMode.EntraId ? 1 : 0;
        AzureKeyBox.Password = _draft.AzureKey;
        AzureRegionBox.Text = _draft.AzureRegion;
        AzureEndpointBox.Text = _draft.AzureEndpoint;
        AzureTenantIdBox.Text = _draft.AzureTenantId;
        TranscribeEndpointBox.Text = _draft.TranscribeEndpoint;
        TranscribeModelKindCombo.SelectedIndex = TranscribeModelKindIndex(_draft.TranscribeModelKind);
        TranscribeModelBox.Text = _draft.TranscribeModel;
        TranscribeApiKeyBox.Password = _draft.TranscribeApiKey;
        TranscribeTenantIdBox.Text = _draft.TranscribeTenantId;
        VocabularyBox.Text = string.Join(", ", _draft.RecognitionVocabulary);
        LlmEnabledBox.IsChecked = _draft.LlmEnabled;
        LlmBaseUrlBox.Text = _draft.LlmBaseUrl;
        LlmApiKeyBox.Password = _draft.LlmApiKey;
        LlmModelBox.Text = _draft.LlmModel;
        LlmPromptBox.Text = _draft.LlmPrompt;
        LanguageCombo.SelectedValue = _draft.Language;
        LoadProfileControls(
            _draft.GetProfile(InputProfile.Profile1Id),
            Profile1NameBox,
            Profile1KeyCombo,
            Profile1ModeCombo,
            Profile1OverlayCombo);
        LoadProfileControls(
            _draft.GetProfile(InputProfile.Profile2Id),
            Profile2NameBox,
            Profile2KeyCombo,
            Profile2ModeCombo,
            Profile2OverlayCombo);
        Profile1ActiveRadio.IsChecked = _draft.ActiveProfileId == InputProfile.Profile1Id;
        Profile2ActiveRadio.IsChecked = _draft.ActiveProfileId == InputProfile.Profile2Id;
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
            VocabularyBox,
            LlmBaseUrlBox,
            LlmModelBox,
            LlmPromptBox,
            Profile1NameBox,
            Profile2NameBox,
        })
        {
            box.TextChanged += OnDraftValueChanged;
        }

        foreach (PasswordBox box in new[] { AzureKeyBox, TranscribeApiKeyBox, LlmApiKeyBox })
            box.PasswordChanged += OnDraftValueChanged;

        LanguageCombo.SelectionChanged += OnDraftValueChanged;
        foreach (ComboBox combo in new[]
        {
            Profile1KeyCombo,
            Profile1ModeCombo,
            Profile1OverlayCombo,
            Profile2KeyCombo,
            Profile2ModeCombo,
            Profile2OverlayCombo,
        })
        {
            combo.SelectionChanged += OnDraftValueChanged;
        }
        Profile1ActiveRadio.Checked += OnDraftValueChanged;
        Profile2ActiveRadio.Checked += OnDraftValueChanged;
        TranscribeModelKindCombo.SelectionChanged += OnDraftValueChanged;
        LlmEnabledBox.Click += OnDraftValueChanged;
        UseContextBox.Click += OnSensitiveSettingChanged;
        LearnFromEditsBox.Click += OnSensitiveSettingChanged;
        DiagnosticLoggingBox.Click += OnSensitiveSettingChanged;
    }

    private void OnNavigationChanged(object sender, RoutedEventArgs e)
    {
        if (OverviewPage is null || ModelSelectionPage is null || ProfilesPage is null || VocabularyPage is null
            || RefinementPage is null || AppPage is null)
        {
            return;
        }

        OverviewPage.Visibility = sender == OverviewNav ? Visibility.Visible : Visibility.Collapsed;
        ModelSelectionPage.Visibility = sender == ModelSelectionNav ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = sender == ProfilesNav ? Visibility.Visible : Visibility.Collapsed;
        VocabularyPage.Visibility = sender == VocabularyNav ? Visibility.Visible : Visibility.Collapsed;
        RefinementPage.Visibility = sender == RefinementNav ? Visibility.Visible : Visibility.Collapsed;
        AppPage.Visibility = sender == AppNav ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AzureFieldsPanel is null)
            return;
        UpdateFieldVisibility();
        OnDraftValueChanged(sender, e);
        if (_loading)
            return;

        FrameworkElement details = EngineList.SelectedIndex switch
        {
            1 => AzureFieldsPanel,
            2 => TranscribeFieldsPanel,
            3 => LocalModelsPanel,
            _ => WindowsFieldsPanel,
        };
        _ = Dispatcher.BeginInvoke(new Action(details.BringIntoView));
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
                ? "gujiguji will read text from the focused app with UI Automation and send it to your configured LLM."
                : checkBox == LearnFromEditsBox
                    ? "After insertion, Enter may capture the same input control. Up to 100 encrypted correction samples are stored locally."
                    : "Full transcripts, recognition vocabulary, and LLM output may contain sensitive data and will be written in plaintext to the diagnostic log.";
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
        bool windows = EngineList.SelectedIndex == 0;
        bool azure = EngineList.SelectedIndex == 1;
        bool transcribe = EngineList.SelectedIndex == 2;
        bool funAsr = EngineList.SelectedIndex == 3;
        bool azureEntra = AzureAuthCombo.SelectedIndex == 1;
        bool transcribeEntra = TranscribeAuthCombo.SelectedIndex == 1;

        WindowsFieldsPanel.Visibility = windows ? Visibility.Visible : Visibility.Collapsed;
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
        LocalModelsPanel.Visibility = funAsr ? Visibility.Visible : Visibility.Collapsed;

        bool llmConfigured = LlmEnabledBox.IsChecked == true
            && LlmRefiner.IsSupportedEndpoint(LlmBaseUrlBox.Text.Trim())
            && !string.IsNullOrWhiteSpace(LlmModelBox.Text);
        UseContextBox.IsEnabled = llmConfigured;
        LearnFromEditsBox.IsEnabled = llmConfigured;
        SuggestVocabularyButton.IsEnabled = llmConfigured && !_extractingVocabulary;
        if (!_extractingVocabulary)
        {
            VocabularySuggestionStatusText.Foreground = MutedBrush;
            VocabularySuggestionStatusText.Text = llmConfigured
                ? "Uses encrypted correction samples stored on this device."
                : "Configure and enable LLM refinement to use this.";
        }
    }

    private async void OnSuggestVocabulary(object sender, RoutedEventArgs e)
    {
        Collect();
        if (!LlmRefiner.IsConfigured(_draft))
        {
            VocabularySuggestionStatusText.Foreground = ErrorBrush;
            VocabularySuggestionStatusText.Text = "Configure and enable LLM refinement first.";
            return;
        }

        _extractingVocabulary = true;
        SuggestVocabularyButton.IsEnabled = false;
        VocabularySuggestionStatusText.Foreground = MutedBrush;
        VocabularySuggestionStatusText.Text = "Analyzing local correction samples...";
        try
        {
            string[] candidates = await _actions.ExtractVocabularyFromCorrections(_draft.Clone());
            if (_closed)
                return;
            if (candidates.Length == 0)
            {
                VocabularySuggestionStatusText.Text = "No recurring terms found.";
                return;
            }
            string[] current = RecognitionVocabulary.Parse(VocabularyBox.Text).Entries;
            string[] merged = RecognitionVocabulary.Normalize(current.Concat(candidates)).Entries;
            int added = merged.Length - current.Length;
            if (added == 0)
            {
                VocabularySuggestionStatusText.Text = "All suggested terms are already in the vocabulary.";
                return;
            }

            VocabularyBox.Text = string.Join(", ", merged);
            VocabularySuggestionStatusText.Foreground = SuccessBrush;
            VocabularySuggestionStatusText.Text =
                $"Added {added} suggestion{(added == 1 ? string.Empty : "s")} for review. Save changes to apply.";
            Log.Write($"Vocabulary suggestions staged candidateCount={candidates.Length} added={added}.");
        }
        catch (Exception exception)
        {
            Log.Error("Vocabulary suggestion", exception);
            VocabularySuggestionStatusText.Foreground = ErrorBrush;
            VocabularySuggestionStatusText.Text = exception.Message;
        }
        finally
        {
            _extractingVocabulary = false;
            if (!_closed)
                SuggestVocabularyButton.IsEnabled = LlmRefiner.IsConfigured(_draft);
        }
    }

    private void Collect()
    {
        _draft.Engine = EngineList.SelectedIndex switch
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
        _draft.TranscribeModelKind = TranscribeModelKindCombo.SelectedIndex switch
        {
            0 => TranscribeModelKind.Gpt4oTranscribe,
            1 => TranscribeModelKind.Gpt4oMiniTranscribe,
            2 => TranscribeModelKind.Gpt4oTranscribeDiarize,
            _ => TranscribeModelKind.Unknown,
        };
        _draft.TranscribeModel = TranscribeModelBox.Text.Trim();
        _draft.TranscribeApiKey = TranscribeApiKeyBox.Password;
        _draft.TranscribeTenantId = TranscribeTenantIdBox.Text.Trim();
        _draft.RecognitionVocabulary = RecognitionVocabulary.Parse(VocabularyBox.Text).Entries;
        _draft.LlmEnabled = LlmEnabledBox.IsChecked == true;
        _draft.LlmBaseUrl = LlmBaseUrlBox.Text.Trim();
        _draft.LlmApiKey = LlmApiKeyBox.Password;
        _draft.LlmModel = LlmModelBox.Text.Trim();
        _draft.LlmPrompt = LlmPromptBox.Text.Trim();
        _draft.Language = LanguageCombo.SelectedValue as string ?? _draft.Language;
        CollectProfileControls(
            _draft.GetProfile(InputProfile.Profile1Id),
            Profile1NameBox,
            Profile1KeyCombo,
            Profile1ModeCombo,
            Profile1OverlayCombo);
        CollectProfileControls(
            _draft.GetProfile(InputProfile.Profile2Id),
            Profile2NameBox,
            Profile2KeyCombo,
            Profile2ModeCombo,
            Profile2OverlayCombo);
        _draft.ActiveProfileId = Profile2ActiveRadio.IsChecked == true
            ? InputProfile.Profile2Id
            : InputProfile.Profile1Id;
        _draft.UseContext = UseContextBox.IsChecked == true;
        _draft.LearnFromEdits = LearnFromEditsBox.IsChecked == true;
        _draft.DiagnosticLogging = DiagnosticLoggingBox.IsChecked == true;
    }

    private void RefreshAll()
    {
        RefreshOverview();
        RefreshModelSelectionSummary();
        RefreshVocabulary();
        RefreshModelRows();
        RefreshDirtyState();
    }

    private void RefreshVocabulary()
    {
        RecognitionVocabularyNormalization vocabulary = RecognitionVocabulary.Parse(VocabularyBox.Text);
        VocabularyCountText.Text = vocabulary.AcceptedCount == 1
            ? "1 term"
            : $"{vocabulary.AcceptedCount} terms";

        RecognitionVocabularyMode mode = RecognitionVocabulary.ResolveMode(
            _draft.Engine,
            _draft.TranscribeModelKind);
        string current = VocabularyEngineDisplay(_draft.Engine, _draft.TranscribeModelKind);
        VocabularyCurrentEngineText.Text = current;
        VocabularyModeText.Text = mode switch
        {
            RecognitionVocabularyMode.PhraseList
                when vocabulary.AcceptedCount > AzureSpeechEngine.MaxVocabularyPhrases =>
                    $"First {AzureSpeechEngine.MaxVocabularyPhrases} of {vocabulary.AcceptedCount} terms "
                    + "will be used as an Azure Phrase List",
            RecognitionVocabularyMode.PhraseList => "Used as an Azure Phrase List",
            RecognitionVocabularyMode.Prompt => "Used as a transcription prompt",
            _ => "Not supported. Use Azure Speech, GPT-4o Transcribe, or Mini.",
        };
        VocabularyModeText.Foreground = mode == RecognitionVocabularyMode.None
            ? AttentionBrush
            : SuccessBrush;
        VocabularyEngineStatusBorder.Background = mode == RecognitionVocabularyMode.None
            ? new SolidColorBrush(Color.FromRgb(255, 250, 240))
            : new SolidColorBrush(Color.FromRgb(244, 248, 245));
        VocabularyEngineStatusBorder.BorderBrush = mode == RecognitionVocabularyMode.None
            ? new SolidColorBrush(Color.FromRgb(228, 212, 183))
            : new SolidColorBrush(Color.FromRgb(191, 210, 199));
    }

    private void RefreshOverview()
    {
        bool localEngine = _draft.Engine == SpeechEngineKind.FunAsr;
        OverviewLocalModelPanel.Visibility = localEngine ? Visibility.Visible : Visibility.Collapsed;
        OverviewLocalReadinessPanel.Visibility = localEngine ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(OverviewLlmPanel, localEngine ? 1 : 0);
        Grid.SetColumnSpan(OverviewLlmPanel, localEngine ? 1 : 3);

        FunAsrModelDefinition selected = FunAsrModelCatalog.Get(_selectedFunAsrModelId);
        bool installed = _funAsr.HasInstalledFiles(selected.Id);
        bool compatible = selected.Supports(_draft.Language);
        bool localReady = installed && compatible;
        int installedCount = FunAsrModelCatalog.Models.Count(model => _funAsr.HasInstalledFiles(model.Id));

        OverviewActiveEngineText.Text = EngineDisplay(_draft.Engine);
        OverviewModelText.Text = selected.DisplayName;
        OverviewLlmText.Text = _draft.LlmEnabled ? "On" : "Off";
        OverviewLocalStatusText.Text = installedCount == 0
            ? "Not installed"
            : $"{installedCount} of {FunAsrModelCatalog.Models.Count} installed";
        InputProfile activeProfile = _draft.ActiveProfile;
        string pttBehavior = activeProfile.PttMode == PttMode.Toggle ? "press to start/stop" : "hold to talk";
        OverviewPttText.Text = $"{activeProfile.Name} · {PttDisplay(activeProfile.PttKey)} · {pttBehavior}";
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
            OverviewReadinessTitle.Text = "gujiguji is ready";
            OverviewReadinessText.Text = $"{EngineDisplay(_draft.Engine)} is selected for new dictation sessions.";
            OverviewBanner.Background = new SolidColorBrush(Color.FromRgb(244, 248, 245));
            OverviewBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(191, 210, 199));
        }

        FunAsrSummaryText.Text =
            $"Runtime {FunAsrModelCatalog.RuntimeVersion} · {RuntimeBuild} · "
            + $"{installedCount} of {FunAsrModelCatalog.Models.Count} models installed";
    }

    private void RefreshModelSelectionSummary()
    {
        string active = SelectionDisplay(_original, _original.FunAsrModelId);
        string selected = SelectionDisplay(_draft, _selectedFunAsrModelId);
        bool sameSelection = !HasModelSelectionChanged();

        if (_draft.Engine == SpeechEngineKind.Azure)
        {
            bool configured = _draft.AzureAuthMode == AzureAuthMode.Key
                ? !string.IsNullOrWhiteSpace(_draft.AzureKey) && !string.IsNullOrWhiteSpace(_draft.AzureRegion)
                : ValidEndpoint(_draft.AzureEndpoint);
            if (!configured)
            {
                SetModelSelectionStatus(
                    $"{active} is active. Configure Azure Speech to finish this selection.",
                    attention: true);
                return;
            }
        }
        else if (_draft.Engine == SpeechEngineKind.GptTranscribe)
        {
            bool configured = ValidEndpoint(_draft.TranscribeEndpoint)
                && !string.IsNullOrWhiteSpace(_draft.TranscribeModel)
                && (_draft.TranscribeAuthMode != AzureAuthMode.Key
                    || !string.IsNullOrWhiteSpace(_draft.TranscribeApiKey));
            if (!configured)
            {
                SetModelSelectionStatus(
                    $"{active} is active. Configure GPT-4o Transcribe to finish this selection.",
                    attention: true);
                return;
            }
        }
        else if (_draft.Engine == SpeechEngineKind.FunAsr)
        {
            FunAsrModelDefinition model = FunAsrModelCatalog.Get(_selectedFunAsrModelId);
            if (!model.Supports(_draft.Language))
            {
                SetModelSelectionStatus(
                    $"{active} is active. {model.DisplayName} is not available for {_draft.Language}.",
                    attention: true);
                return;
            }
            if (!_funAsr.HasInstalledFiles(model.Id))
            {
                SetModelSelectionStatus(
                    $"{active} is active. Download {model.DisplayName} to finish this local selection.",
                    attention: true);
                return;
            }
        }

        SetModelSelectionStatus(
            sameSelection
                ? $"{active} is active for new dictation sessions."
                : $"{active} is active. {selected} will be used after you save changes.",
            attention: !sameSelection);
    }

    private void SetModelSelectionStatus(string text, bool attention)
    {
        ModelSelectionStatusText.Text = text;
        ModelSelectionStatusText.Foreground = attention ? AttentionBrush : SuccessBrush;
        ModelSelectionStatusBorder.Background = attention
            ? new SolidColorBrush(Color.FromRgb(255, 250, 240))
            : new SolidColorBrush(Color.FromRgb(244, 248, 245));
        ModelSelectionStatusBorder.BorderBrush = attention
            ? new SolidColorBrush(Color.FromRgb(228, 212, 183))
            : new SolidColorBrush(Color.FromRgb(191, 210, 199));
    }

    private void RefreshDirtyState()
    {
        bool dirty = !SettingsEqual(_draft, _original);
        string? installingModelId = _modelProgress
            .Where(item => item.Value.Stage is not (
                FunAsrInstallStage.Installed or FunAsrInstallStage.Failed or FunAsrInstallStage.NotInstalled))
            .Select(item => item.Key)
            .FirstOrDefault();
        bool installing = installingModelId is not null;
        bool selectionChanged = HasModelSelectionChanged();
        FunAsrModelDefinition localModel = FunAsrModelCatalog.Get(_selectedFunAsrModelId);
        string? localSelectionBlocker = _draft.Engine != SpeechEngineKind.FunAsr
            ? null
            : !localModel.Supports(_draft.Language)
                ? $"Choose a local model that supports {_draft.Language}"
                : !_funAsr.HasInstalledFiles(localModel.Id)
                    ? $"Download {localModel.DisplayName} to continue"
                    : null;

        SaveButton.IsEnabled = dirty && !installing && localSelectionBlocker is null;
        SaveButton.Content = dirty
            && _draft.Engine == SpeechEngineKind.FunAsr
            && localSelectionBlocker is null
                ? $"Save and use {localModel.DisplayName}"
                : "Save changes";
        CancelButton.IsEnabled = !installing;
        CancelButton.Content = dirty ? "Discard changes" : "Close";

        string active = SelectionDisplay(_original, _original.FunAsrModelId);
        string selected = SelectionDisplay(_draft, _selectedFunAsrModelId);
        string status = installing
            ? $"Current: {active} · Downloading {FunAsrModelCatalog.Get(installingModelId!).DisplayName}"
            : localSelectionBlocker is not null
                ? $"Current: {active} · {localSelectionBlocker}"
                : selectionChanged
                    ? $"Current: {active} · Pending: {selected}"
                    : dirty ? "Unsaved changes" : "Settings saved";
        SetStatus(
            status,
            installing || selectionChanged || localSelectionBlocker is not null ? AttentionBrush : MutedBrush);
    }

    private bool HasModelSelectionChanged() =>
        _original.Engine != _draft.Engine
        || (_draft.Engine == SpeechEngineKind.GptTranscribe
            && _original.TranscribeModelKind != _draft.TranscribeModelKind)
        || (_draft.Engine == SpeechEngineKind.FunAsr
            && FunAsrModelCatalog.NormalizeId(_original.FunAsrModelId) != _selectedFunAsrModelId);

    private void BuildModelRows()
    {
        FunAsrModelsPanel.Children.Clear();
        _modelRows.Clear();
        foreach (FunAsrModelDefinition model in FunAsrModelCatalog.Models)
        {
            var selectedText = new TextBlock
            {
                Text = string.Empty,
                FontSize = 11,
                Foreground = SuccessBrush,
                Margin = new Thickness(9, 2, 0, 0),
                Visibility = Visibility.Collapsed,
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
            meta.Inlines.Add(new Run(
                $"Up to {FormatSize(LocalPackageSize(model))}  |  {LanguageList(model)}  |  "));
            AddLink(meta, "Source", model.Source);
            meta.Inlines.Add(new Run("  "));
            AddLink(meta, "License", model.License);

            var status = new TextBlock
            {
                Foreground = MutedBrush,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 8, 0, 0),
            };
            var progressDetail = new TextBlock
            {
                Foreground = MutedBrush,
                FontSize = 11,
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };
            var progress = new ProgressBar
            {
                Height = 8,
                Minimum = 0,
                Maximum = 100,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = SuccessBrush,
            };

            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(titlePanel, 0);
            Grid.SetColumnSpan(titlePanel, 2);
            Grid.SetRow(status, 1);
            Grid.SetColumnSpan(status, 2);
            Grid.SetRow(description, 2);
            Grid.SetColumn(description, 0);
            Grid.SetRow(meta, 3);
            Grid.SetColumn(meta, 0);
            Grid.SetRow(buttons, 2);
            Grid.SetRowSpan(buttons, 2);
            Grid.SetColumn(buttons, 1);
            Grid.SetRow(progressDetail, 4);
            Grid.SetColumnSpan(progressDetail, 2);
            Grid.SetRow(progress, 5);
            Grid.SetColumnSpan(progress, 2);
            content.Children.Add(titlePanel);
            content.Children.Add(status);
            content.Children.Add(description);
            content.Children.Add(meta);
            content.Children.Add(buttons);
            content.Children.Add(progressDetail);
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
            _modelRows[model.Id] = new(selectedText, status, progressDetail, progress, buttons);
        }
    }

    private void RefreshModelRows()
    {
        string? activeModelId = _actions.ActiveFunAsrModelId();
        foreach (FunAsrModelDefinition model in FunAsrModelCatalog.Models)
        {
            ModelRow row = _modelRows[model.Id];
            bool selected = model.Id == _selectedFunAsrModelId;
            bool compatible = model.Supports(_draft.Language);
            bool installed = _funAsr.HasInstalledFiles(model.Id);
            bool active = _original.Engine == SpeechEngineKind.FunAsr
                && FunAsrModelCatalog.NormalizeId(_original.FunAsrModelId) == model.Id;
            bool willUse = installed && selected && _draft.Engine == SpeechEngineKind.FunAsr;
            _modelProgress.TryGetValue(model.Id, out FunAsrInstallProgress? progress);
            bool terminal = progress?.Stage is FunAsrInstallStage.Installed
                or FunAsrInstallStage.Failed
                or FunAsrInstallStage.NotInstalled;
            bool installing = !terminal && (model.Id == activeModelId || progress is not null);

            row.Selected.Text = willUse
                ? active ? "Active" : "Will use after Save"
                : active
                    ? "Active now"
                    : model.Id == FunAsrModelCatalog.DefaultId ? "Recommended" : string.Empty;
            row.Selected.Visibility = string.IsNullOrEmpty(row.Selected.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            row.Actions.Children.Clear();
            row.ProgressDetail.Visibility = Visibility.Collapsed;
            row.ProgressDetail.Text = string.Empty;
            row.Progress.Visibility = Visibility.Collapsed;
            row.Progress.IsIndeterminate = false;
            row.Progress.Value = 0;

            if (installing)
            {
                row.Status.Text = ProgressText(model, progress);
                row.Status.Foreground = AttentionBrush;
                row.ProgressDetail.Text = ProgressDetailText(progress);
                row.ProgressDetail.Visibility = Visibility.Visible;
                row.Progress.Visibility = Visibility.Visible;
                if (progress?.TotalBytes > 0 && progress.DownloadedBytes > 0)
                {
                    row.Progress.Value = Math.Clamp(
                        progress.DownloadedBytes * 100d / progress.TotalBytes.Value, 0, 100);
                }
                else
                {
                    row.Progress.IsIndeterminate = true;
                }
                row.Actions.Children.Add(ActionButton("Cancel download", false, () => _actions.CancelFunAsr(model.Id)));
                continue;
            }

            if (installed)
            {
                row.Status.Text = !compatible
                    ? $"Installed - not available for {_draft.Language}"
                    : willUse
                        ? active ? "Installed and active" : "Installed - selected for Save"
                        : "Installed and ready";
                row.Status.Foreground = compatible ? SuccessBrush : AttentionBrush;
                if (willUse)
                {
                    Button inUseButton = ActionButton(active ? "Active" : "Will use after Save", true, () => { });
                    inUseButton.IsEnabled = false;
                    row.Actions.Children.Add(inUseButton);
                }
                else
                {
                    Button use = ActionButton("Use after Save", true, () => UseModel(model.Id));
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
                failed ? "Retry package" : $"Download package · {FormatSize(LocalPackageSize(model))}",
                failed,
                () => _ = InstallModelAsync(model.Id)));
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

    private async Task InstallModelAsync(string modelId)
    {
        FunAsrModelDefinition model = FunAsrModelCatalog.Get(modelId);
        try
        {
            SetStatus($"Preparing {model.DisplayName} package...", AttentionBrush);
            long packageSize = LocalPackageSize(model);
            _modelProgress[modelId] = new(
                modelId,
                FunAsrInstallStage.Downloading,
                Path.GetFileName(FunAsrModelCatalog.Runtime.RelativePath),
                0,
                packageSize);
            RefreshModelRows();
            RefreshDirtyState();
            _ = Dispatcher.BeginInvoke(new Action(_modelRows[modelId].Progress.BringIntoView));
            await Task.Run(() => _actions.InstallFunAsr(modelId));
            if (_closed)
                return;

            RefreshAll();
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                _modelProgress[modelId] = new(
                    modelId, FunAsrInstallStage.NotInstalled, string.Empty, 0, null);
                RefreshAll();
                SetStatus($"{model.DisplayName} download canceled. It can resume later.", MutedBrush);
            }
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                _modelProgress[modelId] = new(
                    modelId, FunAsrInstallStage.Failed, string.Empty, 0, null, exception.Message);
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
        if (!_funAsr.HasInstalledFiles(modelId))
        {
            SetStatus($"Download {model.DisplayName} before selecting it.", ErrorBrush);
            return;
        }

        _selectedFunAsrModelId = modelId;
        EngineList.SelectedIndex = 3;
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
            if (progress.Stage is FunAsrInstallStage.Installed
                or FunAsrInstallStage.Failed
                or FunAsrInstallStage.NotInstalled)
            {
                RefreshAll();
            }
            else
            {
                RefreshModelRows();
                RefreshDirtyState();
            }
            if (progress.Stage == FunAsrInstallStage.Failed)
                SetStatus(progress.Error ?? "FunASR installation failed.", ErrorBrush);
        });
    }

    private void OnChooseModel(object sender, RoutedEventArgs e) => ModelSelectionNav.IsChecked = true;

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
            if (ValidateProfiles() is not null)
                ProfilesNav.IsChecked = true;
            else if (_draft.Engine == SpeechEngineKind.FunAsr)
                ModelSelectionNav.IsChecked = true;
            return;
        }

        _onSave(_draft.Clone());
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private string? ValidateDraft()
    {
        string? profileValidation = ValidateProfiles();
        if (profileValidation is not null)
            return profileValidation;
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
            if (!_funAsr.HasInstalledFiles(model.Id))
                return $"Download {model.DisplayName} before selecting FunASR.";
        }
        if (_draft.LlmEnabled
            && (!LlmRefiner.IsSupportedEndpoint(_draft.LlmBaseUrl)
                || string.IsNullOrWhiteSpace(_draft.LlmModel)))
        {
            return "LLM refinement requires HTTPS, or HTTP on this device, plus a Model.";
        }
        return null;
    }

    private string? ValidateProfiles()
    {
        InputProfile first = _draft.GetProfile(InputProfile.Profile1Id);
        InputProfile second = _draft.GetProfile(InputProfile.Profile2Id);
        if (string.IsNullOrWhiteSpace(first.Name) || string.IsNullOrWhiteSpace(second.Name))
            return "Each input profile needs a name.";
        if (first.Name.Length > InputProfile.MaxNameLength || second.Name.Length > InputProfile.MaxNameLength)
            return $"Profile names can contain at most {InputProfile.MaxNameLength} characters.";
        if (string.Equals(first.Name, second.Name, StringComparison.OrdinalIgnoreCase))
            return "Input profile names must be unique.";
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

    private static int TranscribeModelKindIndex(TranscribeModelKind modelKind) => modelKind switch
    {
        TranscribeModelKind.Gpt4oTranscribe => 0,
        TranscribeModelKind.Gpt4oMiniTranscribe => 1,
        TranscribeModelKind.Gpt4oTranscribeDiarize => 2,
        _ => 3,
    };

    private static void LoadProfileControls(
        InputProfile profile,
        TextBox name,
        ComboBox key,
        ComboBox mode,
        ComboBox overlay)
    {
        name.Text = profile.Name;
        key.SelectedValue = profile.PttKey;
        mode.SelectedIndex = profile.PttMode == PttMode.Toggle ? 1 : 0;
        overlay.SelectedIndex = profile.OverlayPosition == OverlayPosition.Bottom ? 1 : 0;
    }

    private static void CollectProfileControls(
        InputProfile profile,
        TextBox name,
        ComboBox key,
        ComboBox mode,
        ComboBox overlay)
    {
        profile.Name = name.Text.Trim();
        profile.PttKey = key.SelectedValue as string ?? profile.PttKey;
        profile.PttMode = mode.SelectedIndex == 1 ? PttMode.Toggle : PttMode.Hold;
        profile.OverlayPosition = overlay.SelectedIndex == 1
            ? OverlayPosition.Bottom
            : OverlayPosition.Top;
    }

    private static string EngineDisplay(SpeechEngineKind engine) => engine switch
    {
        SpeechEngineKind.Azure => "Azure Speech",
        SpeechEngineKind.GptTranscribe => "gpt-4o-transcribe",
        SpeechEngineKind.FunAsr => "FunASR (local)",
        _ => "Windows dictation",
    };

    private static string SelectionDisplay(AppSettings settings, string localModelId) =>
        settings.Engine == SpeechEngineKind.FunAsr
            ? $"{FunAsrModelCatalog.Get(FunAsrModelCatalog.NormalizeId(localModelId)).DisplayName} (local)"
            : VocabularyEngineDisplay(settings.Engine, settings.TranscribeModelKind);

    private static string VocabularyEngineDisplay(
        SpeechEngineKind engine,
        TranscribeModelKind modelKind) => engine switch
        {
            SpeechEngineKind.GptTranscribe => modelKind switch
            {
                TranscribeModelKind.Gpt4oTranscribe => "GPT-4o Transcribe",
                TranscribeModelKind.Gpt4oMiniTranscribe => "GPT-4o Mini Transcribe",
                TranscribeModelKind.Gpt4oTranscribeDiarize => "GPT-4o Transcribe Diarize",
                _ => "Other / unknown",
            },
            _ => EngineDisplay(engine),
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

    private static long LocalPackageSize(FunAsrModelDefinition model) =>
        FunAsrModelCatalog.Runtime.Size + FunAsrModelCatalog.Vad.Size + model.DownloadSize;

    private static string RuntimeBuild =>
        FunAsrModelCatalog.Runtime.RelativePath.Contains("avx2", StringComparison.OrdinalIgnoreCase)
            ? "AVX2 CPU build"
            : "Compatible x64 build";

    private static string ProgressText(
        FunAsrModelDefinition model, FunAsrInstallProgress? progress) => progress?.Stage switch
    {
        FunAsrInstallStage.Downloading when !string.IsNullOrWhiteSpace(progress.Artifact) =>
            $"Downloading package: {PackageName(model, progress.Artifact)}",
        FunAsrInstallStage.Downloading => "Preparing download",
        FunAsrInstallStage.Verifying => $"Verifying package: {PackageName(model, progress.Artifact)}",
        FunAsrInstallStage.Testing => "Testing installed package",
        _ => "Installing",
    };

    private static string ProgressDetailText(FunAsrInstallProgress? progress)
    {
        if (progress?.TotalBytes is not > 0)
            return string.Empty;
        double percent = Math.Clamp(progress.DownloadedBytes * 100d / progress.TotalBytes.Value, 0, 100);
        string artifact = string.IsNullOrWhiteSpace(progress.Artifact) ? string.Empty : $" · {progress.Artifact}";
        return $"Overall: {FormatTransferSize(progress.DownloadedBytes)} of "
            + $"{FormatTransferSize(progress.TotalBytes.Value)} · {percent:F0}%{artifact}";
    }

    private static string PackageName(FunAsrModelDefinition model, string artifact)
    {
        if (artifact.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return $"FunASR runtime ({RuntimeBuild})";
        if (artifact.Contains("vad", StringComparison.OrdinalIgnoreCase))
            return "voice activity detector";
        return $"{model.DisplayName} model";
    }

    private static string FormatTransferSize(long size) => size >= 1_000_000_000
        ? $"{size / 1_000_000_000d:F2} GB"
        : $"{size / 1_000_000d:F1} MB";

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
        && left.ActiveProfileId == right.ActiveProfileId
        && ProfilesEqual(left.Profiles, right.Profiles)
        && left.Engine == right.Engine
        && left.FunAsrModelId == right.FunAsrModelId
        && left.AzureKey == right.AzureKey
        && left.AzureRegion == right.AzureRegion
        && left.AzureAuthMode == right.AzureAuthMode
        && left.AzureEndpoint == right.AzureEndpoint
        && left.AzureTenantId == right.AzureTenantId
        && left.TranscribeEndpoint == right.TranscribeEndpoint
        && left.TranscribeModelKind == right.TranscribeModelKind
        && left.TranscribeModel == right.TranscribeModel
        && left.RecognitionVocabulary.SequenceEqual(right.RecognitionVocabulary)
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

    private static bool ProfilesEqual(InputProfile[] left, InputProfile[] right) =>
        left.Length == right.Length
        && left.Zip(right).All(pair =>
            pair.First.Id == pair.Second.Id
            && pair.First.Name == pair.Second.Name
            && pair.First.PttKey == pair.Second.PttKey
            && pair.First.PttMode == pair.Second.PttMode
            && pair.First.OverlayPosition == pair.Second.OverlayPosition);

    private sealed record ModelRow(
        TextBlock Selected,
        TextBlock Status,
        TextBlock ProgressDetail,
        ProgressBar Progress,
        StackPanel Actions);
}
