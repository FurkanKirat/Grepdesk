namespace Grepdesk.Core.Jobs;

/// <summary>
/// Lets the UI pause a running job. Engines call <see cref="WaitIfPaused"/>
/// between buffers; it returns immediately while the gate is open.
/// </summary>
public sealed class PauseGate : IDisposable
{
    private readonly ManualResetEventSlim _open = new(initialState: true);

    public bool IsPaused => !_open.IsSet;

    public void Pause() => _open.Reset();

    public void Resume() => _open.Set();

    public void WaitIfPaused(CancellationToken ct)
    {
        if (!_open.IsSet)
            _open.Wait(ct);
    }

    public void Dispose() => _open.Dispose();
}
