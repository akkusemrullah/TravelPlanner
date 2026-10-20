using Microsoft.Maui.Graphics;
#if ANDROID || IOS || MACCATALYST
using Microsoft.Maui.Graphics.Platform;
#endif

namespace TravelPlanner.Services
{
    /// <summary>
    /// Crops covers with the platform's image code (Microsoft.Maui.Graphics): scaled to fill the target and
    /// center-cropped (ResizeMode.Bleed), saved as JPEG. The photo arrives already turned upright: the picker is
    /// asked to apply the camera's orientation (MediaPickerOptions.RotateImage).
    /// </summary>
    public sealed class CoverImageProcessor : ICoverImageProcessor
    {
        public Task<byte[]> CropAsync(Stream photo, int width, int height, float quality) =>
            Task.Run(() => Crop(photo, width, height, quality));

        private static byte[] Crop(Stream photo, int width, int height, float quality)
        {
#if ANDROID || IOS || MACCATALYST
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
                throw new InvalidDataException("Not a picture this device can read.");

            using (image)
            using (var cropped = image.Resize(width, height, ResizeMode.Bleed, disposeOriginal: false))
            using (var output = new MemoryStream())
            {
                cropped.Save(output, ImageFormat.Jpeg, quality);
                return output.ToArray();
            }
#else
            throw new PlatformNotSupportedException("Cover photos are cropped on Android and iOS only.");
#endif
        }
    }
}
