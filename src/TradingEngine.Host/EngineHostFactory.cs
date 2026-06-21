using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Host;

public static class EngineHostFactory
{
    public static IHost Create(EngineHostOptions options)
    {
        return Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l
                .SetMinimumLevel(options.MinLogLevel)
                .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning))
            .ConfigureServices((_, services) =>
            {
                services.AddEngineHost(options);
            })
            .Build();
    }

    public static void WireEventHandlers(IHost host)
    {
        var eventBus = host.Services.GetRequiredService<IEventBus>();
        eventBus.Subscribe<EquityUpdated>(
            host.Services.GetRequiredService<EquityPersistenceHandler>());
        eventBus.Subscribe<TradeClosed>(
            host.Services.GetRequiredService<TradePersistenceHandler>());
        eventBus.Subscribe<BarIngested>(
            host.Services.GetRequiredService<BarPersistenceHandler>());
    }

    public static void WireRiskRules(IHost host)
    {
        // iter-38 B3: delegate to the single source of truth in EngineHostWireExtensions.
        host.WireRiskRules(host.Services.GetRequiredService<LoadedConfig>());
    }
}
