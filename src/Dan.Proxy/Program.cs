using Dan.Proxy;
using Dan.Proxy.Config;
using Dan.Proxy.Interfaces;
using Dan.Proxy.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.Configure<DanProxySettings>(context.Configuration.GetSection("DanProxySettings"));
        services.AddSingleton<IDanProxyService, DanProxyService>();

        services.AddHttpClient(Constants.DanProxyHttpClient);
    })
    .Build();

host.Run();
