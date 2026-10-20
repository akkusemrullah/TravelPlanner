using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// Downloads remote images with the app's User-Agent and keeps them on disk; the UI shows the file
    /// (<see cref="RemoteImage"/>). MAUI's UriImageSource sends no User-Agent, and some hosts (Wikimedia Commons) answer
    /// such requests with 403.
    ///
    /// Why a file and not a stream: a file image source is cached by the platform's image loader (Glide on Android) under
    /// its path, so a list cell that scrolls back into view shows its picture at once. A stream source was decoded again
    /// on every bind, and its download was cancelled whenever the cell was recycled - pictures came and went while
    /// scrolling and some never finished. Downloads here don't belong to a cell: one per URL at a time (a second caller
    /// waits for the same download), at most <see cref="MaxParallel"/> together, one retry, and a failed URL is left
    /// alone for <see cref="FailureCooldown"/> instead of being asked for on every scroll.
    /// </summary>
    public static class RemoteImageCache
    {
        /// <summary>Older files are still shown, and downloaded again in the background.</summary>
        public static readonly TimeSpan CacheValidity = TimeSpan.FromDays(7);

        internal static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);
        internal const int MaxParallel = 4;
        private const long MaxBytes = 8 * 1024 * 1024;

        private static readonly ConcurrentDictionary<string, Task<string?>> InFlight = new();
        private static readonly ConcurrentDictionary<string, DateTime> FailedAt = new();
        private static readonly SemaphoreSlim Gate = new(MaxParallel);

        internal static HttpClient Http { get; set; } = CreateClient();

        /// <summary>Wait before the one retry (shorter in tests).</summary>
        internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1.5);

        internal static Func<string> CacheDirectory { get; set; } =
            () => Path.Combine(FileSystem.CacheDirectory, "remote-images");

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppConstants.HttpUserAgent);
            return client;
        }

        internal static string CachePath(Uri uri)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri));
            return Path.Combine(CacheDirectory(), Convert.ToHexString(hash).ToLowerInvariant() + ".img");
        }

        /// <summary>
        /// The file of a picture downloaded before, or null. A file older than <see cref="CacheValidity"/> is still
        /// returned (better an old picture than none) and is refreshed in the background.
        /// </summary>
        public static string? CachedFile(Uri uri)
        {
            var path = CachePath(uri);
            if (!File.Exists(path))
                return null;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= CacheValidity)
                _ = DownloadAsync(uri, refresh: true);
            return path;
        }

        /// <summary>Downloads the picture unless it's on disk already: its file, or null when it can't be had now.</summary>
        public static Task<string?> GetFileAsync(Uri uri) =>
            CachedFile(uri) is { } path ? Task.FromResult<string?>(path) : DownloadAsync(uri, refresh: false);

        /// <summary>Downloads pictures the list will soon show (the first page of cards), quietly.</summary>
        public static void Prefetch(IEnumerable<string?> urls)
        {
            var list = urls.Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
            if (list.Count == 0)
                return;
            _ = Task.Run(() =>
            {
                foreach (var url in list)
                {
                    try
                    {
                        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                            _ = GetFileAsync(uri);
                    }
                    catch (Exception)
                    {
                        // best effort: the cell downloads it itself when it shows
                    }
                }
            });
        }

        private static Task<string?> DownloadAsync(Uri uri, bool refresh)
        {
            var key = uri.AbsoluteUri;
            if (FailedAt.TryGetValue(key, out var failed) && DateTime.UtcNow - failed < FailureCooldown)
                return Task.FromResult(refresh ? CachePathIfExists(uri) : null);

            return InFlight.GetOrAdd(key, k => Task.Run(async () =>
            {
                try
                {
                    var path = await DownloadOnceAsync(uri) ?? await RetryAsync(uri);
                    if (path is null)
                        FailedAt[key] = DateTime.UtcNow;
                    else
                        FailedAt.TryRemove(key, out _);
                    return path ?? CachePathIfExists(uri);
                }
                finally
                {
                    InFlight.TryRemove(key, out _);
                }
            }));
        }

        private static async Task<string?> RetryAsync(Uri uri)
        {
            await Task.Delay(RetryDelay);
            return await DownloadOnceAsync(uri);
        }

        private static string? CachePathIfExists(Uri uri)
        {
            var path = CachePath(uri);
            return File.Exists(path) ? path : null;
        }

        /// <summary>One try: the saved file, or null (network error, HTTP error, too large).</summary>
        private static async Task<string?> DownloadOnceAsync(Uri uri)
        {
            await Gate.WaitAsync();
            try
            {
                using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                    return null;
                if (response.Content.Headers.ContentLength > MaxBytes)
                    return null;

                var path = CachePath(uri);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    long size = 0;
                    await using (var body = await response.Content.ReadAsStreamAsync())
                    await using (var file = File.Create(temp))
                    {
                        var chunk = new byte[81920];
                        int read;
                        while ((read = await body.ReadAsync(chunk)) > 0)
                        {
                            size += read;
                            if (size > MaxBytes)
                                return null;
                            await file.WriteAsync(chunk.AsMemory(0, read));
                        }
                    }
                    if (size == 0)
                        return null;
                    File.Move(temp, path, overwrite: true);
                    return path;
                }
                finally
                {
                    try { File.Delete(temp); } catch (IOException) { }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>Tests: forget in-flight downloads and failures.</summary>
        internal static void Reset()
        {
            InFlight.Clear();
            FailedAt.Clear();
        }
    }

    /// <summary>
    /// <c>helpers:RemoteImage.Url="{Binding ThumbUrl}"</c> on an Image: shows the picture from <see cref="RemoteImageCache"/>
    /// - at once when it's on disk, otherwise as soon as it's downloaded (if the Image still shows that URL then; a
    /// recycled list cell may have moved on). Nothing is shown while it loads, so a placeholder behind the Image shows.
    /// </summary>
    public static class RemoteImage
    {
        public static readonly BindableProperty UrlProperty =
            BindableProperty.CreateAttached("Url", typeof(string), typeof(RemoteImage), null, propertyChanged: OnUrlChanged);

        public static string? GetUrl(BindableObject view) => (string?)view.GetValue(UrlProperty);

        public static void SetUrl(BindableObject view, string? value) => view.SetValue(UrlProperty, value);

        private static void OnUrlChanged(BindableObject bindable, object? oldValue, object? newValue)
        {
            if (bindable is not Image image)
                return;
            if (newValue is not string url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                image.Source = null;
                return;
            }

            if (RemoteImageCache.CachedFile(uri) is { } cached)
            {
                image.Source = ImageSource.FromFile(cached);
                return;
            }

            image.Source = null;
            _ = ShowWhenDownloadedAsync(image, url, uri);
        }

        private static async Task ShowWhenDownloadedAsync(Image image, string url, Uri uri)
        {
            var path = await RemoteImageCache.GetFileAsync(uri).ConfigureAwait(false);
            if (path is null)
                return;
            var dispatcher = image.Dispatcher ?? Application.Current?.Dispatcher;
            dispatcher?.Dispatch(() =>
            {
                if (GetUrl(image) == url)
                    image.Source = ImageSource.FromFile(path);
            });
        }
    }
}
