namespace VoiceInput.Services;

internal enum DictationSessionState
{
    Idle,
    Starting,
    Listening,
    Transcribing,
    Refining,
    Injecting,
    Failed,
    Cancelled,
}

internal sealed class DictationSession : IDisposable
{
    private long _generation;
    private CancellationTokenSource _cts = new();

    public long Generation => Interlocked.Read(ref _generation);
    public CancellationToken Token => _cts.Token;
    public DictationSessionState State { get; private set; } = DictationSessionState.Idle;

    public (long Generation, CancellationToken Token) Begin()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        long generation = Interlocked.Increment(ref _generation);
        State = DictationSessionState.Starting;
        return (generation, _cts.Token);
    }

    public bool IsCurrent(long generation) => Generation == generation;

    public void MoveTo(DictationSessionState state) => State = state;

    public void CompleteProcessing()
    {
        if (State is DictationSessionState.Transcribing or DictationSessionState.Refining or DictationSessionState.Injecting)
            State = DictationSessionState.Idle;
    }

    public void Cancel()
    {
        _cts.Cancel();
        State = DictationSessionState.Cancelled;
    }

    public void Dispose()
    {
        Cancel();
        Interlocked.Increment(ref _generation);
        _cts.Dispose();
    }
}
