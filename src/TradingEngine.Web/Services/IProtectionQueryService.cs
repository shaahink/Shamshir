using TradingEngine.Web.Dtos.Protection;

namespace TradingEngine.Web.Services;

public interface IProtectionQueryService
{
    Task<IReadOnlyList<ProtectionDayResponse>> GetDaysAsync(string? runId, CancellationToken ct);
    Task<IReadOnlyList<ProtectionEntryResponse>> GetDayDetailsAsync(DateTime date, string? runId, CancellationToken ct);
}
