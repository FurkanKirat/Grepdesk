using System;
using System.Collections.Generic;
using System.Text;
using Grepdesk.Core.Viewer;

namespace Grepdesk.Core.Ocr;

/// <summary>
/// Helpers that bridge rendered PDFium bitmaps, OCR results, and <see cref="PdfPageText"/>.
/// </summary>
public static class OcrPdfText
{
    private const double TargetDpiScale = 200.0 / 72.0;
    private const double MaxOcrPixels = 8_000_000;
    private const int FallbackMaxDimension = 2600;

    /// <summary>
    /// Composites unpremultiplied BGRA pixels onto opaque white.
    /// PDFium renders blank paper as transparent (0, 0, 0, 0), which OCR engines
    /// would otherwise read as black.
    /// </summary>
    public static OcrImage CompositeOnWhite(byte[] bgraPixels, int width, int height)
    {
        var byteCount = checked(width * height * 4);
        var output = new byte[byteCount];

        for (var i = 0; i < byteCount; i += 4)
        {
            var a = bgraPixels[i + 3];
            if (a == 255)
            {
                output[i] = bgraPixels[i];
                output[i + 1] = bgraPixels[i + 1];
                output[i + 2] = bgraPixels[i + 2];
                output[i + 3] = 255;
            }
            else if (a == 0)
            {
                output[i] = 255;
                output[i + 1] = 255;
                output[i + 2] = 255;
                output[i + 3] = 255;
            }
            else
            {
                var inv = 255 - a;
                output[i] = (byte)((bgraPixels[i] * a + 255 * inv + 127) / 255);
                output[i + 1] = (byte)((bgraPixels[i + 1] * a + 255 * inv + 127) / 255);
                output[i + 2] = (byte)((bgraPixels[i + 2] * a + 255 * inv + 127) / 255);
                output[i + 3] = 255;
            }
        }

        return new OcrImage(output, width, height);
    }

    /// <summary>
    /// Converts OCR word bounding boxes (in image pixels) into a <see cref="PdfPageText"/>
    /// with per-character boxes in PDF points.
    /// </summary>
    public static PdfPageText ToPdfPageText(
        OcrResult ocr,
        int imageWidth,
        int imageHeight,
        double pageWidthPoints,
        double pageHeightPoints)
    {
        if (ocr.IsEmpty || imageWidth <= 0 || imageHeight <= 0 || pageWidthPoints <= 0 || pageHeightPoints <= 0)
            return PdfPageText.Empty;

        var sx = pageWidthPoints / imageWidth;
        var sy = pageHeightPoints / imageHeight;

        var sb = new StringBuilder();
        var boxes = new List<PdfCharBox>();

        foreach (var line in ocr.Lines)
        {
            if (line.Words.Count == 0) continue;

            var lineHasWords = false;
            double prevRight = 0, prevTop = 0, prevBottom = 0;

            foreach (var word in line.Words)
            {
                if (string.IsNullOrEmpty(word.Text)) continue;

                if (!lineHasWords)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append("\r\n");
                        boxes.Add(default);
                        boxes.Add(default);
                    }
                    lineHasWords = true;
                }
                else
                {
                    sb.Append(' ');
                    if (word.Left > prevRight)
                    {
                        boxes.Add(new PdfCharBox(
                            prevRight * sx,
                            Math.Min(prevTop, word.Top) * sy,
                            word.Left * sx,
                            Math.Max(prevBottom, word.Top + word.Height) * sy));
                    }
                    else
                    {
                        boxes.Add(default);
                    }
                }

                var len = word.Text.Length;
                var top = word.Top * sy;
                var bottom = (word.Top + word.Height) * sy;

                for (var j = 0; j < len; j++)
                {
                    var left = (word.Left + word.Width * j / len) * sx;
                    var right = (word.Left + word.Width * (j + 1) / len) * sx;
                    sb.Append(word.Text[j]);
                    boxes.Add(new PdfCharBox(left, top, right, bottom));
                }

                prevRight = word.Left + word.Width;
                prevTop = word.Top;
                prevBottom = word.Top + word.Height;
            }
        }

        return sb.Length == 0 ? PdfPageText.Empty : new PdfPageText(sb.ToString(), boxes.ToArray());
    }

    /// <summary>Returns true if the page contains at least one visible non-whitespace character.</summary>
    public static bool HasMeaningfulText(PdfPageText? text)
    {
        if (text is null || text.Length == 0) return false;
        var s = text.Text;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c > ' ' && c is not ('\uFFFE' or '\uFFFF') && !char.IsWhiteSpace(c))
                return true;
        }
        return false;
    }

    /// <summary>Computes a render scale (~200 DPI) capped by the OCR engine's max dimension and pixel budget.</summary>
    public static double OcrScaleForPage(double pageWidthPoints, double pageHeightPoints, int maxImageDimension)
    {
        if (pageWidthPoints <= 0 || pageHeightPoints <= 0) return 1.0;

        var maxDim = maxImageDimension > 0 ? maxImageDimension : FallbackMaxDimension;
        var scale = TargetDpiScale;
        scale = Math.Min(scale, maxDim / pageWidthPoints);
        scale = Math.Min(scale, maxDim / pageHeightPoints);
        scale = Math.Min(scale, Math.Sqrt(MaxOcrPixels / (pageWidthPoints * pageHeightPoints)));
        return Math.Max(0.5, scale);
    }
}
