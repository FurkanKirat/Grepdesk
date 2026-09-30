using Avalonia.Controls;
using Avalonia.Threading;
using Grepdesk.Core.Jobs;

namespace Grepdesk.UI.Jobs;

/// <summary>
/// Asks the user about conflicts, one dialog at a time even when several
/// workers hit a conflict together, and remembers an "apply to all" answer.
/// </summary>
internal sealed class DialogConflictResolver(Window owner) : IConflictResolver
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private ConflictChoice? _remembered;

    public async Task<ConflictChoice> ResolveAsync(ConflictInfo conflict, CancellationToken ct)
    {
        if (_remembered is { } early)
            return early;

        await _oneAtATime.WaitAsync(ct);
        try
        {
            // Another worker's question may have set it while we waited.
            if (_remembered is { } remembered)
                return remembered;

            var answer = await Dispatcher.UIThread.InvokeAsync(() => AskAsync(conflict, ct));
            ct.ThrowIfCancellationRequested();

            if (answer.ApplyToAll)
                _remembered = answer.Choice;
            return answer.Choice;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<ConflictAnswer> AskAsync(ConflictInfo conflict, CancellationToken ct)
    {
        var dialog = new ConflictDialog(conflict);
        await using var _ = ct.Register(() => Dispatcher.UIThread.Post(() => dialog.Close()));

        // Closing the dialog with the window's X counts as "skip": the safe choice.
        var answer = await dialog.ShowDialog<ConflictAnswer?>(owner);
        return answer ?? new ConflictAnswer(ConflictChoice.Skip, false);
    }
}
