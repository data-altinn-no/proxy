using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Web;
using Dan.Proxy.Config;
using Dan.Proxy.Interfaces;

namespace Dan.Proxy.Services
{
    public class DanProxyService : IDanProxyService
    {
        private readonly ILogger<DanProxyService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly DanProxySettings _settings;

        public DanProxyService(
            ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory, IOptions<DanProxySettings> settings)
        {
            _logger = loggerFactory.CreateLogger<DanProxyService>();
            _httpClientFactory = httpClientFactory;
            _settings = settings.Value;
        }

        public async Task<HttpResponseData> ProxyRequest(HttpRequestData incomingRequest)
        {
            var client = _httpClientFactory.CreateClient(Constants.DanProxyHttpClient);
            var url = "https://" + HttpUtility.UrlDecode(incomingRequest.Query["url"].ToString());

            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
            {
                var response = incomingRequest.CreateResponse(HttpStatusCode.BadRequest);
                await response.WriteStringAsync("Invalid url provided");
                return response;
            }

            var outgoingRequest = new HttpRequestMessage(HttpMethod.Get, url);

            if (incomingRequest.Headers.TryGetValues("Accept", out var acceptHeaders))
            {
                outgoingRequest.Headers.TryAddWithoutValidation("Accept", acceptHeaders.ToArray());
            }
            else
            {
                outgoingRequest.Headers.Add("Accept", "application/json");
            }

            if (incomingRequest.Headers.TryGetValues("Authorization", out var authHeaderValues))
            {
                outgoingRequest.Headers.Add("Authorization", authHeaderValues.ToArray());
            }

            try
            {
                var incomingResponse = await client.SendAsync(outgoingRequest);
                var outgoingResponse = incomingRequest.CreateResponse(incomingResponse.StatusCode);

                if (incomingResponse.Headers.TryGetValues("Content-Type", out var contentTypes))
                {
                    outgoingResponse.Headers.TryAddWithoutValidation("Content-Type", contentTypes.ToArray());
                }
                else
                {
                    outgoingResponse.Headers.Add("Content-Type", "application/json");
                }

                await (await incomingResponse.Content.ReadAsStreamAsync()).CopyToAsync(outgoingResponse.Body);

                return outgoingResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error proxying request");
                Console.WriteLine(ex);
                throw;
            }
        }
    }
}