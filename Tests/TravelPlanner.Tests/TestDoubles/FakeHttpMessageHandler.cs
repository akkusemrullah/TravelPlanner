using Moq;
using Xunit;
using System.Net;
using System.Text;

namespace TravelPlanner.Tests
{
    /// <summary>Returns canned JSON for any request whose URL contains a registered fragment, and records every call.</summary>
    public sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly List<(string UrlFragment, HttpStatusCode Status, string Body)> _routes = new();

        public List<Uri> Requests { get; } = new();

        public FakeHttpMessageHandler Respond(string urlFragment, string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes.Add((urlFragment, status, json));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);

            var url = request.RequestUri!.ToString();
            var route = _routes.FirstOrDefault(r => url.Contains(r.UrlFragment, StringComparison.OrdinalIgnoreCase));

            var response = route.Body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") };

            return Task.FromResult(response);
        }

        public HttpClient CreateClient() => new(this);
    }
}
