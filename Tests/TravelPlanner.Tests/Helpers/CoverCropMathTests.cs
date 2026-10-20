using Xunit;
using TravelPlanner.Helpers;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    /// <summary>Kapağı kırp: which photos need the crop screen, and which part of the photo the frame shows.</summary>
    public class CoverCropMathTests
    {
        private const int Precision = 6;

        [Theory]
        [InlineData(1920, 1080, true)]
        [InlineData(1280, 720, true)]
        [InlineData(1926, 1080, true)]   // within 1 %
        [InlineData(4000, 3000, false)]  // 4:3
        [InlineData(1080, 1920, false)]  // portrait
        [InlineData(1000, 1000, false)]
        [InlineData(0, 0, false)]
        public void IsCoverShaped(int width, int height, bool expected) =>
            Assert.Equal(expected, CoverCropMath.IsCoverShaped(width, height));

        [Fact]
        public void Centre_OfA4By3Photo_IsAFullWidthBand()
        {
            var region = CoverCropMath.CenterRegion(4000, 3000, 1280, 720);

            Assert.Equal(0, region.X, Precision);
            Assert.Equal(1, region.Width, Precision);
            Assert.Equal(2250.0 / 3000, region.Height, Precision); // 4000 x 2250 is 16:9
            Assert.Equal((1 - region.Height) / 2, region.Y, Precision);
            Assert.True(region.IsValid);
        }

        [Fact]
        public void Centre_OfAPortraitPhoto_IsAFullWidthBandInTheMiddle()
        {
            var region = CoverCropMath.CenterRegion(1080, 1920, 1280, 720);

            Assert.Equal(1, region.Width, Precision);
            Assert.Equal(1080.0 * 9 / 16 / 1920, region.Height, Precision);
            Assert.Equal((1 - region.Height) / 2, region.Y, Precision);
        }

        [Fact]
        public void MovingThePhotoDown_ShowsItsTop_ButNeverPastIt()
        {
            // frame 320 x 180, 4:3 photo shown 320 x 240: 60 pt to move up or down
            var top = CoverCropMath.Region(4000, 3000, 320, 180, 1, 0, 30);
            Assert.Equal(0, top.Y, Precision);

            var farther = CoverCropMath.Region(4000, 3000, 320, 180, 1, 0, 500);
            Assert.Equal(top, farther);

            var bottom = CoverCropMath.Region(4000, 3000, 320, 180, 1, 0, -30);
            Assert.Equal(1, bottom.Y + bottom.Height, Precision);
        }

        [Fact]
        public void Zoom_ShowsASmallerPart_AroundTheMiddle()
        {
            var region = CoverCropMath.Region(4000, 3000, 320, 180, 2, 0, 0);

            Assert.Equal(0.5, region.Width, Precision);
            Assert.Equal(0.25, region.X, Precision);
            Assert.Equal(0.5 - region.Height / 2, region.Y, Precision);
            Assert.True(region.IsValid);
        }

        [Fact]
        public void ZoomedAndMoved_ToTheRightEdge()
        {
            // zoom 2: photo shown 640 x 480; may move 160 pt sideways
            var region = CoverCropMath.Region(4000, 3000, 320, 180, 2, -1000, 0);

            Assert.Equal(1, region.X + region.Width, Precision);
        }

        [Theory]
        [InlineData(0.5, 1)]
        [InlineData(3, 3)]
        [InlineData(50, CoverCropMath.MaxZoom)]
        [InlineData(double.NaN, 1)]
        public void Zoom_StaysBetweenOneAndTheMaximum(double zoom, double expected) =>
            Assert.Equal(expected, CoverCropMath.ClampZoom(zoom));

        [Fact]
        public void Region_IsAlwaysInsideThePhoto_AndOfTheCoversShape()
        {
            var random = new Random(5);
            for (var i = 0; i < 500; i++)
            {
                int width = random.Next(100, 6000), height = random.Next(100, 6000);
                var region = CoverCropMath.Region(width, height, 320, 180,
                    random.NextDouble() * 8, (random.NextDouble() - 0.5) * 4000, (random.NextDouble() - 0.5) * 4000);
                Assert.True(region.IsValid, region.ToString());
                Assert.Equal(16.0 / 9, region.Width * width / (region.Height * height), 6); // always the cover's shape
            }
        }
    }
}
