using Microsoft.Extensions.Logging;
using TradingEngine.Domain.Events;

namespace TradingEngine.Web.Services;

public sealed class ProtectionLedgerWriter : IEventHandler<GovernorStateChanged>
{
    private readonly ILogger<ProtectionLedgerWriter> _logger;

    public ProtectionLedgerWriter(ILogger<ProtectionLedgerWriter> logger)
    {
        _logger = logger;
    }

    public Task HandleAsync(GovernorStateChanged evt, CancellationToken ct)
    {
        _logger.LogInformation("ProtectionLedger: {From}→{To} reason={Reason}", evt.From, evt.To, evt.Reason);
        return Task.CompletedTask;
    }
}
