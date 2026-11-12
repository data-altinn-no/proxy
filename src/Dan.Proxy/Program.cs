using Azure.Monitor.OpenTelemetry.Exporter;
using Dan.Proxy;
using Dan.Proxy.Config;
using Dan.Proxy.Interfaces;
using Dan.Proxy.Services;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((config) =>
    {
        config.AddJsonFile("host.json", optional: true);
        config.AddJsonFile("worker.json", optional: true);
    })
    .ConfigureLogging(logging =>
    {
        logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
        });
    })
    .ConfigureServices((context, services) =>
    {
        // UseAzureMonitorExporter() throws at startup if no connection string is configured,
        // so it's gated here to keep the proxy working locally without one.
        var appInsightsConnectionString = context.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
        {
            services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddHttpClientInstrumentation())
                .UseAzureMonitorExporter(options => options.ConnectionString = appInsightsConnectionString)
                .UseFunctionsWorkerDefaults();
        }

        services.Configure<DanProxySettings>(context.Configuration);
        services.AddTransient<IDanProxyService, DanProxyService>();
        services.AddHttpClient(Constants.DanProxyHttpClient);
    })
    .Build();

host.Run();
