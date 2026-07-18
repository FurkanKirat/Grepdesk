using System.Diagnostics;

namespace FileSearch.Core;

public enum ShellActionStatus
{
    Success,
    ExecutableNotFound,
    IncompatibleTarget,   // örn. File-only editöre klasör verildi
    ProcessStartFailed,   // Process.Start exception fırlattı
    PathNotFound          // hedef path artık diskte yok
}

public readonly struct ShellActionResult
{
    public ShellActionStatus Status { get; }
    public Process? Process { get; }
    public Exception? Exception { get; }

    private ShellActionResult(ShellActionStatus status, Process? process, Exception? exception)
    {
        Status = status;
        Process = process;
        Exception = exception;
    }

    public bool IsSuccess => Status == ShellActionStatus.Success;

    public static ShellActionResult Success(Process? process) =>
        new(ShellActionStatus.Success, process, null);

    public static ShellActionResult Failure(ShellActionStatus status, Exception? exception = null) =>
        new(status, null, exception);
}