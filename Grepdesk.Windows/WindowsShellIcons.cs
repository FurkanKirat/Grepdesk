using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Grepdesk.Core.Preview;
using static Grepdesk.Windows.WindowsShellPreview;

namespace Grepdesk.Windows;

/// <summary>
/// Explorer's icons through IShellItemImageFactory. A folder listing asks for
/// hundreds at once, so they are drawn one after another on a single STA
/// thread that lives as long as the app, rather than a thread per icon.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShellIcons : IShellIcons
{
    private const int SIIGBF_ICONONLY = 0x04;

    private sealed record Request(string Path, int Size, TaskCompletionSource<ShellThumbnail?> Result, CancellationToken Ct);

    private readonly BlockingCollection<Request> _queue = new();

    public WindowsShellIcons()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Shell icons" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public Task<ShellThumbnail?> GetIconAsync(string path, int size, CancellationToken ct = default)
    {
        var request = new Request(path, size, new TaskCompletionSource<ShellThumbnail?>(TaskCreationOptions.RunContinuationsAsynchronously), ct);
        _queue.Add(request, CancellationToken.None);
        return request.Result.Task;
    }

    private void Run()
    {
        foreach (var request in _queue.GetConsumingEnumerable())
        {
            // A folder left before its icons were drawn: skip the rest of its queue cheaply.
            if (request.Ct.IsCancellationRequested)
            {
                request.Result.TrySetResult(null);
                continue;
            }

            ShellThumbnail? icon = null;
            try { icon = Load(Path.GetFullPath(request.Path), request.Size); }
            catch (COMException) { }
            catch (InvalidCastException) { }
            catch (ArgumentException) { }
            request.Result.TrySetResult(icon);
        }
    }

    private static ShellThumbnail? Load(string path, int size)
    {
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var obj) != 0)
            return null;

        var hbitmap = IntPtr.Zero;
        try
        {
            var factory = (IShellItemImageFactory)obj;
            if (factory.GetImage(new NativeSize { cx = size, cy = size }, SIIGBF_ICONONLY, out hbitmap) != 0)
                return null;
            return ReadPixels(hbitmap);
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            Marshal.ReleaseComObject(obj);
        }
    }
}
