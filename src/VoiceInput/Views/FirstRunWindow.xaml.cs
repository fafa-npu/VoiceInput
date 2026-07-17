using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VoiceInput.Models;
using VoiceInput.Services;

namespace VoiceInput.Views;

internal enum FirstRunCompletionChoice
{
    DefaultLocal,
    Configured,
    WindowsFallback,
}

internal sealed record FirstRunWindowActions(
    bool RecognitionReady,
    bool UseConfiguredRecognition,
    string RecognitionSummary,
    Func<Action<FunAsrInstallProgress>, Task> InstallLocalModel,
    Action CancelLocalModelInstall,
    Func<bool> ConfirmWindowsFallback,
    Func<bool> IsPttGestureChorded,
    Action<PttMode> SetPttMode,
    Func<FirstRunCompletionChoice, bool> Complete,
    Action OpenSettings);

public partial class FirstRunWindow : Window
{
    private enum PracticeStage { AwaitingFocus, ReadyToTalk, Listening, Processing, Complete }

    private readonly FirstRunWindowActions _actions;
    private readonly Key _pttKey;
    private PttMode _pttMode;
    private bool _previewListening;
    private bool _previewKeyAccepted;
    private bool _recognitionReady;
    private bool _installing;
    private bool _loadingModeSelection;
    private string? _installError;

    internal FirstRunWindow(
        string pttKey,
        string pttDisplay,
        PttMode pttMode,
        FirstRunWindowActions actions)
    {
        InitializeComponent();
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _recognitionReady = actions.RecognitionReady;
        _pttMode = pttMode;
        _pttKey = pttKey switch
        {
            "LeftCtrl" => Key.LeftCtrl,
            "CapsLock" => Key.CapsLock,
            "RightAlt" => Key.RightAlt,
            "RightShift" => Key.RightShift,
            _ => Key.RightCtrl,
        };

        PttKeyText.Text = pttDisplay;
        _loadingModeSelection = true;
        HoldModeRadio.IsChecked = _pttMode == PttMode.Hold;
        ToggleModeRadio.IsChecked = _pttMode == PttMode.Toggle;
        _loadingModeSelection = false;
        ApplyPttModeText();
        SetPracticeStage(PracticeStage.AwaitingFocus);
        RefreshLocalModelSetup();
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    private void OnPttModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingModeSelection)
            return;

        PttMode selected = ToggleModeRadio.IsChecked == true ? PttMode.Toggle : PttMode.Hold;
        if (selected == _pttMode)
            return;

        _pttMode = selected;
        _actions.SetPttMode(selected);
        _previewListening = false;
        _previewKeyAccepted = false;
        ResetKeycap();
        ListeningBars.Visibility = Visibility.Collapsed;
        PracticeTextBox.Clear();
        ApplyPttModeText();
        SetPracticeStage(PracticeStage.AwaitingFocus);
        if (_recognitionReady)
        {
            PracticeTextBox.Focus();
        }
        else
        {
            SetPracticeStatus(
                "说话方式已设置",
                _actions.UseConfiguredRecognition
                    ? "完成识别引擎配置后即可按所选方式试用。"
                    : "下载本地模型后即可按所选方式试用。",
                "BlueBrush");
        }
    }

    private void ApplyPttModeText()
    {
        string key = PttKeyText.Text;
        if (_pttMode == PttMode.Toggle)
        {
            PracticePttStepText.Text = $"按一下 {key} 开始";
            PracticePttStepDetailText.Text = "松开按键后 gujiguji 开始聆听";
            PracticeReleaseStepText.Text = $"再按一下 {key} 结束";
            PracticeReleaseStepDetailText.Text = "识别完成后文字会回到当前光标位置";
            CompletionTalkStepText.Text = "按一下开始";
            CompletionSubmitStepText.Text = "再按一下输入";
            CompletionSummaryText.Text =
                $"在其他应用中点击文本框，按一下 {key} 开始聆听，再按一下结束并输入。";
            AutomationProperties.SetHelpText(
                PracticeTextBox,
                $"点击这里，按一下 {key} 开始聆听，再按一下结束并输入。");
        }
        else
        {
            PracticePttStepText.Text = $"按住 {key} 说话";
            PracticePttStepDetailText.Text = "按住时 gujiguji 才会聆听";
            PracticeReleaseStepText.Text = "松开即可输入";
            PracticeReleaseStepDetailText.Text = "文字会回到刚才的光标位置";
            CompletionTalkStepText.Text = "按住说话";
            CompletionSubmitStepText.Text = "松开输入";
            CompletionSummaryText.Text =
                $"在其他应用中点击文本框，按住 {key} 说话，松开后文字会输入到原来的光标位置。";
            AutomationProperties.SetHelpText(
                PracticeTextBox,
                $"点击这里，按住 {key} 说话，松开后 gujiguji 会把结果输入到这个文本框。");
        }

        string mode = _pttMode == PttMode.Toggle ? "按一下开始 / 再按一下结束" : "按住说话";
        CurrentSummaryText.Text = $"{_actions.RecognitionSummary} · {key} · {mode}";
    }

    private void SetPracticeStage(PracticeStage stage)
    {
        int completedThrough = stage switch
        {
            PracticeStage.ReadyToTalk or PracticeStage.Listening => 1,
            PracticeStage.Processing => 2,
            PracticeStage.Complete => 3,
            _ => 0,
        };
        int activeStep = stage switch
        {
            PracticeStage.AwaitingFocus => 1,
            PracticeStage.ReadyToTalk or PracticeStage.Listening => 2,
            PracticeStage.Processing => 3,
            _ => 0,
        };

        UpdatePracticeStep(FocusStep, FocusStepBadge, FocusStepNumberText, 1, activeStep, completedThrough);
        UpdatePracticeStep(TalkStep, TalkStepBadge, TalkStepNumberText, 2, activeStep, completedThrough);
        UpdatePracticeStep(ReleaseStep, ReleaseStepBadge, ReleaseStepNumberText, 3, activeStep, completedThrough);
    }

    private void UpdatePracticeStep(
        Border step,
        Border badge,
        TextBlock number,
        int index,
        int activeStep,
        int completedThrough)
    {
        bool complete = index <= completedThrough;
        bool active = index == activeStep;
        step.Background = active ? Brush("BlueSoftBrush") : Brushes.Transparent;
        step.BorderBrush = active
            ? Brush("BlueBrush")
            : complete ? Brush("SuccessBrush") : Brushes.Transparent;
        badge.Background = complete
            ? Brush("SuccessBrush")
            : active ? Brush("BlueBrush") : Brushes.Transparent;
        badge.BorderBrush = active || complete ? Brushes.Transparent : Brush("LineBrush");
        badge.BorderThickness = active || complete ? new Thickness(0) : new Thickness(1);
        number.Text = complete ? "✓" : index.ToString();
        number.Foreground = active || complete ? Brushes.White : Brush("MutedTextBrush");
        AutomationProperties.SetName(step, complete
            ? $"步骤 {index}，已完成"
            : active ? $"步骤 {index}，当前步骤" : $"步骤 {index}，未开始");
    }

    private async void OnInstallLocalModelClick(object sender, RoutedEventArgs e)
    {
        if (_actions.UseConfiguredRecognition || _installing || _recognitionReady)
            return;

        _installing = true;
        _installError = null;
        LocalModelProgressBar.Value = 0;
        LocalModelProgressBar.IsIndeterminate = true;
        RefreshLocalModelSetup();
        string? error = null;
        try
        {
            await _actions.InstallLocalModel(OnLocalModelProgress);
        }
        catch (OperationCanceledException)
        {
            error = "下载已取消，可以随时重新开始。";
        }
        catch (Exception exception)
        {
            error = $"安装失败：{exception.Message}";
        }

        if (Dispatcher.HasShutdownStarted)
            return;
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _installError = error;
                _recognitionReady = error is null;
                _installing = false;
                if (_recognitionReady)
                {
                    SetPracticeStatus("本地模型已就绪", $"点击文本框，按住 {PttKeyText.Text} 开始演练。", "SuccessBrush");
                }
                RefreshLocalModelSetup();
                if (_recognitionReady)
                    PracticeTextBox.Focus();
            });
        }
        catch (TaskCanceledException) when (Dispatcher.HasShutdownStarted)
        {
        }
    }

    private void OnCancelLocalModelInstallClick(object sender, RoutedEventArgs e)
    {
        CancelLocalModelInstallButton.IsEnabled = false;
        LocalModelStatusText.Text = "正在取消下载…";
        _actions.CancelLocalModelInstall();
    }

    private void OnLocalModelProgress(FunAsrInstallProgress progress)
    {
        if (Dispatcher.HasShutdownStarted)
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnLocalModelProgress(progress));
            return;
        }
        if (progress.ModelId != FunAsrModelCatalog.DefaultId)
            return;

        string package = string.IsNullOrWhiteSpace(progress.Artifact)
            ? FunAsrModelCatalog.Default.DisplayName
            : System.IO.Path.GetFileName(progress.Artifact);
        LocalModelStatusText.Text = progress.Stage switch
        {
            FunAsrInstallStage.Downloading => $"正在下载 {package}",
            FunAsrInstallStage.Verifying => $"正在校验 {package}",
            FunAsrInstallStage.Testing => "正在测试本地识别引擎",
            FunAsrInstallStage.Installed => "SenseVoiceSmall 已安装并通过测试。",
            FunAsrInstallStage.Failed => $"安装失败：{progress.Error}",
            _ => LocalModelStatusText.Text,
        };

        if (progress.TotalBytes is > 0)
        {
            LocalModelProgressBar.IsIndeterminate = false;
            LocalModelProgressBar.Value = Math.Clamp(
                progress.DownloadedBytes * 100d / progress.TotalBytes.Value, 0, 100);
            LocalModelProgressText.Text =
                $"{FormatSize(progress.DownloadedBytes)} / {FormatSize(progress.TotalBytes.Value)}";
        }
        else
        {
            LocalModelProgressBar.IsIndeterminate = true;
            LocalModelProgressText.Text = package;
        }
    }

    private void RefreshLocalModelSetup()
    {
        PracticeTextBox.IsEnabled = _recognitionReady && !_installing;
        ContinueButton.IsEnabled = PracticeTextBox.Text.Length > 0 && PracticeTextBox.IsEnabled;
        SkipButton.IsEnabled = !_installing;
        CancelLocalModelInstallButton.IsEnabled = true;

        if (_actions.UseConfiguredRecognition)
        {
            LocalModelInlineStatusText.Text = _recognitionReady
                ? _actions.RecognitionSummary
                : "所选识别引擎尚未就绪";
            LocalModelInlineStatusText.Foreground = Brush(_recognitionReady ? "SuccessBrush" : "AmberBrush");
            LocalModelTitleText.Text = _recognitionReady ? "识别引擎已配置" : "需要完成识别引擎配置";
            LocalModelStatusText.Text = _recognitionReady
                ? $"{_actions.RecognitionSummary}，可以直接在下方试用。"
                : $"{_actions.RecognitionSummary}。请打开完整设置检查引擎或模型。";
            LocalModelSetupPanel.Visibility = _recognitionReady ? Visibility.Collapsed : Visibility.Visible;
            InstallLocalModelButton.Visibility = Visibility.Collapsed;
            CancelLocalModelInstallButton.Visibility = Visibility.Collapsed;
            LocalModelProgressBar.Visibility = Visibility.Collapsed;
            LocalModelProgressText.Visibility = Visibility.Collapsed;
            SkipButton.Content = _recognitionReady ? "跳过演练" : "改用 Windows 听写（准确率较低）";
            AutomationProperties.SetName(
                SkipButton,
                _recognitionReady ? "跳过首次使用演练" : "改用准确率较低的 Windows 听写");
            return;
        }

        if (_recognitionReady)
        {
            LocalModelSetupPanel.Visibility = Visibility.Collapsed;
            LocalModelInlineStatusText.Text = "SenseVoiceSmall 已就绪";
            LocalModelInlineStatusText.Foreground = Brush("SuccessBrush");
            LocalModelTitleText.Text = "本地模型已就绪";
            LocalModelStatusText.Text = "SenseVoiceSmall 将在本机完成识别，语音不会上传。";
            InstallLocalModelButton.Visibility = Visibility.Collapsed;
            CancelLocalModelInstallButton.Visibility = Visibility.Collapsed;
            LocalModelProgressBar.Visibility = Visibility.Collapsed;
            LocalModelProgressText.Visibility = Visibility.Collapsed;
            SkipButton.Content = "跳过演练";
            AutomationProperties.SetName(SkipButton, "跳过首次使用演练");
            return;
        }

        LocalModelSetupPanel.Visibility = Visibility.Visible;
        LocalModelInlineStatusText.Text = _installing ? "正在安装本地模型" : "先完成上方模型安装";
        LocalModelInlineStatusText.Foreground = Brush("AmberBrush");
        LocalModelTitleText.Text = _installing ? "正在准备本地识别" : "使用本地 FunASR（推荐）";
        LocalModelStatusText.Text = _installError
            ?? $"下载 SenseVoiceSmall 与 CPU 运行时，共约 {FormatSize(DefaultPackageSize)}。";
        InstallLocalModelButton.Content = _installError is null ? "下载并使用" : "重新下载";
        InstallLocalModelButton.Visibility = _installing ? Visibility.Collapsed : Visibility.Visible;
        CancelLocalModelInstallButton.Visibility = _installing ? Visibility.Visible : Visibility.Collapsed;
        LocalModelProgressBar.Visibility = _installing ? Visibility.Visible : Visibility.Collapsed;
        LocalModelProgressText.Visibility = _installing ? Visibility.Visible : Visibility.Collapsed;
        if (_installing && LocalModelProgressBar.Value == 0)
        {
            LocalModelProgressBar.IsIndeterminate = true;
            LocalModelProgressText.Text = "正在准备下载…";
        }
        SkipButton.Content = "改用 Windows 听写（准确率较低）";
        AutomationProperties.SetName(SkipButton, "改用准确率较低的 Windows 听写");
    }

    private static long DefaultPackageSize => FunAsrModelCatalog.Runtime.Size
        + FunAsrModelCatalog.Vad.Size
        + FunAsrModelCatalog.Default.DownloadSize;

    private static string FormatSize(long size) => size >= 1_000_000_000
        ? $"{size / 1_000_000_000d:F2} GB"
        : $"{size / 1_000_000d:F1} MB";

    private void OnPracticeTextBoxGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (PracticeTextBox.Text.Length == 0)
        {
            SetPracticeStage(PracticeStage.ReadyToTalk);
            SetPracticeStatus("文本框已聚焦", PracticeStartInstruction(), "BlueBrush");
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsPttKey(e))
        {
            if (_previewKeyAccepted)
                CancelPreviewPttCycle();
            return;
        }
        if (e.IsRepeat)
            return;
        _previewKeyAccepted = false;

        if (!_recognitionReady)
        {
            SetPracticeStage(PracticeStage.AwaitingFocus);
            SetPracticeStatus(
                _actions.UseConfiguredRecognition ? "先完成识别引擎配置" : "先安装本地模型",
                _actions.UseConfiguredRecognition
                    ? "请在完整设置中确认所选引擎或模型已经可用。"
                    : "下载完成后即可在这里试用语音输入。",
                "AmberBrush");
            return;
        }

        if (!PracticeTextBox.IsKeyboardFocusWithin)
        {
            SetPracticeStage(PracticeStage.AwaitingFocus);
            SetPracticeStatus("先确认光标位置", "请先点击上面的文本框，再使用说话键。", "AmberBrush");
            return;
        }

        if (_actions.IsPttGestureChorded())
        {
            CancelPreviewPttCycle();
            return;
        }

        _previewKeyAccepted = true;
        KeycapBorder.Background = Brush("AccentSoftBrush");
        KeycapBorder.BorderBrush = Brush("AccentBrush");
        KeycapBorder.BorderThickness = new Thickness(1);
        if (_pttMode == PttMode.Toggle)
        {
            SetPracticeStatus(
                _previewListening ? "正在聆听" : "准备开始",
                _previewListening
                    ? $"松开 {PttKeyText.Text} 后结束并识别。"
                    : $"松开 {PttKeyText.Text} 后开始聆听。",
                _previewListening ? "AccentBrush" : "BlueBrush");
            return;
        }

        _previewListening = true;
        SetPracticeStage(PracticeStage.Listening);
        ListeningBars.Visibility = Visibility.Visible;
        SetPracticeStatus("正在聆听", "继续按住；说完后松开说话键。", "AccentBrush");
    }

    private void CancelPreviewPttCycle()
    {
        _previewKeyAccepted = false;
        ResetKeycap();
        if (_pttMode == PttMode.Toggle && _previewListening)
        {
            SetPracticeStage(PracticeStage.Listening);
            ListeningBars.Visibility = Visibility.Visible;
            SetPracticeStatus(
                "正在聆听",
                $"组合键不会结束录音；请单独再按一下 {PttKeyText.Text}。",
                "AccentBrush");
            return;
        }

        _previewListening = false;
        ListeningBars.Visibility = Visibility.Collapsed;
        SetPracticeStage(PracticeStage.ReadyToTalk);
        SetPracticeStatus("已忽略组合键", PracticeStartInstruction(), "BlueBrush");
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!IsPttKey(e) || !_previewKeyAccepted)
            return;
        _previewKeyAccepted = false;

        if (_pttMode == PttMode.Toggle)
        {
            ResetKeycap();
            _previewListening = !_previewListening;
            ListeningBars.Visibility = _previewListening ? Visibility.Visible : Visibility.Collapsed;
            if (_previewListening)
            {
                SetPracticeStage(PracticeStage.Listening);
                SetPracticeStatus("正在聆听", $"说完后再按一下 {PttKeyText.Text}。", "AccentBrush");
            }
            else if (PracticeTextBox.Text.Length == 0)
            {
                SetPracticeStage(PracticeStage.Processing);
                SetPracticeStatus("正在处理", "gujiguji 会把识别结果输入到当前光标位置。", "BlueBrush");
            }
            return;
        }

        if (!_previewListening)
            return;
        _previewListening = false;
        ResetKeycap();
        ListeningBars.Visibility = Visibility.Collapsed;
        if (PracticeTextBox.Text.Length == 0)
        {
            SetPracticeStage(PracticeStage.Processing);
            SetPracticeStatus("正在处理", "gujiguji 会把识别结果输入到当前光标位置。", "BlueBrush");
        }
    }

    private void OnPracticeTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        bool hasText = PracticeTextBox.Text.Length > 0;
        ContinueButton.IsEnabled = hasText && _recognitionReady && !_installing;
        if (ContinueButton.IsEnabled)
        {
            _previewListening = false;
            ResetKeycap();
            ListeningBars.Visibility = Visibility.Collapsed;
            SetPracticeStage(PracticeStage.Complete);
            SetPracticeStatus(
                "演练完成，可以继续",
                _pttMode == PttMode.Toggle
                    ? "日常使用也是这样：点击文本框，按一下开始，再按一下输入。"
                    : "日常使用也是这样：点击文本框，按住说话，松开输入。",
                "SuccessBrush");
        }
        else if (!_previewListening)
        {
            bool focused = PracticeTextBox.IsKeyboardFocusWithin;
            SetPracticeStage(focused ? PracticeStage.ReadyToTalk : PracticeStage.AwaitingFocus);
            SetPracticeStatus(
                focused ? "文本框已聚焦" : "先点击上面的文本框",
                focused ? PracticeStartInstruction() : "光标出现后，再使用说话键。",
                focused ? "BlueBrush" : "MutedTextBrush");
        }
    }

    private string PracticeStartInstruction() => _pttMode == PttMode.Toggle
        ? $"按一下 {PttKeyText.Text} 开始，说完后再按一下。"
        : $"现在按住 {PttKeyText.Text}，说一句话，然后松开。";

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

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        if (_recognitionReady)
            ShowCompletionPage();
    }

    private void OnPracticeAgainClick(object sender, RoutedEventArgs e)
    {
        ShowPracticePage();
        PracticeTextBox.Clear();
        SetPracticeStage(PracticeStage.AwaitingFocus);
        PracticeTextBox.Focus();
        SetPracticeStatus("文本框已聚焦", PracticeStartInstruction(), "BlueBrush");
    }

    private void ShowCompletionPage()
    {
        PracticePage.Visibility = Visibility.Collapsed;
        PracticeFooter.Visibility = Visibility.Collapsed;
        CompletionPage.Visibility = Visibility.Visible;
        CompletionFooter.Visibility = Visibility.Visible;
        ProgressSecondHalf.Background = Brush("AccentBrush");
        StepCountText.Text = "2 / 2";
        Title = "gujiguji 已准备好";
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
        Title = "gujiguji 快速设置";
        PracticePage.ScrollToTop();
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        if (!_recognitionReady && !_actions.ConfirmWindowsFallback())
            return;
        CompleteAndClose(_recognitionReady
            ? _actions.UseConfiguredRecognition
                ? FirstRunCompletionChoice.Configured
                : FirstRunCompletionChoice.DefaultLocal
            : FirstRunCompletionChoice.WindowsFallback);
    }

    private void OnFinishClick(object sender, RoutedEventArgs e) => CompleteAndClose(
        _actions.UseConfiguredRecognition
            ? FirstRunCompletionChoice.Configured
            : FirstRunCompletionChoice.DefaultLocal);

    private void CompleteAndClose(FirstRunCompletionChoice choice)
    {
        if (!_actions.Complete(choice))
        {
            SetPracticeStatus("识别引擎尚未就绪", "请完成配置，或暂时使用 Windows 听写。", "AmberBrush");
            return;
        }
        Close();
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        _actions.OpenSettings();
        e.Handled = true;
    }
}
