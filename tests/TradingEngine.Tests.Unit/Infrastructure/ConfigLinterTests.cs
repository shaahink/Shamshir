using System.Text.Json;
using TradingEngine.Infrastructure.Configuration;

namespace TradingEngine.Tests.Unit.Infrastructure;

/// <summary>
/// iter-quant-model P2.6 (D9, units doctrine): the config linter is what makes gold/crypto "safe by
/// construction" — it fails loudly the moment a strategy/profile JSON sets a raw-pip field without its
/// normalized companion, instead of quietly shipping a config that will crush a XAUUSD/BTCUSD stop.
/// </summary>
public sealed class ConfigLinterTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void LintStrategyJson_RawPipWithoutCompanion_ReportsViolation()
    {
        var root = Parse("""{"id":"x","orderEntry":{"maxSlippagePips":2.0}}""");

        var violations = ConfigLinter.LintStrategyJson(root, "x");

        violations.Should().ContainSingle(v => v.Contains("orderEntry.maxSlippagePips"));
    }

    [Fact]
    public void LintStrategyJson_RawPipWithCompanionPresent_NoViolation()
    {
        var root = Parse("""
            {"id":"x","orderEntry":{"maxSlippagePips":2.0,"maxSlippageSpreadMultiple":2.0}}
            """);

        ConfigLinter.LintStrategyJson(root, "x").Should().BeEmpty();
    }

    [Fact]
    public void LintStrategyJson_NoOrderEntrySection_NoViolation()
    {
        var root = Parse("""{"id":"x"}""");

        ConfigLinter.LintStrategyJson(root, "x").Should().BeEmpty();
    }

    [Fact]
    public void LintStrategyJson_AllFiveRawPipFields_EachReportedIndependently()
    {
        var root = Parse("""
            {
              "id": "x",
              "orderEntry": { "limitOffsetPips": 5.0, "maxSlippagePips": 2.0 },
              "positionManagement": {
                "stopLoss": { "maxPips": 100 },
                "breakeven": { "offsetPips": 1.0 },
                "trailing": { "stepPips": 10 }
              }
            }
            """);

        var violations = ConfigLinter.LintStrategyJson(root, "x");

        violations.Should().HaveCount(5);
        violations.Should().Contain(v => v.Contains("orderEntry.limitOffsetPips"));
        violations.Should().Contain(v => v.Contains("orderEntry.maxSlippagePips"));
        violations.Should().Contain(v => v.Contains("positionManagement.stopLoss.maxPips"));
        violations.Should().Contain(v => v.Contains("positionManagement.breakeven.offsetPips"));
        violations.Should().Contain(v => v.Contains("positionManagement.trailing.stepPips"));
    }

    [Fact]
    public void LintRiskProfileJson_MaxSlPipsWithoutCompanion_ReportsViolation()
    {
        var root = Parse("""{"id":"standard","maxSlPips":100.0}""");

        ConfigLinter.LintRiskProfileJson(root, "standard")
            .Should().ContainSingle(v => v.Contains("maxSlPips"));
    }

    [Fact]
    public void LintRiskProfileJson_MaxSlPipsWithCompanion_NoViolation()
    {
        var root = Parse("""{"id":"standard","maxSlPips":100.0,"maxSlAtrMultiple":5.0}""");

        ConfigLinter.LintRiskProfileJson(root, "standard").Should().BeEmpty();
    }
}
