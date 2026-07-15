using System;
using cAlgo.API;

namespace TestCbot;

[Robot(AccessRights = AccessRights.FullAccess)]
public sealed class TestCbot : Robot
{
    private const string Version = "1.0.0-test";
    private readonly Random _rng = new();
    private int _barCount;

    protected override void OnStart()
    {
        Print($"SHAMSHIR-TEST|v={Version}|{SymbolName}|{TimeFrame.ShortName}");
        MarketData.GetBars(TimeFrame, SymbolName).BarClosed += OnBar;
    }

    private void OnBar(BarClosedEventArgs args)
    {
        _barCount++;
        if (_barCount % 5 != 0) return;
        var d = _rng.Next(2) == 0 ? TradeType.Buy : TradeType.Sell;
        var v = _rng.Next(1, 4) * 10_000;
        ExecuteMarketOrder(d, SymbolName, v, "min", _rng.Next(15, 40), _rng.Next(15, 40));
        if (Positions.Count > 5) { foreach (var p in Positions) ClosePosition(p); }
    }

    protected override void OnStop() { Print($"SHAMSHIR-TEST|STOP|bars={_barCount}"); }
}
