namespace TradingEngine.Tests.Unit.SignalGateTests;

[Trait("Category", "SignalGate")]
public sealed class SignalGateServiceTests
{
    private static readonly DateTime T0 = new(2024, 6, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2024, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OnBar_SameTimestampTwice_DecrementsOnce()
    {
        var gate = new SignalGateService();
        gate.OnPositionClosed("s1", "EURUSD", TradeDirection.Long, "SL", T0);
        // Default ReentryOptions: CooldownBarsAfterSl=5 => 5-bar cooldown

        // 5 calls with same timestamp => only 1 unique bar tick
        gate.OnBar(T1); gate.OnBar(T1); gate.OnBar(T1); gate.OnBar(T1); gate.OnBar(T1);

        // 4 bars must remain => Check still blocked
        gate.Check("s1", "EURUSD", TradeDirection.Long, T1).Allowed.Should().BeFalse();
    }

    [Fact]
    public void OnBar_UniqueTimestamps_DecrementsFully()
    {
        var gate = new SignalGateService();
        gate.OnPositionClosed("s1", "EURUSD", TradeDirection.Long, "SL", T0);

        // 5 unique timestamps => cooldown fully expires
        var t = T1;
        for (int i = 0; i < 5; i++)
        {
            gate.OnBar(t);
            t = t.AddHours(1);
        }

        gate.Check("s1", "EURUSD", TradeDirection.Long, t).Allowed.Should().BeTrue();
    }
}
