using System.Buffers;
using Grepdesk.Core.Jobs;

namespace Grepdesk.Core.Transfer;

/// <summary>Copies one file, reporting progress and honouring pause/cancel.</summary>
public interface IFileCopier
{
    /// <param name="onBytes">Called with the number of bytes copied since the last call.</param>
    /// <exception cref="OperationCanceledException">The job was cancelled; no partial destination is left behind.</exception>
    void Copy(string source, string destination, bool overwrite, JobContext ctx, Action<long> onBytes);
}

/// <summary>Portable stream copy, used where no native copy API is wired up.</summary>
public sealed class StreamFileCopier : IFileCopier
{
    private const int BufferSize = 1 << 20;

    public void Copy(string source, string destination, bool overwrite, JobContext ctx, Action<long> onBytes)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var created = false;
        var completed = false;
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            using (var output = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            {
                created = true;
                output.SetLength(input.Length);

                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    onBytes(read);
                    ctx.Checkpoint();
                }
            }

            CopyMetadata(source, destination);
            completed = true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (created && !completed)
            {
                try { File.Delete(destination); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Timestamp plus attributes (Windows) or permission bits (Unix, e.g. a script's execute bit).</summary>
    internal static void CopyMetadata(string source, string destination)
    {
        var info = new FileInfo(source);
        // Time first: a read-only attribute set before it could block the change.
        File.SetLastWriteTimeUtc(destination, info.LastWriteTimeUtc);
        if (OperatingSystem.IsWindows())
            File.SetAttributes(destination, info.Attributes);
        else
            File.SetUnixFileMode(destination, info.UnixFileMode);
    }
}

/// <summary>
/// For Linux and macOS. Small files go through <see cref="File.Copy(string, string, bool)"/>,
/// which uses the kernel's fast paths (copy_file_range and reflinks on Linux,
/// clonefile/fcopyfile on macOS — an instant copy on Btrfs, XFS and APFS).
/// It can't report progress or stop half-way, so large files use the stream
/// copy, which can.
/// </summary>
public sealed class NativeFileCopier : IFileCopier
{
    private const long StreamAbove = 64L * 1024 * 1024;
    private readonly StreamFileCopier _stream = new();

    public void Copy(string source, string destination, bool overwrite, JobContext ctx, Action<long> onBytes)
    {
        var length = new FileInfo(source).Length;
        if (length > StreamAbove)
        {
            _stream.Copy(source, destination, overwrite, ctx, onBytes);
            return;
        }

        ctx.Checkpoint();
        File.Copy(source, destination, overwrite);
        StreamFileCopier.CopyMetadata(source, destination);
        onBytes(length);
    }
}
