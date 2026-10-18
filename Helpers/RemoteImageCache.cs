using System.Security.Cryptography;
using System.Text;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// Downloads remote images with the app's User-Agent and keeps them on disk for <see cref="CacheValidity"/>.
    /// MAUI's UriImageSource sends no User-Agent, and some hosts (Wikimedia Commons) answer such requests with 403.
    /// A failed download falls back to the stale copy, if there is one.
    /// </summary>
    public static class RemoteImageCache
    {
        public static readonly TimeSpan CacheValidity = TimeSpan.FromDays(7);
        private const long MaxBytes = 5 * 1024 * 1024;

        private static readonly HttpClient Http = CreateClient();

        internal static Func<string> CacheDirectory { get; set; } =
            () => Path.Combine(FileSystem.CacheDirectory, "remote-images");

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppConstants.HttpUserAgent);
            return client;
        }

        internal static string CachePath(Uri uri)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri));
            return Path.Combine(CacheDirectory(), Convert.ToHexString(hash).ToLowerInvariant() + ".img");
        }

        public static async Task<Stream?> OpenAsync(Uri uri, CancellationToken ct)
        {
            var path = CachePath(uri);
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < CacheValidity)
                return File.OpenRead(path);

            try
            {
                using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxBytes)
                    throw new InvalidDataException("Image too large.");

                var buffer = new MemoryStream();
                await using (var body = await response.Content.ReadAsStreamAsync(ct))
                {
                    var chunk = new byte[81920];
                    int read;
                    while ((read = await body.ReadAsync(chunk, ct)) > 0)
                    {
                        if (buffer.Length + read > MaxBytes)
                            throw new InvalidDataException("Image too large.");
                        buffer.Write(chunk, 0, read);
                    }
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var temp = path + ".tmp";
                    await File.WriteAllBytesAsync(temp, buffer.ToArray(), ct);
                    File.Move(temp, path, overwrite: true);
                }
                catch (IOException)
                {
                    // The image still shows; it is downloaded again next time.
                }

                buffer.Position = 0;
                return buffer;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
            {
                return File.Exists(path) ? File.OpenRead(path) : null;
            }
        }
    }
}
