using System.Collections.Concurrent;

namespace Grepdesk.Core.Jobs;

public enum JobErrorKind
{
    /// <summary>Generic I/O failure; <see cref="JobError.Detail"/> carries the OS message.</summary>
    Io,
    /// <summary>A zip entry whose path would land outside the destination folder (Zip Slip).</summary>
    UnsafePath,
    /// <summary>A password-protected zip entry; .NET cannot decrypt these.</summary>
    Encrypted,
    /// <summary>The file was locked or in use, even after retrying.</summary>
    Locked,
    /// <summary>A folder can't be copied or moved into itself or one of its subfolders.</summary>
    IntoItself,
    /// <summary>A symbolic link or junction; not followed, to avoid copying loops.</summary>
    LinkSkipped,
    /// <summary>A source path that no longer exists.</summary>
    NotFound
}

public sealed record JobError(string Path, JobErrorKind Kind, string? Detail = null);

/// <summary>Everything an engine needs from the job that runs it.</summary>
public sealed class JobContext(IConflictResolver conflicts, CancellationToken token)
{
    private readonly ConcurrentQueue<JobError> _errors = new();

    public JobProgress Progress { get; } = new();
    public PauseGate Pause { get; } = new();
    public IConflictResolver Conflicts { get; } = conflicts;
    public CancellationToken Token { get; } = token;

    public IReadOnlyCollection<JobError> Errors => _errors;

    public void ReportError(string path, Exception ex) => _errors.Enqueue(new JobError(path, JobErrorKind.Io, ex.Message));

    public void ReportError(string path, JobErrorKind kind, string? detail = null) => _errors.Enqueue(new JobError(path, kind, detail));

    /// <summary>Blocks while paused, throws if cancelled. Call between buffers.</summary>
    public void Checkpoint()
    {
        Token.ThrowIfCancellationRequested();
        Pause.WaitIfPaused(Token);
    }

    /// <summary>
    /// Synchronous bridge for worker threads. Workers run on dedicated threads
    /// doing blocking file I/O, so blocking here while the UI shows the
    /// question is fine and keeps the engines simple.
    /// </summary>
    public ConflictChoice Resolve(ConflictInfo conflict) =>
        Conflicts.ResolveAsync(conflict, Token).GetAwaiter().GetResult();
}
