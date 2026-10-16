using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TravelPlanner.Helpers;
using TravelPlanner.Models;

namespace TravelPlanner.Services.Sync
{
    /// <summary>Why a cloud call couldn't be made or failed.</summary>
    public enum SyncErrorKind
    {
        /// <summary>No connection / server unreachable / timeout - try again later.</summary>
        Network,
        /// <summary>Not signed in, or the token can't be refreshed right now.</summary>
        NotSignedIn,
        /// <summary>The server refused (4xx) - a bug or a permission problem, not worth retrying blindly.</summary>
        Rejected,
        /// <summary>Server error (5xx).</summary>
        Server
    }

    public sealed class SyncException : Exception
    {
        public SyncException(SyncErrorKind kind, string message, Exception? inner = null, int? status = null, string? serverCode = null)
            : base(message, inner)
        {
            Kind = kind;
            Status = status;
            ServerCode = serverCode;
        }

        public SyncErrorKind Kind { get; }

        public int? Status { get; }

        /// <summary>The database error code of a refused RPC (e.g. P0002 "not found", 42501 "not allowed") - G6.</summary>
        public string? ServerCode { get; }
    }

    /// <summary>The few Supabase calls the sync engine needs (G4/G5): RPCs and Storage objects.</summary>
    public interface ISupabaseClient
    {
        /// <summary>POST /rest/v1/rpc/&lt;function&gt; with <paramref name="arguments"/> as the JSON body.</summary>
        Task<JsonElement> RpcAsync(string function, JsonObject arguments, CancellationToken cancellationToken = default);

        /// <summary>
        /// POST /rest/v1/rpc/&lt;function&gt; with only the publishable key - for the few functions open to everyone
        /// (account_status), callable even when the session is already gone.
        /// </summary>
        Task<JsonElement> PublicRpcAsync(string function, JsonObject arguments, CancellationToken cancellationToken = default);

        /// <summary>POST /functions/v1/&lt;name&gt; (an Edge Function) with the user's token and <paramref name="body"/> as JSON.</summary>
        Task<JsonElement> InvokeFunctionAsync(string name, JsonObject body, CancellationToken cancellationToken = default);

        /// <summary>Uploads (or replaces) an object in a Storage bucket.</summary>
        Task UploadAsync(string bucket, string path, Stream content, string? contentType, CancellationToken cancellationToken = default);

        /// <summary>Downloads an object into <paramref name="target"/>.</summary>
        Task DownloadAsync(string bucket, string path, Stream target, CancellationToken cancellationToken = default);

        /// <summary>
        /// Paths of the files directly inside the "folder" <paramref name="prefix"/> (e.g. "trips/&lt;tripSyncId&gt;"),
        /// as full object paths. Only what the account may read is listed.
        /// </summary>
        Task<IReadOnlyList<string>> ListAsync(string bucket, string prefix, CancellationToken cancellationToken = default);

        /// <summary>Deletes objects (full paths inside the bucket); paths that don't exist are ignored by the server.</summary>
        Task DeleteAsync(string bucket, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);
    }

    public class SupabaseClient : ISupabaseClient
    {
        /// <summary>Name of the HttpClient registered in MauiProgram (BaseAddress = project URL).</summary>
        public const string HttpClientName = "supabase-api";

        private readonly Func<HttpClient> _createClient;
        private readonly IAuthService _auth;

        public SupabaseClient(IHttpClientFactory httpClientFactory, IAuthService auth)
            : this(() => httpClientFactory.CreateClient(HttpClientName), auth)
        {
        }

        internal SupabaseClient(Func<HttpClient> createClient, IAuthService auth)
        {
            _createClient = createClient;
            _auth = auth;
        }

        public async Task<JsonElement> RpcAsync(string function, JsonObject arguments, CancellationToken cancellationToken = default)
        {
            using var request = await CreateRequestAsync(HttpMethod.Post, $"rest/v1/rpc/{function}", cancellationToken);
            request.Content = new StringContent(arguments.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
                return default;

            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }

        public async Task<JsonElement> PublicRpcAsync(string function, JsonObject arguments, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"rest/v1/rpc/{function}");
            request.Headers.Add("apikey", SupabaseConfig.PublishableKey);
            request.Content = new StringContent(arguments.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
                return default;

            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }

        public async Task<JsonElement> InvokeFunctionAsync(string name, JsonObject body, CancellationToken cancellationToken = default)
        {
            using var request = await CreateRequestAsync(HttpMethod.Post, $"functions/v1/{name}", cancellationToken);
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
                return default;

            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }

        public async Task UploadAsync(string bucket, string path, Stream content, string? contentType, CancellationToken cancellationToken = default)
        {
            using var request = await CreateRequestAsync(HttpMethod.Post, $"storage/v1/object/{bucket}/{path}", cancellationToken);
            request.Headers.Add("x-upsert", "true");
            request.Content = new StreamContent(content);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
                ? mediaType
                : new MediaTypeHeaderValue("application/octet-stream");

            using var response = await SendAsync(request, cancellationToken);
        }

        public async Task DownloadAsync(string bucket, string path, Stream target, CancellationToken cancellationToken = default)
        {
            using var request = await CreateRequestAsync(HttpMethod.Get, $"storage/v1/object/{bucket}/{path}", cancellationToken);
            using var response = await SendAsync(request, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await source.CopyToAsync(target, cancellationToken);
        }

        public async Task<IReadOnlyList<string>> ListAsync(string bucket, string prefix, CancellationToken cancellationToken = default)
        {
            const int pageSize = 1000;
            var folder = prefix.Trim('/');
            var paths = new List<string>();

            for (var offset = 0; ; offset += pageSize)
            {
                using var request = await CreateRequestAsync(HttpMethod.Post, $"storage/v1/object/list/{bucket}", cancellationToken);
                var body = new JsonObject { ["prefix"] = folder, ["limit"] = pageSize, ["offset"] = offset };
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

                using var response = await SendAsync(request, cancellationToken);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "[]" : text);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    break;

                var count = 0;
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    count++;
                    // Sub-folders come back with a null id; files of a trip sit directly in its folder.
                    if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Null)
                        continue;
                    if (item.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } file)
                        paths.Add($"{folder}/{file}");
                }

                if (count < pageSize)
                    break;
            }

            return paths;
        }

        public async Task DeleteAsync(string bucket, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
        {
            foreach (var chunk in paths.Chunk(1000))
            {
                using var request = await CreateRequestAsync(HttpMethod.Delete, $"storage/v1/object/{bucket}", cancellationToken);
                var prefixes = new JsonArray();
                foreach (var path in chunk)
                    prefixes.Add(path);
                request.Content = new StringContent(new JsonObject { ["prefixes"] = prefixes }.ToJsonString(), Encoding.UTF8, "application/json");

                using var response = await SendAsync(request, cancellationToken);
            }
        }

        private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string path, CancellationToken cancellationToken)
        {
            string? token;
            try
            {
                token = await _auth.GetAccessTokenAsync(cancellationToken);
            }
            catch (AuthException ex)
            {
                throw new SyncException(SyncErrorKind.NotSignedIn, ex.Message, ex);
            }

            if (token is null)
                throw new SyncException(SyncErrorKind.NotSignedIn, "No access token (guest or offline).");

            var request = new HttpRequestMessage(method, path);
            request.Headers.Add("apikey", SupabaseConfig.PublishableKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken,
                                                          HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
        {
            var client = _createClient();
            client.BaseAddress ??= new Uri(SupabaseConfig.Url.TrimEnd('/') + "/");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, completion, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new SyncException(SyncErrorKind.Network, ex.Message, ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SyncException(SyncErrorKind.Network, "Timed out.", ex);
            }

            if (response.IsSuccessStatusCode)
                return response;

            var status = (int)response.StatusCode;
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch
            {
                body = string.Empty;
            }
            response.Dispose();

            var kind = status >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests
                ? SyncErrorKind.Server
                : status == 401 ? SyncErrorKind.NotSignedIn : SyncErrorKind.Rejected;
            throw new SyncException(kind, $"HTTP {status}: {Truncate(body)}", status: status, serverCode: ErrorCodeOf(body));
        }

        /// <summary>
        /// PostgREST error bodies look like {"code":"P0002","message":"…"}; our Edge Functions answer
        /// {"error":"quota_exceeded"} - either code is returned.
        /// </summary>
        internal static string? ErrorCodeOf(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return null;
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return null;
                if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                    return code.GetString();
                return root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Truncate(string text) => text.Length <= 300 ? text : text[..300];
    }
}
