using Microsoft.Extensions.Logging;

namespace TradingEngine.Services;

public sealed record OrderContext(Guid OrderId, decimal Lots, decimal RiskAmount, RiskProfile Profile);

public sealed class OrderDispatcher(
    IRiskManager riskManager,
    IRiskProfileResolver riskProfileResolver,
    ISymbolInfoRegistry symbolRegistry,
    Func<string, string, decimal> crossRateProvider,
    ILogger<OrderDispatcher> logger)
{
    public async Task<OrderContext?> DispatchAsync(
        TradeIntent intent, EquitySnapshot equity, decimal currentMid,
        IBrokerAdapter broker, CancellationToken ct)
    {
        var profile = riskProfileResolver.Resolve(intent.RiskProfileId);
        var violations = riskManager.Validate(intent, equity, profile, currentMid);

        if (violations.Count > 0)
        {
            logger.LogWarning("Blocked. Strategy={Strategy} Symbol={Symbol} Violations={V}",
                intent.StrategyId, intent.Symbol, string.Join(", ", violations.Select(v => v.Code)));
            return null;
        }

        var symbolInfo = symbolRegistry.Get(intent.Symbol);
        var entryPrice = intent.LimitPrice ?? new Price(currentMid);
        var slDistance = PipCalculator.Distance(entryPrice, intent.StopLoss, symbolInfo);
        var pipValue = PipCalculator.PipValuePerLot(symbolInfo, entryPrice.Value, crossRateProvider);
        var lots = riskManager.CalculateLotSize(intent, equity, profile, currentMid);
        var riskAmount = (decimal)slDistance.Value * pipValue * lots;

        if (!riskManager.ValidateBudgetEntry(riskAmount, equity, riskAmount))
        {
            var riskPerLot = (decimal)slDistance.Value * pipValue;
            var originalLots = lots;
            while (lots > symbolInfo.MinLots)
            {
                lots = Math.Max(lots * 0.5m, symbolInfo.MinLots);
                lots = Math.Floor(lots / symbolInfo.LotStep) * symbolInfo.LotStep;
                if (lots < symbolInfo.MinLots) break;
                riskAmount = riskPerLot * lots;
                if (riskManager.ValidateBudgetEntry(riskAmount, equity, riskAmount))
                {
                    logger.LogWarning("Downsized: Strategy={Strategy} Symbol={Symbol} lots {Original}→{New} for budget",
                        intent.StrategyId, intent.Symbol, originalLots, lots);
                    break;
                }
            }
            if (lots < symbolInfo.MinLots || !riskManager.ValidateBudgetEntry(riskAmount, equity, riskAmount))
            {
                logger.LogWarning("BudgetBlocked: Strategy={Strategy} Symbol={Symbol} lots={Lots} risk={Risk:F2}",
                    intent.StrategyId, intent.Symbol, originalLots, riskAmount);
                return null;
            }
        }

        logger.LogInformation("Order: Strategy={Strategy} Symbol={Symbol} Dir={Dir} Lots={Lots} Entry={Entry:F5} SL={SL:F5}",
            intent.StrategyId, intent.Symbol, intent.Direction, lots, entryPrice.Value, intent.StopLoss.Value);

        var orderReq = new OrderRequest(intent, lots, intent.Symbol, intent.Direction, OrderType.Market, intent.LimitPrice);
        var orderId = await broker.SubmitOrderAsync(orderReq, ct);

        return new OrderContext(orderId, lots, riskAmount, profile);
    }
}
