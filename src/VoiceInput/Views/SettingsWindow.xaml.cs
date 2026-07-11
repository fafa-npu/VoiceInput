using System.Windows;
using VoiceInput.Models;
using VoiceInput.Services;

namespace VoiceInput.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _draft;
    private readonly Action<AppSettings> _onSave;
    private readonly LlmRefiner _refiner = new();

    public SettingsWindow(AppSettings current, Action<AppSettings> onSave)
    {
        InitializeComponent();
        _draft = current.Clone();
        _onSave = onSave;

        EngineCombo.SelectedIndex = _draft.Engine switch
        {
            SpeechEngineKind.Azure => 1,
            SpeechEngineKind.GptTranscribe => 2,
            _ => 0,
        };
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
        UpdateFieldVisibility();
    }

    private void OnEngineChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateFieldVisibility();

    private void OnAzureAuthChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateFieldVisibility();

    private void OnTranscribeAuthChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateFieldVisibility();

    /// <summary>Show only the fields relevant to the selected engine and auth mode.</summary>
    private void UpdateFieldVisibility()
    {
        bool azure = EngineCombo.SelectedIndex == 1;
        bool transcribe = EngineCombo.SelectedIndex == 2;
        bool azureEntra = AzureAuthCombo.SelectedIndex == 1;
        bool transcribeEntra = TranscribeAuthCombo.SelectedIndex == 1;

        Show(AzureAuthLabel, AzureAuthCombo, azure);
        Show(AzureKeyLabel, AzureKeyBox, azure && !azureEntra);
        Show(AzureRegionLabel, AzureRegionBox, azure && !azureEntra);
        Show(AzureEndpointLabel, AzureEndpointBox, azure && azureEntra);
        Show(AzureTenantLabel, AzureTenantIdBox, azure && azureEntra);

        Show(TranscribeAuthLabel, TranscribeAuthCombo, transcribe);
        Show(TranscribeEndpointLabel, TranscribeEndpointBox, transcribe);
        Show(TranscribeModelLabel, TranscribeModelBox, transcribe);
        Show(TranscribeApiKeyLabel, TranscribeApiKeyBox, transcribe && !transcribeEntra);
        Show(TranscribeTenantLabel, TranscribeTenantIdBox, transcribe && transcribeEntra);
    }

    private static void Show(UIElement label, UIElement field, bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        label.Visibility = v;
        field.Visibility = v;
    }

    private void Collect()
    {
        _draft.Engine = EngineCombo.SelectedIndex switch
        {
            1 => SpeechEngineKind.Azure,
            2 => SpeechEngineKind.GptTranscribe,
            _ => SpeechEngineKind.Windows,
        };
        _draft.AzureAuthMode = AzureAuthCombo.SelectedIndex == 1 ? AzureAuthMode.EntraId : AzureAuthMode.Key;
        _draft.TranscribeAuthMode = TranscribeAuthCombo.SelectedIndex == 1 ? AzureAuthMode.EntraId : AzureAuthMode.Key;
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
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        Collect();
        StatusText.Text = "Testing…";
        TestButton.IsEnabled = false;
        var (ok, message) = await _refiner.TestAsync(_draft);
        StatusText.Foreground = ok ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Firebrick;
        StatusText.Text = message;
        TestButton.IsEnabled = true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Collect();
        string? validation = ValidateDraft();
        if (validation is not null)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = validation;
            return;
        }
        _onSave(_draft);
        Close();
    }

    private string? ValidateDraft()
    {
        if (_draft.Engine == SpeechEngineKind.Azure)
        {
            if (_draft.AzureAuthMode == AzureAuthMode.Key &&
                (string.IsNullOrWhiteSpace(_draft.AzureKey) || string.IsNullOrWhiteSpace(_draft.AzureRegion)))
                return "Azure Speech key authentication requires both Key and Region.";
            if (_draft.AzureAuthMode == AzureAuthMode.EntraId && !ValidEndpoint(_draft.AzureEndpoint))
                return "Azure Speech Entra authentication requires a valid HTTPS Endpoint.";
        }
        if (_draft.Engine == SpeechEngineKind.GptTranscribe)
        {
            if (!ValidEndpoint(_draft.TranscribeEndpoint) || string.IsNullOrWhiteSpace(_draft.TranscribeModel))
                return "Foundry transcription requires a valid HTTPS Endpoint and Deployment.";
            if (_draft.TranscribeAuthMode == AzureAuthMode.Key && string.IsNullOrWhiteSpace(_draft.TranscribeApiKey))
                return "Foundry key authentication requires an API Key.";
        }
        if (_draft.LlmEnabled &&
            (!ValidEndpoint(_draft.LlmBaseUrl) || string.IsNullOrWhiteSpace(_draft.LlmModel)))
            return "LLM refinement requires a valid HTTPS Base URL and Model.";
        return null;
    }

    private static bool ValidEndpoint(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
