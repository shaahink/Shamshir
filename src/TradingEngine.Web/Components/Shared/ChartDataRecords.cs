namespace TradingEngine.Web.Components.Shared;

public record EquityDataPoint(DateTime Time, decimal Equity);
public record BarDataPoint(DateTime Time, decimal Open, decimal High, decimal Low, decimal Close);
public record MarkerData(DateTime Time, string Direction);
public record RegimeCell(DateTime Date, string Regime);
