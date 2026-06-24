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

        EngineCombo.SelectedIndex = _draft.Engine == SpeechEngineKind.Azure ? 1 : 0;
        AzureKeyBox.Text = _draft.AzureKey;
        AzureRegionBox.Text = _draft.AzureRegion;
        LlmEnabledBox.IsChecked = _draft.LlmEnabled;
        LlmBaseUrlBox.Text = _draft.LlmBaseUrl;
        LlmApiKeyBox.Text = _draft.LlmApiKey;
        LlmModelBox.Text = _draft.LlmModel;
    }

    private void Collect()
    {
        _draft.Engine = EngineCombo.SelectedIndex == 1 ? SpeechEngineKind.Azure : SpeechEngineKind.Windows;
        _draft.AzureKey = AzureKeyBox.Text.Trim();
        _draft.AzureRegion = AzureRegionBox.Text.Trim();
        _draft.LlmEnabled = LlmEnabledBox.IsChecked == true;
        _draft.LlmBaseUrl = LlmBaseUrlBox.Text.Trim();
        _draft.LlmApiKey = LlmApiKeyBox.Text;     // not trimmed: keys may contain edge whitespace intentionally
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
        _onSave(_draft);
        Close();
    }
}
