using Dan.Proxy.Interfaces;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Dan.Proxy
{
    public class Proxy
    {
        private readonly ILogger _logger;
        private readonly IDanProxyService _proxyService;

        public Proxy(ILoggerFactory loggerFactory, IDanProxyService danProxyService)
        {
            _logger = loggerFactory.CreateLogger<Proxy>();
            _proxyService = danProxyService;
        }

        [Function("DanProxy")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequestData req)
        {
            if (req.Query["url"] == null)
            {
                var response = req.CreateResponse(HttpStatusCode.BadRequest);
                await response.WriteStringAsync("Invalid or missing url provided");
                return response;
            }

            _logger.LogInformation($"Proxy request to {req.Query["url"]}");
            return await _proxyService.ProxyRequest(req);
        }
    }
}
