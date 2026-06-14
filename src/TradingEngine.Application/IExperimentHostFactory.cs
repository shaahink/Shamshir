using Microsoft.Extensions.Hosting;

namespace TradingEngine.Application;

public interface IExperimentHostFactory
{
    IHost Create(EngineHostOptions options);
    void WireEventHandlers(IHost host);
    void WireRiskRules(IHost host);
}
