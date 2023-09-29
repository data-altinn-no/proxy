using Dan.Proxy.Interfaces;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

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
            _logger.LogInformation($"Proxy request to {req.Query}");

            return await _proxyService.ProxyRequest(req);
        }
    }
}
