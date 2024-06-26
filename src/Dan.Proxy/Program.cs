using Dan.Proxy;
using Dan.Proxy.Config;
using Dan.Proxy.Interfaces;
using Dan.Proxy.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((config) =>
    {
        config.AddJsonFile("host.json", optional: true);
        config.AddJsonFile("worker.json", optional: true);
    })
    .ConfigureServices((context, services) =>
    {
        var configurationRoot = context.Configuration;
        services.Configure<DanProxySettings>(configurationRoot);

        // services.Configure<DanProxySettings>(context.Configuration);
        services.AddTransient<IDanProxyService, DanProxyService>();
        services.AddHttpClient(Constants.DanProxyHttpClient);
    })
    .Build();

host.Run();
