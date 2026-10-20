using TravelPlanner.Services;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// The cover crop screen's arithmetic. The frame has the cover's shape; the photo under it is scaled so it covers
    /// the frame (<see cref="CoverScale"/>), then zoomed (1 or more) and moved by the user, but never so far that the
    /// frame shows anything outside the photo.
    /// </summary>
    public static class CoverCropMath
    {
        public const double MaxZoom = 5;

        /// <summary>Within this share of the cover's shape a photo is used as it is, without the crop screen.</summary>
        public const double ShapeTolerance = 0.01;

        /// <summary>The photo has (nearly) the cover's 16:9 shape.</summary>
        public static bool IsCoverShaped(int width, int height) =>
            width > 0 && height > 0
            && Math.Abs((double)width / height * TripCoverService.CoverHeight / TripCoverService.CoverWidth - 1) <= ShapeTolerance;

        /// <summary>Scale at which the photo just covers the frame.</summary>
        public static double CoverScale(double photoWidth, double photoHeight, double frameWidth, double frameHeight) =>
            Math.Max(frameWidth / photoWidth, frameHeight / photoHeight);

        public static double ClampZoom(double zoom) => Math.Clamp(double.IsFinite(zoom) ? zoom : 1, 1, MaxZoom);

        /// <summary>How far the photo's centre may move from the frame's centre along one side.</summary>
        public static double ClampOffset(double offset, double shownSize, double frameSize)
        {
            var max = Math.Max(0, (shownSize - frameSize) / 2);
            return Math.Clamp(double.IsFinite(offset) ? offset : 0, -max, max);
        }

        /// <summary>The part of the photo inside the frame, with the photo's centre moved by (<paramref name="offsetX"/>, <paramref name="offsetY"/>).</summary>
        public static CropRegion Region(double photoWidth, double photoHeight, double frameWidth, double frameHeight,
                                        double zoom, double offsetX, double offsetY)
        {
            var scale = CoverScale(photoWidth, photoHeight, frameWidth, frameHeight) * ClampZoom(zoom);
            var shownWidth = photoWidth * scale;
            var shownHeight = photoHeight * scale;
            offsetX = ClampOffset(offsetX, shownWidth, frameWidth);
            offsetY = ClampOffset(offsetY, shownHeight, frameHeight);

            var width = Math.Min(1, frameWidth / shownWidth);
            var height = Math.Min(1, frameHeight / shownHeight);
            var x = Math.Clamp((shownWidth - frameWidth) / 2 - offsetX, 0, Math.Max(0, shownWidth - frameWidth)) / shownWidth;
            var y = Math.Clamp((shownHeight - frameHeight) / 2 - offsetY, 0, Math.Max(0, shownHeight - frameHeight)) / shownHeight;
            return new CropRegion(x, y, width, height);
        }

        /// <summary>The middle part of the photo with the shape of <paramref name="targetWidth"/> x <paramref name="targetHeight"/>.</summary>
        public static CropRegion CenterRegion(double photoWidth, double photoHeight, double targetWidth, double targetHeight) =>
            Region(photoWidth, photoHeight, targetWidth, targetHeight, 1, 0, 0);
    }
}
