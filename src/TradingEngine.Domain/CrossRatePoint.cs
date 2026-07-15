namespace TradingEngine.Domain;

/// <summary>
/// One observation of a currency's USD leg: how many USD one unit of it bought at <see cref="AtUtc"/>.
/// Cross rates are pivoted on USD so that adding a currency (or re-denominating the account) means
/// supplying one leg rather than a leg per pair.
/// </summary>
public readonly record struct CrossRatePoint(DateTime AtUtc, decimal UsdPerUnit);
