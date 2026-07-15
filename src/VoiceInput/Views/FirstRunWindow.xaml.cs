using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace VoiceInput.Views;

public partial class FirstRunWindow : Window
{
    private readonly Action _complete;
    private readonly Action _openSettings;
    private readonly Key _pttKey;
    private bool _previewListening;

    internal FirstRunWindow(
        string pttKey,
        string pttDisplay,
        string currentSummary,
        Action complete,
        Action openSettings)
    {
        InitializeComponent();
        _complete = complete ?? throw new ArgumentNullException(nameof(complete));
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _pttKey = pttKey switch
        {
            "LeftCtrl" => Key.LeftCtrl,
            "CapsLock" => Key.CapsLock,
            "RightAlt" => Key.RightAlt,
            "RightShift" => Key.RightShift,
            _ => Key.RightCtrl,
        };

        PttKeyText.Text = pttDisplay;
        PracticePttStepText.Text = $"按住 {pttDisplay} 说话";
        CurrentSummaryText.Text = currentSummary;
        CompletionSummaryText.Text =
            $"在其他应用中点击文本框，按住 {pttDisplay} 说话，松开后文字会输入到原来的光标位置。";
        AutomationProperties.SetHelpText(
            PracticeTextBox,
            $"点击这里，按住 {pttDisplay} 说话，松开后 VoiceInput 会把结果输入到这个文本框。");
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    private void OnPracticeTextBoxGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (PracticeTextBox.Text.Length == 0)
            SetPracticeStatus("文本框已聚焦", $"现在按住 {PttKeyText.Text}，说一句话，然后松开。", "BlueBrush");
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsPttKey(e) || e.IsRepeat)
            return;

        if (!PracticeTextBox.IsKeyboardFocusWithin)
        {
            SetPracticeStatus("先确认光标位置", "请先点击上面的文本框，再按住说话键。", "AmberBrush");
            return;
        }

        _previewListening = true;
        KeycapBorder.Background = Brush("AccentSoftBrush");
        KeycapBorder.BorderBrush = Brush("AccentBrush");
        KeycapBorder.BorderThickness = new Thickness(1);
        ListeningBars.Visibility = Visibility.Visible;
        SetPracticeStatus("正在聆听", "继续按住；说完后松开说话键。", "AccentBrush");
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!IsPttKey(e) || !_previewListening)
            return;

        _previewListening = false;
        ResetKeycap();
        ListeningBars.Visibility = Visibility.Collapsed;
        if (PracticeTextBox.Text.Length == 0)
            SetPracticeStatus("正在处理", "VoiceInput 会把识别结果输入到当前光标位置。", "BlueBrush");
    }

    private void OnPracticeTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        bool hasText = PracticeTextBox.Text.Length > 0;
        ContinueButton.IsEnabled = hasText;
        if (hasText)
        {
            _previewListening = false;
            ResetKeycap();
            ListeningBars.Visibility = Visibility.Collapsed;
            SetPracticeStatus("演练完成，可以继续", "日常使用也是这样：点击文本框，按住说话，松开输入。", "SuccessBrush");
        }
    }

    private void SetPracticeStatus(string title, string detail, string dotBrush)
    {
        PracticeStatusText.Text = title;
        PracticeStatusDetailText.Text = detail;
        PracticeStatusDot.Fill = Brush(dotBrush);
        AutomationProperties.SetName(PracticeStatusText, $"{title}. {detail}");
    }

    private bool IsPttKey(KeyEventArgs e) =>
        (e.Key == Key.System ? e.SystemKey : e.Key) == _pttKey;

    private void ResetKeycap()
    {
        KeycapBorder.Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFB));
        KeycapBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x87, 0x95, 0x9C));
        KeycapBorder.BorderThickness = new Thickness(1, 1, 1, 4);
    }

    private void OnContinueClick(object sender, RoutedEventArgs e) => ShowCompletionPage();

    private void OnPracticeAgainClick(object sender, RoutedEventArgs e)
    {
        ShowPracticePage();
        PracticeTextBox.Clear();
        PracticeTextBox.Focus();
        SetPracticeStatus("文本框已聚焦", $"现在按住 {PttKeyText.Text}，说一句话，然后松开。", "BlueBrush");
    }

    private void ShowCompletionPage()
    {
        PracticePage.Visibility = Visibility.Collapsed;
        PracticeFooter.Visibility = Visibility.Collapsed;
        CompletionPage.Visibility = Visibility.Visible;
        CompletionFooter.Visibility = Visibility.Visible;
        ProgressSecondHalf.Background = Brush("AccentBrush");
        StepCountText.Text = "2 / 2";
        Title = "VoiceInput 已准备好";
        CompletionPage.ScrollToTop();
        FinishButton.Focus();
    }

    private void ShowPracticePage()
    {
        CompletionPage.Visibility = Visibility.Collapsed;
        CompletionFooter.Visibility = Visibility.Collapsed;
        PracticePage.Visibility = Visibility.Visible;
        PracticeFooter.Visibility = Visibility.Visible;
        ProgressSecondHalf.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0xEA, 0xEC));
        StepCountText.Text = "1 / 2";
        Title = "VoiceInput 快速设置";
        PracticePage.ScrollToTop();
    }

    private void OnSkipClick(object sender, RoutedEventArgs e) => CompleteAndClose();

    private void OnFinishClick(object sender, RoutedEventArgs e) => CompleteAndClose();

    private void CompleteAndClose()
    {
        _complete();
        Close();
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        _complete();
        Close();
        _openSettings();
        e.Handled = true;
    }
}
