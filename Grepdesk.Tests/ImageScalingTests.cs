using Grepdesk.Core.Preview;

namespace Grepdesk.Tests;

public class ImageScalingTests
{
    [Theory]
    [InlineData(16, 16, 1.0, false)]      // shown 1:1
    [InlineData(16, 16, 1.5, true)]       // a 16 px icon on a 150 % screen
    [InlineData(16, 16, 8.0, true)]       // zoomed far in
    [InlineData(4000, 3000, 1.25, false)] // a photo enlarged a little stays smooth
    [InlineData(4000, 3000, 3.0, true)]   // ...but zoomed in to see pixels
    [InlineData(4000, 3000, 0.2, false)]  // shrunk to fit
    [InlineData(512, 300, 1.5, true)]     // at the size limit
    [InlineData(513, 300, 1.5, false)]
    public void Chooses_hard_pixels_only_when_enlarging_small_images_or_zooming_in(int width, int height, double scale, bool nearest) =>
        Assert.Equal(nearest, ImageScaling.UseNearestNeighbor(width, height, scale));
}
