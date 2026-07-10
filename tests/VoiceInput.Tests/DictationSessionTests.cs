using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class DictationSessionTests
{
    [Fact]
    public void BeginCancelsPriorGeneration()
    {
        using var session = new DictationSession();
        var first = session.Begin();

        var second = session.Begin();

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(second.Token.IsCancellationRequested);
        Assert.True(session.IsCurrent(second.Generation));
        Assert.False(session.IsCurrent(first.Generation));
        Assert.Equal(DictationSessionState.Starting, session.State);
    }

    [Fact]
    public void CancelMovesStateAndSignalsToken()
    {
        using var session = new DictationSession();
        var current = session.Begin();
        session.MoveTo(DictationSessionState.Listening);

        session.Cancel();

        Assert.True(current.Token.IsCancellationRequested);
        Assert.Equal(DictationSessionState.Cancelled, session.State);
    }
}
