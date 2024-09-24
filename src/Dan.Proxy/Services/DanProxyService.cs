using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Web;
using Dan.Proxy.Config;
using Dan.Proxy.Interfaces;
using System.Reflection.PortableExecutable;

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

            if (_settings.DebugMode)
            _logger.LogInformation($"Settings ignoredheaders {string.Join(",", _settings.IgnoredHeaders)} debugmode: {_settings.DebugMode}");
        }

        private bool IsEligibleHeader(string headerName)
        {
            if (headerName.Trim().Equals("Host", StringComparison.OrdinalIgnoreCase) || headerName.Trim().StartsWith("x-", StringComparison.OrdinalIgnoreCase))
                return false;

            if (_settings.IgnoredHeaders.Length > 0 && _settings.IgnoredHeaders.Contains(headerName))
            {
                return false;
            }

            return true;
        }

        public async Task<HttpResponseData> ProxyRequest(HttpRequestData incomingRequest)
        {
            HttpClient client;

            if (_settings.IgnoreCertificateValidation)
            {
                var handler = new HttpClientHandler();
                handler.ClientCertificateOptions = ClientCertificateOption.Manual;
                handler.ServerCertificateCustomValidationCallback =
                    (httpRequestMessage, cert, cetChain, policyErrors) =>
                    {
                        return true;
                    };

               client = new HttpClient(handler);
            }
            else
            {
                client = _httpClientFactory.CreateClient(Constants.DanProxyHttpClient);
            }
            
            
            foreach (var header in incomingRequest.Headers)
            {
                _logger.LogInformation($"Incoming::: header {header.Key} : headerName: {string.Join(",", header.Value.ToArray())}");
            }
            
            var url = "https://" + HttpUtility.UrlDecode(incomingRequest.Query["url"].ToString());

            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
            {
                var response = incomingRequest.CreateResponse(HttpStatusCode.BadRequest);
                await response.WriteStringAsync("Invalid url provided");
                return response;
            }

            if (_settings.DebugMode)
            {
                foreach (var header in incomingRequest.Headers)
                {
                    _logger.LogInformation($"Incoming::: header {header.Key} : headerName: {string.Join(",", header.Value.ToArray())}");
                }
            }

            var outgoingRequest = new HttpRequestMessage(HttpMethod.Parse(incomingRequest.Method), url);

            if (outgoingRequest.Method != HttpMethod.Get)
            {
                using StreamReader reader = new StreamReader(incomingRequest.Body);
                string requestBody = await reader.ReadToEndAsync();

                _logger.LogInformation($"Incoming::: body {requestBody}");

                if (!string.IsNullOrEmpty(requestBody))
                {
                    outgoingRequest.Content = new StringContent(requestBody);
                }
            }

            try
            {

                foreach (var header in incomingRequest.Headers.Where(x => IsEligibleHeader(x.Key)))
                {
                    outgoingRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());

                    if (_settings.DebugMode)
                    {
                        _logger.LogInformation($"Outgoing::: header {header.Key} : {string.Join(",", header.Value.ToArray())}");
                    }
                }

                if (!incomingRequest.Headers.TryGetValues("Accept", out var acceptHeaders))
                {
                    outgoingRequest.Headers.Add("Accept", "application/json");
                }

                var incomingResponse = await client.SendAsync(outgoingRequest);

                _logger.LogInformation($"Response from {url} is {incomingResponse.StatusCode}");

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





