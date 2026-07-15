using System.Diagnostics;

namespace TradingEngine.Tests.Simulation.Harness;

public static class CtraderProcessGuard
{
    public static void KillStrays()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("ctrader-cli"))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch { }
            }
        }
        catch { }
    }

    public static int StrayCount()
    {
        try
        {
            return Process.GetProcessesByName("ctrader-cli").Length;
        }
        catch
        {
            return -1;
        }
    }
}
