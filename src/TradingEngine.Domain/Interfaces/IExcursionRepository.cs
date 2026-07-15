namespace TradingEngine.Domain;

/// <summary>P3.1: persists the recorded MAE/MFE path for a trade, keyed by (RunId, PositionId). One row
/// per closed trade the venue recorded a path for -- a separate table from TradeResult, not a column on
/// it, since a path is a few hundred bytes of compact JSON, not a queryable trade field.</summary>
public interface IExcursionRepository
{
    Task SaveAsync(string runId, Guid positionId, string pathJson, string? sessionLabel, CancellationToken ct);
    Task<string?> GetAsync(string runId, Guid positionId, CancellationToken ct);

    /// <summary>P4.5.7: fetch all excursion paths for a run (replace the hand-typed GUID-pair flow).</summary>
    Task<IReadOnlyList<(string RunId, Guid PositionId, string PathJson)>> GetByRunAsync(string runId, CancellationToken ct);
}
