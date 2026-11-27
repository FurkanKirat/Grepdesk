using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Grepdesk.Core.Ocr;

/// <summary>An uncompressed BGRA8888 image ready for on-device OCR.</summary>
public readonly record struct OcrImage(byte[] BgraPixels, int Width, int Height);

/// <summary>A single recognized word with its bounding box in image pixel coordinates.</summary>
public readonly record struct OcrWord(string Text, double Left, double Top, double Width, double Height);

/// <summary>A recognized line of text and its constituent words.</summary>
public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words);

/// <summary>Structured OCR output for one image/page.</summary>
public sealed record OcrResult(IReadOnlyList<OcrLine> Lines)
{
    public static readonly OcrResult Empty = new([]);
    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>Pluggable on-device OCR backend.</summary>
public interface IOcrEngine
{
    string Name { get; }
    bool IsAvailable { get; }
    int MaxImageDimension { get; }
    Task<OcrResult> RecognizeAsync(OcrImage image, CancellationToken ct = default);
}
