namespace Grepdesk.Core.Preview;

/// <summary>Which resampling an image should be drawn with.</summary>
public static class ImageScaling
{
    /// <summary>
    /// True when the image should be enlarged with nearest-neighbour (hard pixels) instead of smoothing.
    /// Smoothing blurs a 16 × 16 icon or pixel art into mush when it is enlarged, and a bigger scale is
    /// usually a zoom to look at pixels; photos shown only a little larger stay smooth.
    /// </summary>
    /// <param name="scale">Device pixels per image pixel: the zoom times the screen's scaling (150 % display = 1.5).</param>
    public static bool UseNearestNeighbor(int pixelWidth, int pixelHeight, double scale)
    {
        if (scale <= 1.0001) return false;               // shrinking or 1:1: smooth (and identical at 1:1)
        if (scale >= 2) return true;                     // zoomed in: show the pixels
        return Math.Max(pixelWidth, pixelHeight) <= SmallImageSide; // a little enlargement: icons and sprites stay crisp
    }

    /// <summary>The longest side, in pixels, up to which an image counts as an icon or sprite.</summary>
    public const int SmallImageSide = 512;
}
