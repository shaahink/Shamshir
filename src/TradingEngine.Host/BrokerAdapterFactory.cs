using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain.Interfaces;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.Venues.CTrader;
using TradingEngine.Infrastructure.Transport.NetMq;

namespace TradingEngine.Host;

public static class BrokerAdapterFactory
{
    public static IBrokerAdapter Create(EngineMode mode, double slipPips, IConfiguration config, IServiceProvider sp)
    {
        if (mode == EngineMode.Live || mode == EngineMode.Paper)
        {
            var dataPort = int.TryParse(config["Engine:Broker:NetMQ:DataPort"], out var dp) ? dp : 15555;
            var commandPort = int.TryParse(config["Engine:Broker:NetMQ:CommandPort"], out var cp) ? cp : 15556;
            var transport = new NetMqMessageTransport(
                $"tcp://127.0.0.1:{dataPort}", $"tcp://*:{commandPort}",
                sp.GetRequiredService<ILogger<NetMqMessageTransport>>());
            var adapter = new CTraderBrokerAdapter(transport,
                sp.GetRequiredService<ILogger<CTraderBrokerAdapter>>());
            adapter.OnSymbolSpec = spec =>
            {
                sp.GetRequiredService<ISymbolInfoRegistry>().UpsertVenueSpec(spec);

                // P4.4 (F44): persist venue economics so they outlive this process and reach the tape leg.
                if (sp.GetService<IVenueSymbolSpecStore>() is { } store)
                    _ = Task.Run(() => store.SaveAsync(spec));
            };
            return adapter;
        }

        return new SimulatedBrokerAdapter(
            sp.GetRequiredService<ISymbolInfoRegistry>(),
            sp.GetRequiredService<Func<string, string, decimal>>(),
            slippagePips: slipPips);
    }
}
