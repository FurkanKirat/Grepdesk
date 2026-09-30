using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Grepdesk.Core.Jobs;
using Grepdesk.Core.Transfer;

namespace Grepdesk.Windows;

/// <summary>
/// Copies with CopyFile2, the API Explorer itself uses. It keeps attributes
/// and timestamps, reports progress per chunk, and picks up OS-level
/// speed-ups on its own (block cloning on ReFS / Dev Drive, ODX offload on SANs).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CopyFile2Copier : IFileCopier
{
    // Above this size, skip the system file cache: a multi-GB copy would
    // otherwise evict everything else from RAM for no benefit (Explorer does the same).
    private const long UnbufferedThreshold = 256L * 1024 * 1024;

    private const uint CopyFileFailIfExists = 0x00000001;
    private const uint CopyFileNoBuffering = 0x00001000;

    private const int MessageChunkFinished = 2;
    private const int ActionContinue = 0;
    private const int ActionCancel = 1; // cancel and delete the partial destination

    private const int HResultRequestAborted = unchecked((int)0x800704D3); // ERROR_REQUEST_ABORTED

    // COPYFILE2_MESSAGE: Type (4) + padding (4), then the ChunkFinished union
    // arm: stream number (4), flags (4), source and destination handles, then
    // ULARGE_INTEGERs: chunk number, chunk size, stream size, stream bytes
    // transferred, total file size, total bytes transferred.
    private static readonly int TotalBytesTransferredOffset = 8 + 8 + 2 * IntPtr.Size + 5 * 8;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ProgressRoutine(IntPtr message, IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedParameters
    {
        public uint Size;
        public uint CopyFlags;
        public IntPtr Cancel;
        public IntPtr ProgressRoutine;
        public IntPtr CallbackContext;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int CopyFile2(string existingFileName, string newFileName, ref ExtendedParameters parameters);

    public void Copy(string source, string destination, bool overwrite, JobContext ctx, Action<long> onBytes)
    {
        long reported = 0;

        // Runs on this thread, inside CopyFile2. Exceptions must not cross
        // back into native code, so cancellation is signalled by return value.
        ProgressRoutine routine = (message, _) =>
        {
            if (Marshal.ReadInt32(message) == MessageChunkFinished)
            {
                var total = Marshal.ReadInt64(message, TotalBytesTransferredOffset);
                onBytes(total - reported);
                reported = total;
            }

            try
            {
                ctx.Checkpoint(); // blocks here while paused
                return ActionContinue;
            }
            catch (OperationCanceledException)
            {
                return ActionCancel;
            }
        };

        var flags = overwrite ? 0u : CopyFileFailIfExists;
        if (new FileInfo(source).Length > UnbufferedThreshold)
            flags |= CopyFileNoBuffering;

        var parameters = new ExtendedParameters
        {
            Size = (uint)Marshal.SizeOf<ExtendedParameters>(),
            CopyFlags = flags,
            ProgressRoutine = Marshal.GetFunctionPointerForDelegate(routine)
        };

        var hr = CopyFile2(source, destination, ref parameters);
        GC.KeepAlive(routine);

        if (hr == HResultRequestAborted)
            throw new OperationCanceledException(ctx.Token);
        if (hr < 0)
        {
            // Keeps the HRESULT, which the engine inspects to spot locked files.
            var error = Marshal.GetExceptionForHR(hr)!;
            throw error is IOException or UnauthorizedAccessException ? error : new IOException(error.Message, hr);
        }
    }
}
