namespace TradingEngine.Domain;

public record ComplianceResult(bool Passed, IReadOnlyList<string> Violations, ComplianceSeverity Severity);

public enum ComplianceSeverity { None, Warning, Block, HardStop }
