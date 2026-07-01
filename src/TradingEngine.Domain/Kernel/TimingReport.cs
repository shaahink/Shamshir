namespace TradingEngine.Domain;

public sealed record TimingReport(
    long Bars,
    long EvaluateMs,
    long PumpMs,
    long CompleteBarMs,
    long JournalSteps)
{
    public long TotalEngineMs => EvaluateMs + PumpMs + CompleteBarMs;
    public double MeanBarMs => Bars > 0 ? (double)TotalEngineMs / Bars : 0;
    public double MeanEvaluateMs => Bars > 0 ? (double)EvaluateMs / Bars : 0;
    public double MeanPumpMs => Bars > 0 ? (double)PumpMs / Bars : 0;
    public double MeanCompleteBarMs => Bars > 0 ? (double)CompleteBarMs / Bars : 0;
    public double MeanJournalStepsPerBar => Bars > 0 ? (double)JournalSteps / Bars : 0;
}
