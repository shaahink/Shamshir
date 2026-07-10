using FluentAssertions;
using TradingEngine.Services;
using Xunit;

namespace TradingEngine.Tests.Unit.Services;

public class SessionDetectorTests
{
    [Theory]
    [InlineData("2026-01-15T00:00:00Z", SessionDetector.Asian)]
    [InlineData("2026-01-15T03:30:00Z", SessionDetector.Asian)]
    [InlineData("2026-01-15T07:59:59Z", SessionDetector.Asian)]
    [InlineData("2026-01-15T08:00:00Z", SessionDetector.AsianLondon)]
    [InlineData("2026-01-15T08:30:00Z", SessionDetector.AsianLondon)]
    [InlineData("2026-01-15T08:59:59Z", SessionDetector.AsianLondon)]
    [InlineData("2026-01-15T09:00:00Z", SessionDetector.London)]
    [InlineData("2026-01-15T11:00:00Z", SessionDetector.London)]
    [InlineData("2026-01-15T12:59:59Z", SessionDetector.London)]
    [InlineData("2026-01-15T13:00:00Z", SessionDetector.LondonNewYork)]
    [InlineData("2026-01-15T15:00:00Z", SessionDetector.LondonNewYork)]
    [InlineData("2026-01-15T16:59:59Z", SessionDetector.LondonNewYork)]
    [InlineData("2026-01-15T17:00:00Z", SessionDetector.NewYork)]
    [InlineData("2026-01-15T20:00:00Z", SessionDetector.NewYork)]
    [InlineData("2026-01-15T21:59:59Z", SessionDetector.NewYork)]
    [InlineData("2026-01-15T22:00:00Z", SessionDetector.Pacific)]
    [InlineData("2026-01-15T23:30:00Z", SessionDetector.Pacific)]
    public void Detect_ReturnsCorrectSession(string utcStr, string expected)
    {
        var dt = DateTime.Parse(utcStr, null, System.Globalization.DateTimeStyles.RoundtripKind);
        SessionDetector.Detect(dt).Should().Be(expected);
    }
}
