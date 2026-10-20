using Microsoft.Maui.Graphics;
using TravelPlanner.Helpers;
#if ANDROID || IOS || MACCATALYST
using Microsoft.Maui.Graphics.Platform;
#endif

namespace TravelPlanner.Services
{
    /// <summary>
    /// Crops covers with the platform's image code (Microsoft.Maui.Graphics): the chosen part of the photo (the middle
    /// one when none was chosen) drawn onto a picture of the target size, saved as JPEG. The photo arrives already
    /// turned upright: the picker is asked to apply the camera's orientation (MediaPickerOptions.RotateImage).
    /// </summary>
    public sealed class CoverImageProcessor : ICoverImageProcessor
    {
        public Task<(int Width, int Height)> MeasureAsync(Stream photo) => Task.Run(() => Measure(photo));

        public Task<byte[]> CropAsync(Stream photo, CropRegion? region, int width, int height, float quality) =>
            Task.Run(() => Crop(photo, region, width, height, quality));

        private static (int Width, int Height) Measure(Stream photo)
        {
#if ANDROID || IOS || MACCATALYST
            using var image = Load(photo);
            return ((int)image.Width, (int)image.Height);
#else
            throw new PlatformNotSupportedException("Cover photos are cropped on Android and iOS only.");
#endif
        }

        private static byte[] Crop(Stream photo, CropRegion? region, int width, int height, float quality)
        {
#if ANDROID || IOS || MACCATALYST
            using var image = Load(photo);
            var area = region ?? CoverCropMath.CenterRegion(image.Width, image.Height, width, height);

            // the region scaled to the target: the whole photo is drawn shifted and scaled, the context cuts the rest
            var scaleX = width / (area.Width * image.Width);
            var scaleY = height / (area.Height * image.Height);
            using var context = new PlatformBitmapExportService().CreateContext(width, height, 1);
            context.Canvas.Antialias = true;
            context.Canvas.DrawImage(image,
                (float)(-area.X * image.Width * scaleX), (float)(-area.Y * image.Height * scaleY),
                (float)(image.Width * scaleX), (float)(image.Height * scaleY));

            using var output = new MemoryStream();
            context.Image.Save(output, ImageFormat.Jpeg, quality);
            return output.ToArray();
#else
            throw new PlatformNotSupportedException("Cover photos are cropped on Android and iOS only.");
#endif
        }

#if ANDROID || IOS || MACCATALYST
        private static Microsoft.Maui.Graphics.IImage Load(Stream photo)
        {
            Microsoft.Maui.Graphics.IImage? image;
            try
            {
                image = PlatformImage.FromStream(photo);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Not a picture this device can read.", ex);
            }
            if (image is null || image.Width < 1 || image.Height < 1)
            {
                image?.Dispose();
                throw new InvalidDataException("Not a picture this device can read.");
            }
            return image;
        }
#endif
    }
}
