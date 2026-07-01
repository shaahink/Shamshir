namespace TradingEngine.Infrastructure.Persistence.Entities;

/// <summary>
/// iter-38 (owner decision D5). Every persisted entity carries creation + last-update timestamps. Stamped
/// automatically by <c>AuditStampInterceptor</c> on SaveChanges (CreatedAtUtc on insert, UpdatedAtUtc always),
/// surfaced in the UI ("Created" column/field) for debug + provenance.
///
/// TODO(iter-38 T1): retrofit ALL existing entities to implement this and add the two columns —
///   TradeResultEntity, OrderEntity, PositionEntity, EngineEventEntity, EquitySnapshotEntity, BarEntity,
///   BacktestRunEntity, ExperimentEntity, ExperimentRunEntity, RiskProfileEntity (has UpdatedAtUtc),
///   PropFirmRuleSetEntity (has UpdatedAtUtc), GovernorOptionsEntity (has UpdatedAtUtc), DatasetEntity,
///   ConfigSetEntity, JournalEntryEntity, StrategyConfigEntity (has UpdatedAtUtc), AddOnPackEntity (done).
/// Then fold the columns into the single P0-A1 EF regen. Arch test asserts every entity implements this.
/// </summary>
public interface IAuditableEntity
{
    DateTime CreatedAtUtc { get; set; }
    DateTime UpdatedAtUtc { get; set; }
}
