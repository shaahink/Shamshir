namespace TradingEngine.Tests.Unit.Phase31Tests;

[Trait("Category", "Unit")]
public sealed class JournalNormalizerTests
{
    [Theory]
    [InlineData("SL")]
    [InlineData("TP")]
    [InlineData("FORCE")]
    [InlineData("DailyDD")]
    [InlineData("MaxDD")]
    public void OrderFilled_with_close_reason_normalizes_to_CLOSE(string reason)
    {
        // A close fill shares the "OrderFilled" event name with an entry fill, but must read as CLOSE
        // so the journal's CLOSE filter actually surfaces exits (previously every close hid under FILL).
        JournalNormalizer.NormalizeKind("OrderFilled", reason)
            .Should().Be(nameof(JournalEventKind.CLOSE));
    }

    [Theory]
    [InlineData("Filled")]
    [InlineData("PartialFill")]
    public void OrderFilled_entry_normalizes_to_FILL(string reason)
    {
        JournalNormalizer.NormalizeKind("OrderFilled", reason)
            .Should().Be(nameof(JournalEventKind.FILL));
    }

    [Fact]
    public void OrderCancelled_normalizes_to_ENTRY_EXPIRED()
    {
        JournalNormalizer.NormalizeKind("OrderCancelled", "ENTRY_EXPIRED")
            .Should().Be(nameof(JournalEventKind.ENTRY_EXPIRED));
    }

    [Fact]
    public void OrderSubmitted_and_rejected_keep_their_kinds()
    {
        JournalNormalizer.NormalizeKind("OrderSubmitted", null).Should().Be(nameof(JournalEventKind.ORDER));
        JournalNormalizer.NormalizeKind("OrderRejected", "MAX_EXPOSURE").Should().Be(nameof(JournalEventKind.REJECTED));
        JournalNormalizer.NormalizeKind("SIGNAL", null).Should().Be(nameof(JournalEventKind.SIGNAL));
    }
}
