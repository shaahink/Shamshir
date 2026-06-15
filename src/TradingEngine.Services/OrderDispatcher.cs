using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Services;

public sealed record OrderContext(Guid OrderId, decimal Lots, decimal RiskAmount, RiskProfile Profile);

public sealed class OrderDispatcher(
    IRiskManager riskManager,
    IRiskProfileResolver riskProfileResolver,
    ISymbolInfoRegistry symbolRegistry,
    Func<string, string, decimal> crossRateProvider,
    IDecisionJournal decisionJournal,
    EngineRunContext runContext,
    ILogger<OrderDispatcher> logger)
{
    public async Task<OrderContext?> DispatchAsync(
        TradeIntent intent, EquitySnapshot equity, decimal currentMid,
        IBrokerAdapter broker, IReadOnlyList<ProjectedPosition> openPositions, CancellationToken ct)
    {
        var profile = riskProfileResolver.Resolve(intent.RiskProfileId);
        var symbolInfo = symbolRegistry.Get(intent.Symbol);
        var entryPrice = intent.LimitPrice ?? new Price(currentMid);
        var slDistance = PipCalculator.Distance(entryPrice, intent.StopLoss, symbolInfo);
        var pipValue = PipCalculator.PipValuePerLot(symbolInfo, entryPrice.Value, crossRateProvider);
        var lots = riskManager.CalculateLotSize(intent, equity, profile, currentMid);

        var violations = riskManager.ValidateOrder(
            intent, equity, profile, currentMid,
            symbolInfo, (decimal)slDistance.Value, pipValue, lots,
            openPositions, out var downsizedLots);

        if (violations.Count > 0)
        {
            var violationCodes = string.Join(", ", violations.Select(v => v.Code));
            logger.LogWarning("Blocked. Strategy={Strategy} Symbol={Symbol} Violations={V}",
                intent.StrategyId, intent.Symbol, violationCodes);
            decisionJournal.Record(new DecisionRecord(
                runContext.RunId,
                equity.TimestampUtc,
                0,
                intent.Symbol.Value,
                intent.StrategyId,
                null,
                "OrderRejected",
                violationCodes,
                null,
                $"Risk validation failed: {violationCodes}",
                JsonSerializer.Serialize(new { violations = violations.Select(v => new { v.Code, v.Message }) })));
            return null;
        }

        var finalLots = downsizedLots > 0 ? downsizedLots : lots;
        var riskAmount = (decimal)slDistance.Value * pipValue * finalLots;

        logger.LogInformation("Order: Strategy={Strategy} Symbol={Symbol} Dir={Dir} Lots={Lots} Entry={Entry:F5} SL={SL:F5}",
            intent.StrategyId, intent.Symbol, intent.Direction, finalLots, entryPrice.Value, intent.StopLoss.Value);

        var orderReq = new OrderRequest(intent, finalLots, intent.Symbol, intent.Direction, OrderType.Market, intent.LimitPrice);
        var orderId = await broker.SubmitOrderAsync(orderReq, ct);

        decisionJournal.Record(new DecisionRecord(
            runContext.RunId,
            equity.TimestampUtc,
            0,
            intent.Symbol.Value,
            intent.StrategyId,
            null,
            "OrderSubmitted",
            null,
            null,
            $"Order accepted: lots={finalLots:F4} risk={riskAmount:F2}",
            JsonSerializer.Serialize(new { lots = finalLots, riskAmount, entryPrice = entryPrice.Value, sl = intent.StopLoss.Value })));

        return new OrderContext(orderId, finalLots, riskAmount, profile);
    }
}
