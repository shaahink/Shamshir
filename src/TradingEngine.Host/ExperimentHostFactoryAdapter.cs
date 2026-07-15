using Microsoft.Extensions.Hosting;

namespace TradingEngine.Host;

public sealed class ExperimentHostFactoryAdapter : IExperimentHostFactory
{
    public IHost Create(EngineHostOptions options) => EngineHostFactory.Create(options);
    public void WireEventHandlers(IHost host) => EngineHostFactory.WireEventHandlers(host);
    public void WireRiskRules(IHost host) => EngineHostFactory.WireRiskRules(host);
}
