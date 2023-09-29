using Microsoft.Azure.Functions.Worker.Http;

namespace Dan.Proxy.Interfaces;
public interface IDanProxyService
{
    /// <summary>
    /// Proxy function that we use in non-staging environments to access to data sources that are firewalled
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    public Task<HttpResponseData> ProxyRequest(HttpRequestData request);
}