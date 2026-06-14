using Microsoft.Extensions.Logging;
using TradingEngine.Domain.Events;

namespace TradingEngine.Host;

public sealed class EffectExecutor
{
    private readonly IBrokerAdapter _broker;
    private readonly IEventBus _eventBus;
    private readonly IDecisionJournal _decisionJournal;
    private readonly IEquitySink? _equitySink;
    private readonly IProgress<BacktestProgressEvent>? _progress;
    private readonly string _runId;
    private readonly IEngineClock _clock;
    private readonly ILogger<EffectExecutor> _logger;

    public EffectExecutor(
        IBrokerAdapter broker,
        IEventBus eventBus,
        IDecisionJournal decisionJournal,
        IEquitySink? equitySink,
        IProgress<BacktestProgressEvent>? progress,
        EngineRunContext runContext,
        IEngineClock clock,
        ILogger<EffectExecutor> logger)
    {
        _broker = broker;
        _eventBus = eventBus;
        _decisionJournal = decisionJournal;
        _equitySink = equitySink;
        _progress = progress;
        _runId = runContext.RunId;
        _clock = clock;
        _logger = logger;
    }

    public async Task ExecuteAsync(EngineEffect effect, CancellationToken ct)
    {
        switch (effect)
        {
            case SubmitOrder submit:
                var intent = new TradeIntent(submit.Symbol, submit.Direction, OrderType.Market,
                    submit.LimitPrice, submit.StopLoss, submit.TakeProfit,
                    submit.StrategyId, "standard", "", _clock.UtcNow);
                var orderReq = new OrderRequest(intent, submit.Lots, submit.Symbol, submit.Direction,
                    OrderType.Market, submit.LimitPrice);
                await _broker.SubmitOrderAsync(orderReq, ct);
                break;

            case ModifyStopLoss modSl:
                await _broker.ModifyOrderAsync(modSl.PositionId, modSl.NewStopLoss, null, ct);
                break;

            case ModifyTakeProfit modTp:
                await _broker.ModifyOrderAsync(modTp.PositionId, new Price(0), modTp.NewTakeProfit, ct);
                break;

            case CloseOpenPosition closePos:
                await _broker.ClosePositionAsync(closePos.PositionId, ct);
                _progress?.Report(new BacktestProgressEvent(_runId, "CLOSE",
                    $"Close position {closePos.PositionId} reason={closePos.Reason}", _clock.UtcNow));
                break;

            case RecordDecisionEvent record:
                _decisionJournal.Record(record.Decision);
                break;
        }
    }
}
