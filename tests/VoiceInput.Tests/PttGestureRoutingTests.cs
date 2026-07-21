using VoiceInput.Models;
using VoiceInput.Services;
using VoiceInput.Views;

namespace VoiceInput.Tests;

public sealed class PttGestureRoutingTests
{
    [Fact]
    public void SettingsSavePreservesAConcurrentOnboardingModeChange()
    {
        Assert.Equal(
            PttMode.Toggle,
            AppController.ResolvePttModeAfterSettingsSave(
                current: PttMode.Toggle,
                settingsOpenedWith: PttMode.Hold,
                submitted: PttMode.Hold));
    }

    [Fact]
    public void SettingsSavePreservesAConcurrentProfileSwitch()
    {
        Assert.Equal(
            InputProfile.Profile2Id,
            AppController.ResolveActiveProfileAfterSettingsSave(
                current: InputProfile.Profile2Id,
                settingsOpenedWith: InputProfile.Profile1Id,
                submitted: InputProfile.Profile1Id));
    }

    [Theory]
    [InlineData(FirstRunCompletionChoice.DefaultLocal, SpeechEngineKind.GptTranscribe, "paraformer-zh-q8", SpeechEngineKind.FunAsr, FunAsrModelCatalog.Qwen3AsrId)]
    [InlineData(FirstRunCompletionChoice.Configured, SpeechEngineKind.GptTranscribe, "paraformer-zh-q8", SpeechEngineKind.GptTranscribe, "paraformer-zh-q8")]
    [InlineData(FirstRunCompletionChoice.WindowsFallback, SpeechEngineKind.FunAsr, "paraformer-zh-q8", SpeechEngineKind.Windows, "paraformer-zh-q8")]
    public void OnboardingCompletionPreservesAnExplicitlyConfiguredEngine(
        object choice,
        SpeechEngineKind currentEngine,
        string currentModel,
        SpeechEngineKind expectedEngine,
        string expectedModel)
    {
        (SpeechEngineKind engine, string model) = AppController.ResolveOnboardingRecognition(
            (FirstRunCompletionChoice)Convert.ToInt32(choice),
            currentEngine,
            currentModel);

        Assert.Equal(expectedEngine, engine);
        Assert.Equal(expectedModel, model);
    }

    [Theory]
    [InlineData(PttMode.Hold, PttGesture.Engaged, false, DictationSessionState.Idle, PttGestureAction.Start)]
    [InlineData(PttMode.Hold, PttGesture.Engaged, false, DictationSessionState.Transcribing, PttGestureAction.Busy)]
    [InlineData(PttMode.Hold, PttGesture.Engaged, false, DictationSessionState.Refining, PttGestureAction.Busy)]
    [InlineData(PttMode.Hold, PttGesture.Engaged, false, DictationSessionState.Injecting, PttGestureAction.Busy)]
    [InlineData(PttMode.Hold, PttGesture.Released, true, DictationSessionState.Listening, PttGestureAction.Stop)]
    [InlineData(PttMode.Hold, PttGesture.RecoveredRelease, true, DictationSessionState.Listening, PttGestureAction.Stop)]
    [InlineData(PttMode.Hold, PttGesture.Cancelled, true, DictationSessionState.Listening, PttGestureAction.Cancel)]
    [InlineData(PttMode.Toggle, PttGesture.Engaged, false, DictationSessionState.Idle, PttGestureAction.None)]
    [InlineData(PttMode.Toggle, PttGesture.Cancelled, true, DictationSessionState.Listening, PttGestureAction.None)]
    [InlineData(PttMode.Toggle, PttGesture.Released, false, DictationSessionState.Idle, PttGestureAction.Start)]
    [InlineData(PttMode.Toggle, PttGesture.Released, false, DictationSessionState.Failed, PttGestureAction.Start)]
    [InlineData(PttMode.Toggle, PttGesture.Released, false, DictationSessionState.Cancelled, PttGestureAction.Start)]
    [InlineData(PttMode.Toggle, PttGesture.Released, true, DictationSessionState.Starting, PttGestureAction.Stop)]
    [InlineData(PttMode.Toggle, PttGesture.Released, true, DictationSessionState.Listening, PttGestureAction.Stop)]
    [InlineData(PttMode.Toggle, PttGesture.RecoveredRelease, false, DictationSessionState.Idle, PttGestureAction.Start)]
    [InlineData(PttMode.Toggle, PttGesture.RecoveredRelease, true, DictationSessionState.Starting, PttGestureAction.Stop)]
    [InlineData(PttMode.Toggle, PttGesture.RecoveredRelease, true, DictationSessionState.Listening, PttGestureAction.Stop)]
    [InlineData(PttMode.Toggle, PttGesture.Released, false, DictationSessionState.Transcribing, PttGestureAction.Busy)]
    [InlineData(PttMode.Toggle, PttGesture.Released, false, DictationSessionState.Refining, PttGestureAction.Busy)]
    [InlineData(PttMode.Toggle, PttGesture.Released, false, DictationSessionState.Injecting, PttGestureAction.Busy)]
    public void ResolvesConfiguredGesture(
        PttMode mode,
        object gesture,
        bool dictating,
        object state,
        object expected)
    {
        Assert.Equal(
            (PttGestureAction)Convert.ToInt32(expected),
            AppController.ResolvePttGesture(
                mode,
                (PttGesture)Convert.ToInt32(gesture),
                dictating,
            (DictationSessionState)Convert.ToInt32(state)));
    }

    [Theory]
    [InlineData(false, DictationSessionState.Idle, true)]
    [InlineData(false, DictationSessionState.Cancelled, true)]
    [InlineData(false, DictationSessionState.Failed, true)]
    [InlineData(true, DictationSessionState.Listening, false)]
    [InlineData(false, DictationSessionState.Starting, false)]
    [InlineData(false, DictationSessionState.Listening, false)]
    [InlineData(false, DictationSessionState.Transcribing, false)]
    [InlineData(false, DictationSessionState.Refining, false)]
    [InlineData(false, DictationSessionState.Injecting, false)]
    public void ProfileSwitchOnlyRunsOutsideRecordingAndProcessing(
        bool dictating,
        object state,
        bool expected)
    {
        Assert.Equal(
            expected,
            AppController.CanSwitchProfile(dictating, (DictationSessionState)Convert.ToInt32(state)));
    }

    [Theory]
    [InlineData(true, DictationSessionState.Starting, true)]
    [InlineData(true, DictationSessionState.Listening, true)]
    [InlineData(false, DictationSessionState.Starting, false)]
    [InlineData(false, DictationSessionState.Listening, false)]
    [InlineData(false, DictationSessionState.Idle, false)]
    [InlineData(false, DictationSessionState.Cancelled, false)]
    [InlineData(true, DictationSessionState.Transcribing, false)]
    [InlineData(true, DictationSessionState.Refining, false)]
    [InlineData(true, DictationSessionState.Injecting, false)]
    public void EscapeOnlyCancelsAnActiveRecordingSession(
        bool dictating,
        object state,
        bool expected)
    {
        Assert.Equal(
            expected,
            AppController.ShouldCancelForEscape(
                dictating,
                (DictationSessionState)Convert.ToInt32(state)));
    }

    [Theory]
    [InlineData(InputProfile.Profile1Id, InputProfile.Profile2Id)]
    [InlineData(InputProfile.Profile2Id, InputProfile.Profile1Id)]
    [InlineData("unknown", InputProfile.Profile2Id)]
    public void ProfileSwitchCyclesBetweenFixedProfiles(string current, string expected) =>
        Assert.Equal(expected, AppController.NextProfileId(current));
}
