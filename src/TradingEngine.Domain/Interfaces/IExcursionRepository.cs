namespace TradingEngine.Domain;

/// <summary>P3.1: persists the recorded MAE/MFE path for a trade, keyed by (RunId, PositionId). One row
/// per closed trade the venue recorded a path for -- a separate table from TradeResult, not a column on
/// it, since a path is a few hundred bytes of compact JSON, not a queryable trade field.</summary>
public interface IExcursionRepository
{
    Task SaveAsync(string runId, Guid positionId, string pathJson, CancellationToken ct);
    Task<string?> GetAsync(string runId, Guid positionId, CancellationToken ct);
}
