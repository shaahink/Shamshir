using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingEngine.Engine;

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
        var violations = riskManager.Validate(intent, equity, profile, currentMid);

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

        var symbolInfo = symbolRegistry.Get(intent.Symbol);
        var entryPrice = intent.LimitPrice ?? new Price(currentMid);
        var slDistance = PipCalculator.Distance(entryPrice, intent.StopLoss, symbolInfo);
        var pipValue = PipCalculator.PipValuePerLot(symbolInfo, entryPrice.Value, crossRateProvider);
        var lots = riskManager.CalculateLotSize(intent, equity, profile, currentMid);

        var maxDailyLossPercent = (decimal)profile.MaxDailyDrawdownPercent;
        var maxTotalLossPercent = (decimal)profile.MaxTotalDrawdownPercent;

        var guardResult = RiskGate.ProjectWorstCase(
            equity.Equity,
            equity.DailyStartEquity,
            equity.Balance,
            maxDailyLossPercent,
            maxTotalLossPercent,
            "Fixed",
            (decimal)slDistance.Value,
            lots,
            pipValue,
            openPositions);

        if (guardResult != RiskGate.Passed)
        {
            logger.LogWarning("RiskGate blocked. Strategy={Strategy} Symbol={Symbol} Guard={Guard}",
                intent.StrategyId, intent.Symbol, guardResult);
            decisionJournal.Record(new DecisionRecord(
                runContext.RunId,
                equity.TimestampUtc,
                0,
                intent.Symbol.Value,
                intent.StrategyId,
                null,
                "OrderRejected",
                guardResult,
                null,
                $"Worst-case DD projection breach: {guardResult}",
                JsonSerializer.Serialize(new { lots, projectedGuard = guardResult })));
            return null;
        }
        var riskAmount = (decimal)slDistance.Value * pipValue * lots;

        var perTradeRiskAmount = equity.Equity * (decimal)profile.RiskPerTradePercent;

        if (!riskManager.ValidateBudgetEntry(riskAmount, equity, perTradeRiskAmount))
        {
            var riskPerLot = (decimal)slDistance.Value * pipValue;
            var originalLots = lots;
            while (lots > symbolInfo.MinLots)
            {
                lots = Math.Max(lots * 0.5m, symbolInfo.MinLots);
                lots = Math.Floor(lots / symbolInfo.LotStep) * symbolInfo.LotStep;
                if (lots < symbolInfo.MinLots) break;
                riskAmount = riskPerLot * lots;
                if (riskManager.ValidateBudgetEntry(riskAmount, equity, perTradeRiskAmount))
                {
                    logger.LogWarning("Downsized: Strategy={Strategy} Symbol={Symbol} lots {Original}→{New} for budget",
                        intent.StrategyId, intent.Symbol, originalLots, lots);
                    break;
                }
            }
            if (lots < symbolInfo.MinLots || !riskManager.ValidateBudgetEntry(riskAmount, equity, perTradeRiskAmount))
            {
                logger.LogWarning("BudgetBlocked: Strategy={Strategy} Symbol={Symbol} lots={Lots} risk={Risk:F2}",
                    intent.StrategyId, intent.Symbol, originalLots, riskAmount);
                decisionJournal.Record(new DecisionRecord(
                    runContext.RunId,
                    equity.TimestampUtc,
                    0,
                    intent.Symbol.Value,
                    intent.StrategyId,
                    null,
                    "OrderRejected",
                    "BudgetBlocked",
                    null,
                    $"Budget exceeded: lots={originalLots:F4} risk={riskAmount:F2}",
                    JsonSerializer.Serialize(new { originalLots, riskAmount })));
                return null;
            }
        }

        logger.LogInformation("Order: Strategy={Strategy} Symbol={Symbol} Dir={Dir} Lots={Lots} Entry={Entry:F5} SL={SL:F5}",
            intent.StrategyId, intent.Symbol, intent.Direction, lots, entryPrice.Value, intent.StopLoss.Value);

        var orderReq = new OrderRequest(intent, lots, intent.Symbol, intent.Direction, OrderType.Market, intent.LimitPrice);
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
            $"Order accepted: lots={lots:F4} risk={riskAmount:F2}",
            JsonSerializer.Serialize(new { lots, riskAmount, entryPrice = entryPrice.Value, sl = intent.StopLoss.Value })));

        return new OrderContext(orderId, lots, riskAmount, profile);
    }
}
