namespace TradingEngine.Services;

public static class SessionDetector
{
    public const string Asian = "Asian";
    public const string London = "London";
    public const string NewYork = "NewYork";
    public const string AsianLondon = "Asian-London";
    public const string LondonNewYork = "London-NY";
    public const string Pacific = "Pacific";

    public static string Detect(DateTime utc)
    {
        var t = utc.TimeOfDay;
        if (t >= new TimeSpan(0, 0, 0) && t < new TimeSpan(8, 0, 0))
            return Asian;
        if (t >= new TimeSpan(8, 0, 0) && t < new TimeSpan(9, 0, 0))
            return AsianLondon;
        if (t >= new TimeSpan(9, 0, 0) && t < new TimeSpan(13, 0, 0))
            return London;
        if (t >= new TimeSpan(13, 0, 0) && t < new TimeSpan(17, 0, 0))
            return LondonNewYork;
        if (t >= new TimeSpan(17, 0, 0) && t < new TimeSpan(22, 0, 0))
            return NewYork;
        return Pacific;
    }
}
