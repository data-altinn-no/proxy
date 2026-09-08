using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Web;
using Dan.Proxy.Config;
using Dan.Proxy.Interfaces;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Security.Cryptography.X509Certificates;
using static System.Runtime.InteropServices.JavaScript.JSType;

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

            if (_settings.DebugMode)
            {
                _logger.LogInformation($"Debug mode enabled - IgnoreCertificateValidation: {_settings.IgnoreCertificateValidation},  CustomCertificateHeaderName: {_settings.CustomCertificateHeaderName}, IgnoredHeaders: { string.Join(",", _settings.IgnoredHeaders)}");
                foreach (var header in incomingRequest.Headers)
                {
                    _logger.LogInformation($"Incoming::: header {header.Key} : headerName: {string.Join(",", header.Value.ToArray())}");
                }
            }

            var decodedUrl = HttpUtility.UrlDecode(incomingRequest.Query["url"]?.ToString());
            var url = "https://" + decodedUrl;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var targetUri))
            {
                var response = incomingRequest.CreateResponse(HttpStatusCode.BadRequest);
                await response.WriteStringAsync("Invalid url provided");
                return response;
            }

            var targetHost = targetUri.Host;
            var ignoreCertValidationForHost = _settings.IgnoreCertificateValidationHostsList
                .Any(h => h.Equals(targetHost, StringComparison.OrdinalIgnoreCase));

            if (incomingRequest.Headers.TryGetValues(_settings.CustomCertificateHeaderName, out var certHeaders))
            {
                _logger.LogInformation("Client certificate provided in header");
                var clientCert = new X509Certificate2(Convert.FromBase64String(certHeaders.Single())); //there should only be 1 or 0
                var handler = new HttpClientHandler();
                handler.ClientCertificates.Add(clientCert);
                client = new HttpClient(handler);
            }   
            else if (_settings.IgnoreCertificateValidation || ignoreCertValidationForHost)
            {
                _logger.LogInformation("Ignoring certificate validation");

                var handler = new HttpClientHandler();
                handler.ClientCertificateOptions = ClientCertificateOption.Manual;
                handler.ServerCertificateCustomValidationCallback =
                    (httpRequestMessage, cert, cetChain, policyErrors) =>
                    {
                        // Global bypass er eksplisitt skrudd på - gjelder alt.
                        if (_settings.IgnoreCertificateValidation)
                        {
                            return true;
                        }

                        // Host-spesifikk bypass: ignorer kun sertifikatfeil for det
                        // konkrete alltidlistede hostet. Hvis requesten (f.eks. via en
                        // redirect) faktisk går mot et annet host, skal normal validering
                        // gjelde for det hostet - ellers kan et alltidlistet host
                        // "smitte" tillit videre til et vilkårlig annet host.
                        var requestHost = httpRequestMessage.RequestUri?.Host;
                        if (!string.IsNullOrEmpty(requestHost)
                            && _settings.IgnoreCertificateValidationHostsList
                                .Any(h => h.Equals(requestHost, StringComparison.OrdinalIgnoreCase)))
                        {
                            return true;
                        }

                        return policyErrors == System.Net.Security.SslPolicyErrors.None;
                    };

                client = new HttpClient(handler);
            }
            
            else
            {
                _logger.LogInformation("Running standard proxy setup");
                client = _httpClientFactory.CreateClient(Constants.DanProxyHttpClient); 
            }

            var outgoingRequest = new HttpRequestMessage(HttpMethod.Parse(incomingRequest.Method), targetUri);

            if (outgoingRequest.Method != HttpMethod.Get)
            {
                using StreamReader reader = new StreamReader(incomingRequest.Body);
                string requestBody = await reader.ReadToEndAsync();

                _logger.LogInformation($"Incoming::: body {requestBody}");

                // Default to text/plain if nothing found, as that is the default string content header
                var contentHeader = incomingRequest.Headers.TryGetValues("Content-Type", out var contentTypes) ? 
                    contentTypes.FirstOrDefault() ?? "text/plain" : 
                    "text/plain";

                //remove any charset information on body if it exists
                if (contentHeader.IndexOf(';') > 0)
                {
                    contentHeader = contentHeader.Substring(0, contentHeader.IndexOf(';'));
                }

                //remove any charset information on body
                if (!string.IsNullOrEmpty(requestBody))
                {
                    outgoingRequest.Content = new StringContent(requestBody, Encoding.UTF8, contentHeader);
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





