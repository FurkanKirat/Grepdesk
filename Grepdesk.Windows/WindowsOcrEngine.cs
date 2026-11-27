using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Grepdesk.Core.Ocr;

namespace Grepdesk.Windows;

/// <summary>
/// On-device OCR backed by Windows' built-in <c>Windows.Media.Ocr.OcrEngine</c> WinRT APIs.
/// Uses direct WinRT activation and COM vtable calls so <c>Grepdesk.Windows</c> stays
/// targeting plain <c>net10.0</c> with zero extra NuGet dependencies.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsOcrEngine : IOcrEngine
{
    private const int RO_INIT_MULTITHREADED = 1;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    private const int BitmapPixelFormatBgra8 = 87;
    private const int BitmapAlphaModePremultiplied = 0;
    private const int AsyncStatusCompleted = 1;

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid IID_IAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
    private static readonly Guid IID_IClosable = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
    private static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-c000-000000000046");
    private static readonly Guid IID_IBufferFactory = new("71af914d-c10f-484b-bc50-14bc623b3a27");
    private static readonly Guid IID_IBuffer = new("905a0fe0-bc53-11df-8c49-001e4fc686da");
    private static readonly Guid IID_IBufferByteAccess = new("905a0fef-bc53-11df-8c49-001e4fc686da");
    private static readonly Guid IID_ISoftwareBitmapStatics = new("df0385db-672f-4a9d-806e-c2442f343e86");
    private static readonly Guid IID_IOcrEngineStatics = new("5bffa85a-3384-3540-9940-699120d428a8");
    private static readonly Guid IID_IOcrEngine = new("5a14bc41-5b76-3140-b680-8825562683ac");
    private static readonly Guid IID_IAsyncOperationOcrResult = new("c7d7118e-ae36-59c0-ac76-7badee711c8b");
    private static readonly Guid IID_IAsyncOperationCompletedHandlerOcrResult = new("989c1371-444a-5e7e-b197-9eaaf9d2829a");
    private static readonly Guid IID_IOcrResult = new("9bd235b2-175b-3d6a-92e2-388c206e2f63");
    private static readonly Guid IID_IOcrLine = new("0043a16f-e31f-3a24-899c-d444bd088124");
    private static readonly Guid IID_IOcrWord = new("3c2a477a-5cd9-3525-ba2a-23d1e0a68a1d");

    private static readonly Lazy<(bool Available, int MaxDimension)> Probe = new(ProbeSupport);

    public string Name => "windows-ocr";
    public bool IsAvailable => Probe.Value.Available;
    public int MaxImageDimension => Probe.Value.MaxDimension;

    public Task<OcrResult> RecognizeAsync(OcrImage image, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || !IsAvailable)
            return Task.FromResult(OcrResult.Empty);

        if (image.Width <= 0 || image.Height <= 0)
            return Task.FromResult(OcrResult.Empty);

        var byteCount = checked(image.Width * image.Height * 4);
        if (image.BgraPixels.Length < byteCount)
            return Task.FromResult(OcrResult.Empty);

        return Task.Run(() => RecognizeCore(image, (uint)byteCount, ct), ct);
    }

    private static (bool Available, int MaxDimension) ProbeSupport()
    {
        if (!OperatingSystem.IsWindows())
            return (false, 2600);

        var roHr = RoInitialize(RO_INIT_MULTITHREADED);
        if (roHr < 0 && roHr != RPC_E_CHANGED_MODE)
            return (false, 2600);

        void* statics = null;
        void* engine = null;
        try
        {
            if (GetActivationFactory("Windows.Media.Ocr.OcrEngine", IID_IOcrEngineStatics, out statics) < 0 || statics is null)
                return (false, 2600);

            uint maxDim = 0;
            var hr = ((delegate* unmanaged[Stdcall]<void*, uint*, int>)Slot(statics, 6))(statics, &maxDim);
            if (hr < 0 || maxDim == 0)
                maxDim = 2600;

            engine = CreateOcrEngine(statics);
            return engine is not null ? (true, (int)maxDim) : (false, (int)maxDim);
        }
        catch
        {
            return (false, 2600);
        }
        finally
        {
            Release(engine);
            Release(statics);
            if (roHr is 0 or 1)
                RoUninitialize();
        }
    }

    private static OcrResult RecognizeCore(OcrImage image, uint byteCount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var roHr = RoInitialize(RO_INIT_MULTITHREADED);
        if (roHr < 0 && roHr != RPC_E_CHANGED_MODE)
            return OcrResult.Empty;

        void* ocrStatics = null;
        void* ocrEngine = null;
        void* bufferFactory = null;
        void* buffer = null;
        void* byteAccess = null;
        void* bitmapStatics = null;
        void* softwareBitmap = null;
        void* asyncOp = null;
        void* asyncInfo = null;
        void* ocrResult = null;
        void* linesView = null;
        CompletedHandlerObject* handler = null;
        using var done = new ManualResetEventSlim(false);

        try
        {
            CheckHr(GetActivationFactory("Windows.Media.Ocr.OcrEngine", IID_IOcrEngineStatics, out ocrStatics), "OcrEngine factory");
            ocrEngine = CreateOcrEngine(ocrStatics);
            if (ocrEngine is null)
                return OcrResult.Empty;

            CheckHr(GetActivationFactory("Windows.Storage.Streams.Buffer", IID_IBufferFactory, out bufferFactory), "Buffer factory");
            CheckHr(((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Slot(bufferFactory, 6))(bufferFactory, byteCount, &buffer), "Buffer.Create");
            CheckHr(((delegate* unmanaged[Stdcall]<void*, uint, int>)Slot(buffer, 8))(buffer, byteCount), "Buffer.put_Length");

            var iidByteAccess = IID_IBufferByteAccess;
            CheckHr(QueryInterface(buffer, &iidByteAccess, &byteAccess), "IBufferByteAccess");

            byte* dst = null;
            CheckHr(((delegate* unmanaged[Stdcall]<void*, byte**, int>)Slot(byteAccess, 3))(byteAccess, &dst), "IBufferByteAccess.Buffer");
            fixed (byte* src = image.BgraPixels)
            {
                System.Buffer.MemoryCopy(src, dst, byteCount, byteCount);
            }

            CheckHr(GetActivationFactory("Windows.Graphics.Imaging.SoftwareBitmap", IID_ISoftwareBitmapStatics, out bitmapStatics), "SoftwareBitmap factory");
            CheckHr(((delegate* unmanaged[Stdcall]<void*, void*, int, int, int, int, void**, int>)Slot(bitmapStatics, 10))(
                bitmapStatics, buffer, BitmapPixelFormatBgra8, image.Width, image.Height, BitmapAlphaModePremultiplied, &softwareBitmap),
                "SoftwareBitmap.CreateCopyWithAlphaFromBuffer");

            CheckHr(((delegate* unmanaged[Stdcall]<void*, void*, void**, int>)Slot(ocrEngine, 6))(ocrEngine, softwareBitmap, &asyncOp), "OcrEngine.RecognizeAsync");

            var iidAsyncInfo = IID_IAsyncInfo;
            QueryInterface(asyncOp, &iidAsyncInfo, &asyncInfo);

            handler = CompletedHandlerObject.Create(done);
            CheckHr(((delegate* unmanaged[Stdcall]<void*, void*, int>)Slot(asyncOp, 6))(asyncOp, handler), "IAsyncOperation.put_Completed");

            try
            {
                done.Wait(ct);
            }
            catch (OperationCanceledException)
            {
                if (asyncInfo is not null)
                    ((delegate* unmanaged[Stdcall]<void*, int>)Slot(asyncInfo, 9))(asyncInfo); // Cancel
                throw;
            }

            if (asyncInfo is not null)
            {
                int status = 0;
                if (((delegate* unmanaged[Stdcall]<void*, int*, int>)Slot(asyncInfo, 7))(asyncInfo, &status) >= 0 &&
                    status != AsyncStatusCompleted)
                {
                    if (status == 2) throw new OperationCanceledException(ct);
                    int errHr = 0;
                    if (((delegate* unmanaged[Stdcall]<void*, int*, int>)Slot(asyncInfo, 8))(asyncInfo, &errHr) >= 0 && errHr < 0)
                        Marshal.ThrowExceptionForHR(errHr);
                    return OcrResult.Empty;
                }
            }

            CheckHr(((delegate* unmanaged[Stdcall]<void*, void**, int>)Slot(asyncOp, 8))(asyncOp, &ocrResult), "IAsyncOperation.GetResults");
            if (ocrResult is null)
                return OcrResult.Empty;

            CheckHr(((delegate* unmanaged[Stdcall]<void*, void**, int>)Slot(ocrResult, 6))(ocrResult, &linesView), "OcrResult.get_Lines");
            if (linesView is null)
                return OcrResult.Empty;

            uint lineCount = 0;
            CheckHr(((delegate* unmanaged[Stdcall]<void*, uint*, int>)Slot(linesView, 7))(linesView, &lineCount), "Lines.get_Size");
            if (lineCount == 0)
                return OcrResult.Empty;

            var lines = new List<OcrLine>((int)lineCount);
            for (uint i = 0; i < lineCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                void* lineObj = null;
                void* wordsView = null;
                try
                {
                    if (((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Slot(linesView, 6))(linesView, i, &lineObj) < 0 || lineObj is null)
                        continue;

                    var lineText = ReadHString(lineObj, 7);
                    if (((delegate* unmanaged[Stdcall]<void*, void**, int>)Slot(lineObj, 6))(lineObj, &wordsView) < 0 || wordsView is null)
                        continue;

                    uint wordCount = 0;
                    if (((delegate* unmanaged[Stdcall]<void*, uint*, int>)Slot(wordsView, 7))(wordsView, &wordCount) < 0 || wordCount == 0)
                        continue;

                    var words = new List<OcrWord>((int)wordCount);
                    for (uint j = 0; j < wordCount; j++)
                    {
                        void* wordObj = null;
                        try
                        {
                            if (((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Slot(wordsView, 6))(wordsView, j, &wordObj) < 0 || wordObj is null)
                                continue;

                            WinRtRect rect = default;
                            if (((delegate* unmanaged[Stdcall]<void*, WinRtRect*, int>)Slot(wordObj, 6))(wordObj, &rect) < 0)
                                continue;

                            var wordText = ReadHString(wordObj, 7);
                            if (string.IsNullOrEmpty(wordText))
                                continue;

                            words.Add(new OcrWord(wordText, rect.X, rect.Y, rect.Width, rect.Height));
                        }
                        finally
                        {
                            Release(wordObj);
                        }
                    }

                    if (words.Count > 0)
                        lines.Add(new OcrLine(lineText, words));
                }
                finally
                {
                    Release(wordsView);
                    Release(lineObj);
                }
            }

            return lines.Count == 0 ? OcrResult.Empty : new OcrResult(lines);
        }
        finally
        {
            if (handler is not null) Release(handler);
            if (asyncInfo is not null) ((delegate* unmanaged[Stdcall]<void*, int>)Slot(asyncInfo, 10))(asyncInfo); // Close
            CloseIfClosable(softwareBitmap);
            Release(linesView);
            Release(ocrResult);
            Release(asyncInfo);
            Release(asyncOp);
            Release(softwareBitmap);
            Release(bitmapStatics);
            Release(byteAccess);
            Release(buffer);
            Release(bufferFactory);
            Release(ocrEngine);
            Release(ocrStatics);
            if (roHr is 0 or 1)
                RoUninitialize();
        }
    }

    private static void* CreateOcrEngine(void* ocrStatics)
    {
        void* engine = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void**, int>)Slot(ocrStatics, 10))(ocrStatics, &engine); // TryCreateFromUserProfileLanguages
        if (hr >= 0 && engine is not null)
            return engine;

        void* langs = null;
        void* firstLang = null;
        try
        {
            if (((delegate* unmanaged[Stdcall]<void*, void**, int>)Slot(ocrStatics, 7))(ocrStatics, &langs) < 0 || langs is null)
                return null;

            uint count = 0;
            if (((delegate* unmanaged[Stdcall]<void*, uint*, int>)Slot(langs, 7))(langs, &count) < 0 || count == 0)
                return null;

            if (((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Slot(langs, 6))(langs, 0, &firstLang) < 0 || firstLang is null)
                return null;

            hr = ((delegate* unmanaged[Stdcall]<void*, void*, void**, int>)Slot(ocrStatics, 9))(ocrStatics, firstLang, &engine); // TryCreateFromLanguage
            return hr >= 0 ? engine : null;
        }
        finally
        {
            Release(firstLang);
            Release(langs);
        }
    }

    private static string ReadHString(void* comObj, int slot)
    {
        IntPtr hstring = IntPtr.Zero;
        try
        {
            if (((delegate* unmanaged[Stdcall]<void*, IntPtr*, int>)Slot(comObj, slot))(comObj, &hstring) < 0 || hstring == IntPtr.Zero)
                return "";

            uint len = 0;
            var ptr = WindowsGetStringRawBuffer(hstring, &len);
            return ptr is null || len == 0 ? "" : new string(ptr, 0, (int)len);
        }
        finally
        {
            if (hstring != IntPtr.Zero)
                WindowsDeleteString(hstring);
        }
    }

    private static int GetActivationFactory(string className, Guid iid, out void* factory)
    {
        IntPtr hstring = IntPtr.Zero;
        factory = null;
        try
        {
            fixed (char* p = className)
            {
                var hr = WindowsCreateString(p, (uint)className.Length, &hstring);
                if (hr < 0) return hr;
            }
            void* result = null;
            var actHr = RoGetActivationFactory(hstring, &iid, &result);
            factory = result;
            return actHr;
        }
        finally
        {
            if (hstring != IntPtr.Zero)
                WindowsDeleteString(hstring);
        }
    }

    private static void CloseIfClosable(void* obj)
    {
        if (obj is null) return;
        void* closable = null;
        var iid = IID_IClosable;
        if (QueryInterface(obj, &iid, &closable) >= 0 && closable is not null)
        {
            ((delegate* unmanaged[Stdcall]<void*, int>)Slot(closable, 6))(closable);
            Release(closable);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void* Slot(void* comObj, int index) => (*(void***)comObj)[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int QueryInterface(void* comObj, Guid* iid, void** ppv) =>
        ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Slot(comObj, 0))(comObj, iid, ppv);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Release(void* comObj)
    {
        if (comObj is not null)
            ((delegate* unmanaged[Stdcall]<void*, uint>)Slot(comObj, 2))(comObj);
    }

    private static void CheckHr(int hr, string op)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRtRect
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;
    }

    /// <summary>
    /// Unmanaged COM object implementing <c>IAsyncOperationCompletedHandler&lt;OcrResult&gt;</c> + <c>IAgileObject</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CompletedHandlerObject
    {
        private static readonly void** Vtbl = CreateVtbl();

        public void** LpVtbl;
        public int RefCount;
        public IntPtr EventHandle;

        public static CompletedHandlerObject* Create(ManualResetEventSlim done)
        {
            var obj = (CompletedHandlerObject*)NativeMemory.Alloc((nuint)sizeof(CompletedHandlerObject));
            obj->LpVtbl = Vtbl;
            obj->RefCount = 1;
            obj->EventHandle = GCHandle.ToIntPtr(GCHandle.Alloc(done));
            return obj;
        }

        private static void** CreateVtbl()
        {
            var vtbl = (void**)NativeMemory.Alloc((nuint)(4 * sizeof(void*)));
            vtbl[0] = (delegate* unmanaged[Stdcall]<CompletedHandlerObject*, Guid*, void**, int>)&Qi;
            vtbl[1] = (delegate* unmanaged[Stdcall]<CompletedHandlerObject*, uint>)&AddRef;
            vtbl[2] = (delegate* unmanaged[Stdcall]<CompletedHandlerObject*, uint>)&ReleaseObj;
            vtbl[3] = (delegate* unmanaged[Stdcall]<CompletedHandlerObject*, void*, int, int>)&Invoke;
            return vtbl;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Qi(CompletedHandlerObject* self, Guid* riid, void** ppv)
        {
            if (ppv is null) return unchecked((int)0x80004003); // E_POINTER
            if (*riid == IID_IUnknown || *riid == IID_IAgileObject || *riid == IID_IAsyncOperationCompletedHandlerOcrResult)
            {
                *ppv = self;
                Interlocked.Increment(ref self->RefCount);
                return 0;
            }
            *ppv = null;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(CompletedHandlerObject* self) =>
            (uint)Interlocked.Increment(ref self->RefCount);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint ReleaseObj(CompletedHandlerObject* self)
        {
            var next = Interlocked.Decrement(ref self->RefCount);
            if (next == 0)
            {
                if (self->EventHandle != IntPtr.Zero)
                {
                    GCHandle.FromIntPtr(self->EventHandle).Free();
                    self->EventHandle = IntPtr.Zero;
                }
                NativeMemory.Free(self);
            }
            return (uint)next;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Invoke(CompletedHandlerObject* self, void* asyncOp, int asyncStatus)
        {
            if (self->EventHandle != IntPtr.Zero &&
                GCHandle.FromIntPtr(self->EventHandle).Target is ManualResetEventSlim evt)
            {
                try { evt.Set(); }
                catch (ObjectDisposedException) { }
            }
            return 0;
        }
    }

    [DllImport("combase.dll")]
    private static extern int RoInitialize(int initType);

    [DllImport("combase.dll")]
    private static extern void RoUninitialize();

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, Guid* iid, void** factory);

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString(char* sourceString, uint length, IntPtr* hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern char* WindowsGetStringRawBuffer(IntPtr hstring, uint* length);
}
