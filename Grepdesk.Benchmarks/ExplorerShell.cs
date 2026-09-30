using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Grepdesk.Benchmarks;

/// <summary>
/// Drives Windows Explorer's own engines (zip folder, file copy) through the
/// Shell.Application COM object — the same code paths as "Send to →
/// Compressed folder", "Extract all" and Ctrl+C/Ctrl+V.
///
/// The zip folder works asynchronously on a thread that needs a Windows
/// message loop, so the COM calls run on an STA thread that keeps pumping
/// messages until the caller's completion check says the output is complete.
/// Completion is detected by polling, so times carry ±<see cref="PollInterval"/> of error.
/// </summary>
internal static class ExplorerShell
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    // FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI: no dialogs to answer.
    public const int SilentFlags = 4 | 16 | 1024;

    /// <summary>Runs <paramref name="start"/> then pumps messages until <paramref name="isDone"/> is true.</summary>
    [SupportedOSPlatform("windows")]
    public static void Run(Action<dynamic> start, Func<bool> isDone, TimeSpan timeout)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            dynamic? shell = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                start(shell);

                var clock = Stopwatch.StartNew();
                var nextCheck = TimeSpan.Zero;
                while (true)
                {
                    Pump();
                    if (clock.Elapsed >= nextCheck)
                    {
                        if (isDone())
                            break;
                        nextCheck = clock.Elapsed + PollInterval;
                    }
                    if (clock.Elapsed > timeout)
                        throw new TimeoutException($"Explorer did not finish within {timeout}.");
                    Thread.Sleep(10);
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                if (shell is not null)
                    Marshal.FinalReleaseComObject(shell);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
            throw error;
    }

    /// <summary>An empty zip file: the 22-byte end-of-central-directory record alone.</summary>
    public static void CreateEmptyZip(string path) =>
        File.WriteAllBytes(path, [0x50, 0x4B, 0x05, 0x06, .. new byte[18]]);

    /// <summary>True once the file can be opened exclusively, i.e. nobody is writing it any more.</summary>
    public static bool IsUnlocked(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void Pump()
    {
        while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out Msg msg, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Msg msg);
}
