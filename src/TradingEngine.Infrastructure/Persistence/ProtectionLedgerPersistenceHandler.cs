using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain.Events;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence;

public sealed class ProtectionLedgerPersistenceHandler : IEventHandler<GovernorStateChanged>, IAsyncDisposable
{
    private readonly string _runId;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDecisionJournal _decisionJournal;
    private readonly ILogger<ProtectionLedgerPersistenceHandler> _logger;

    public ProtectionLedgerPersistenceHandler(
        EngineRunContext runContext,
        IServiceScopeFactory scopeFactory,
        IDecisionJournal decisionJournal,
        ILogger<ProtectionLedgerPersistenceHandler> logger)
    {
        _runId = runContext.RunId;
        _scopeFactory = scopeFactory;
        _decisionJournal = decisionJournal;
        _logger = logger;
    }

    public async Task HandleAsync(GovernorStateChanged evt, CancellationToken ct)
    {
        _logger.LogInformation("ProtectionLedger: {From}->{To} reason={Reason}", evt.From, evt.To, evt.Reason);

        _decisionJournal.Record(new DecisionRecord(
            _runId,
            evt.AtUtc,
            0,
            null,
            null,
            evt.From.ToString(),
            "GovernorStateChanged",
            null,
            evt.To.ToString(),
            evt.Reason,
            "{}"));

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            var today = evt.AtUtc.Date;
            var dailyLedger = await db.DailyProtectionLedgers
                .FirstOrDefaultAsync(l => l.RunId == _runId && l.Date == today, ct);

            if (dailyLedger is null)
            {
                dailyLedger = new DailyProtectionLedgerEntity
                {
                    Id = Guid.NewGuid(),
                    RunId = _runId,
                    Date = today,
                    FinalGovernorState = evt.To.ToString(),
                    BreachOccurred = evt.To == GovernorTradingState.HardStop || evt.To == GovernorTradingState.SoftStop,
                };
                db.DailyProtectionLedgers.Add(dailyLedger);
            }
            else
            {
                dailyLedger.FinalGovernorState = evt.To.ToString();
                if (evt.To == GovernorTradingState.HardStop || evt.To == GovernorTradingState.SoftStop)
                {
                    dailyLedger.BreachOccurred = true;
                }
            }

            var entry = new ProtectionLedgerEntryEntity
            {
                Id = Guid.NewGuid(),
                LedgerId = dailyLedger.Id,
                AtUtc = evt.AtUtc,
                Category = "GovernorStateChange",
                Reason = evt.Reason,
            };
            db.ProtectionLedgerEntries.Add(entry);

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProtectionLedgerPersistenceHandler: failed to persist entry");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
