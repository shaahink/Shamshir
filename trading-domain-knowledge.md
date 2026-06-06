# Trading Domain Knowledge — AI Agent Reference

> **Who this is for:** AI agents implementing or extending the trading engine. Read this before touching any code related to symbols, pricing, pip values, position sizing, SL/TP calculation, or prop firm rules. Getting any of this wrong produces silent financial errors — the engine will compile and run but calculate incorrect lot sizes, wrong risk amounts, or breach prop firm rules without triggering any exception.

---

## 1. Symbol Taxonomy

A trading symbol always represents a pair: something you are buying (base) against something you are selling (quote). Understanding the pair structure is required for correct pip value and lot size calculation.

### 1.1 Forex pairs

```
EURUSD  →  EUR is base, USD is quote
           "How many USD does 1 EUR cost?"
           Price = 1.0850 means 1 EUR = 1.0850 USD

USDJPY  →  USD is base, JPY is quote
           Price = 149.50 means 1 USD = 149.50 JPY

GBPJPY  →  GBP is base, JPY is quote  (cross pair — neither is USD)
```

**Majors** (always involve USD, highest liquidity):
`EURUSD`, `GBPUSD`, `USDJPY`, `USDCHF`, `AUDUSD`, `USDCAD`, `NZDUSD`

**Minors / crosses** (two major currencies, no USD):
`EURGBP`, `EURJPY`, `GBPJPY`, `AUDCAD`, `EURCHF`, and others

**Exotics** (one major + one emerging market):
`USDZAR`, `USDMXN`, `USDTRY`, `EURNOK` — wide spreads, low liquidity

### 1.2 Metals (quoted like forex)

```
XAUUSD  →  Gold (XAU) priced in USD — treated as forex pair
XAGUSD  →  Silver (XAG) priced in USD
```

Pip size for metals differs — see section 2.

### 1.3 Indices (CFDs)

```
US30    →  Dow Jones Industrial Average
NAS100  →  Nasdaq 100
SPX500  →  S&P 500
GER40   →  DAX
UK100   →  FTSE 100
```

Indices are priced in points, not pips. Pip/point size is typically 1.0 (whole number increments). Contract size varies by broker.

### 1.4 Crypto pairs

```
BTCUSD  →  Bitcoin priced in USD
ETHUSD  →  Ethereum priced in USD
BTCEUR  →  Bitcoin priced in EUR
```

Key differences from forex:
- Trade 24/7, 365 days — no weekly close, no session gaps (except broker maintenance)
- No fixed lot size standard — typically denominated in base currency units (e.g. 1 lot = 1 BTC)
- No swap/rollover in spot; perpetual contracts have funding rates instead
- Spreads are proportional and wider — model as percentage, not fixed pips
- Price can move 5–10% in a day — ATR-based SL is essential, fixed-pip SL is inappropriate

---

## 2. Pip Mechanics

A **pip** (percentage in point) is the minimum standardised price movement for a symbol. Getting the pip size wrong makes every downstream calculation wrong.

### 2.1 Pip size by symbol type

| Symbol type | Example | Pip size | Decimal places displayed |
|---|---|---|---|
| Most forex | EURUSD | 0.0001 | 4 (or 5 with fractional pip) |
| JPY pairs | USDJPY, GBPJPY | 0.01 | 2 (or 3 with fractional pip) |
| Gold (XAUUSD) | XAUUSD | 0.01 | 2 |
| Silver (XAGUSD) | XAGUSD | 0.001 | 3 |
| Indices | US30 | 1.0 | 0 |
| Most crypto | BTCUSD | 1.0 | 0–2 (broker dependent) |

**Fractional pips:** Some brokers quote 5 decimal places for forex (e.g. 1.08501) or 3 for JPY pairs (149.501). The 5th decimal is 1/10th of a pip. The pip size does not change — it remains 0.0001 for EURUSD. Never use the tick size (smallest displayable movement) as the pip size.

### 2.2 Pip distance calculation

```csharp
// In TradingEngine.Domain.Helpers.PipCalculator
public static Pips Distance(Price from, Price to, SymbolInfo symbol)
{
    var rawDistance = Math.Abs(to.Value - from.Value);
    return new Pips((double)(rawDistance / symbol.PipSize));
}

// Example:
// EURUSD entry 1.08420, SL 1.08210
// Distance = |1.08420 - 1.08210| / 0.0001 = 21 pips

// USDJPY entry 149.50, SL 148.80
// Distance = |149.50 - 148.80| / 0.01 = 70 pips
```

### 2.3 SymbolInfo — required metadata per symbol

Every symbol the engine trades must have this metadata. Load from broker on connect or from a local config file:

```csharp
public record SymbolInfo(
    Symbol Symbol,
    SymbolCategory Category,      // Forex, Metal, Index, Crypto, Commodity
    string BaseCurrency,          // e.g. "EUR" for EURUSD
    string QuoteCurrency,         // e.g. "USD" for EURUSD
    decimal PipSize,              // 0.0001 for EURUSD, 0.01 for USDJPY
    decimal TickSize,             // smallest price increment (may be < PipSize)
    decimal ContractSize,         // units of base per 1 standard lot (100_000 for forex)
    decimal MinLots,              // broker minimum
    decimal MaxLots,              // broker maximum
    decimal LotStep,              // lot size increment (usually 0.01)
    decimal MarginRate,           // margin required as fraction of notional
    string AccountCurrency);      // account denomination, e.g. "USD"

public enum SymbolCategory { Forex, Metal, Index, Crypto, Commodity }
```

---

## 3. Pip Value Calculation

**Pip value** = the monetary value of a 1-pip move on 1 standard lot, expressed in the account currency. This is the critical number for lot size calculation. It changes with market price for some symbol types.

### 3.1 Three cases based on currency relationship

**Case 1 — Quote currency = Account currency (most common)**

```
EURUSD, account in USD:
PipValue = PipSize × ContractSize
         = 0.0001 × 100,000
         = $10.00 per lot (fixed, does not change with price)

GBPUSD, account in USD:
PipValue = 0.0001 × 100,000 = $10.00 per lot
```

**Case 2 — Base currency = Account currency**

```
USDCAD, account in USD:
PipValue = (PipSize × ContractSize) / CurrentAskPrice
         = (0.0001 × 100,000) / 1.3650
         = $7.33 per lot  ← changes as USDCAD rate changes

USDCHF, account in USD:
PipValue = (0.0001 × 100,000) / CurrentAskPrice
```

**Case 3 — Neither base nor quote = Account currency (cross pairs)**

```
EURGBP, account in USD:
PipValue = PipSize × ContractSize × ConversionRate(GBP→USD)
         = 0.0001 × 100,000 × GBPUSD_rate
         = 10 × 1.2650
         = $12.65 per lot  ← changes as GBPUSD rate changes

EURJPY, account in USD:
PipSize for JPY pair = 0.01
PipValue = 0.01 × 100,000 × ConversionRate(JPY→USD)
         = 1000 × (1 / USDJPY_rate)
         = 1000 × (1 / 149.50)
         = $6.69 per lot
```

### 3.2 Pip value implementation

```csharp
// In TradingEngine.Domain.Helpers.PipCalculator
public static decimal PipValuePerLot(
    SymbolInfo symbol,
    decimal currentPrice,
    Func<string, string, decimal> getCrossRate) // (fromCurrency, toCurrency) → rate
{
    var rawPipValue = symbol.PipSize * symbol.ContractSize;

    if (symbol.QuoteCurrency == symbol.AccountCurrency)
    {
        // Case 1 — no conversion needed
        return rawPipValue;
    }

    if (symbol.BaseCurrency == symbol.AccountCurrency)
    {
        // Case 2 — divide by current price
        if (currentPrice == 0) throw new InvalidOperationException("Price cannot be zero");
        return rawPipValue / currentPrice;
    }

    // Case 3 — cross rate conversion needed
    var conversionRate = getCrossRate(symbol.QuoteCurrency, symbol.AccountCurrency);
    return rawPipValue * conversionRate;
}
```

**Pitfall:** Pip value must be recalculated on each lot size calculation call, not cached across ticks. For Case 1 symbols (most forex with USD account) it is constant, but for others it drifts with price. Cache the symbol metadata — never the computed pip value.

---

## 4. Bid / Ask and Entry Price

**Bid** = price brokers buy at (you sell at) = price of short orders, exit of long orders
**Ask** = price brokers sell at (you buy at) = price of long orders, exit of short orders

```
Spread = Ask - Bid

Going Long:   Entry at Ask.  SL triggered when Bid reaches SL.  TP triggered when Bid reaches TP.
Going Short:  Entry at Bid.  SL triggered when Ask reaches SL.  TP triggered when Ask reaches TP.
```

This matters for backtest accuracy. When simulating a long trade on bar data (OHLC, not tick):
- Entry approximated at the Ask (Open + half spread)
- SL/TP checks use the Bid side of the bar
- A bar that touches the SL low (Bid) does not necessarily stop out a short entry on the same bar

In `SimulatedBrokerAdapter`, apply the spread model:
```csharp
decimal simulatedAsk = tick.Bid + symbol.TypicalSpread;  // for long entry fills
decimal simulatedBid = tick.Bid;                          // for short entry fills, SL checks
```

---

## 5. Stop Loss Calculation

Stop loss placement is a domain decision, not a system decision. The engine provides calculation helpers; strategies configure which method to use. All methods return a `Price`, never a pip distance — the distance is derived from entry and SL.

### 5.1 Fixed pip SL

```csharp
// Simplest method — fixed distance regardless of volatility
// Appropriate for: highly liquid, low-volatility instruments in known range
public static Price FixedPip(
    Price entry,
    TradeDirection direction,
    Pips distance,
    SymbolInfo symbol)
{
    var offset = (decimal)distance.Value * symbol.PipSize;
    return direction == TradeDirection.Long
        ? new Price(entry.Value - offset)   // SL below entry for long
        : new Price(entry.Value + offset);  // SL above entry for short
}
```

**When not to use:** Crypto (volatility too unpredictable), news events, low-liquidity sessions.

### 5.2 ATR-based SL (preferred for most strategies)

ATR (Average True Range) measures recent volatility. Placing SL at a multiple of ATR means the stop breathes with market conditions — tight in calm markets, wider in volatile ones.

```csharp
// Most robust general-purpose method
// multiplier typically 1.0–2.5 depending on strategy timeframe
public static Price AtrBased(
    Price entry,
    TradeDirection direction,
    double atrValue,      // ATR in price units (from IIndicatorService.Atr)
    double multiplier,    // e.g. 1.5
    SymbolInfo symbol)
{
    var offset = (decimal)(atrValue * multiplier);

    // Round to tick size — SL must be a valid broker price
    var rawSl = direction == TradeDirection.Long
        ? entry.Value - offset
        : entry.Value + offset;

    return new Price(RoundToTickSize(rawSl, symbol.TickSize));
}

private static decimal RoundToTickSize(decimal price, decimal tickSize)
    => Math.Round(price / tickSize) * tickSize;
```

**Important:** ATR is calculated on closed bars. Use the ATR of the chart timeframe the strategy is running on. A 1H ATR for a 1H strategy, a D1 ATR for a daily strategy. Do not mix timeframes here.

### 5.3 Swing high / swing low SL

Places SL just beyond the most recent swing high (for shorts) or swing low (for longs), plus a buffer. Most structurally sound method — SL invalidates the trade premise if hit.

```csharp
public static Price SwingBased(
    Price entry,
    TradeDirection direction,
    IReadOnlyList<Bar> recentBars,
    int lookbackBars,       // how many bars to look back for the swing
    Pips bufferPips,        // additional buffer beyond the swing point
    SymbolInfo symbol)
{
    var bufferOffset = (decimal)bufferPips.Value * symbol.PipSize;

    if (direction == TradeDirection.Long)
    {
        // SL below recent swing low
        var swingLow = recentBars
            .TakeLast(lookbackBars)
            .Min(b => b.Low);
        return new Price(RoundToTickSize(swingLow - bufferOffset, symbol.TickSize));
    }
    else
    {
        // SL above recent swing high
        var swingHigh = recentBars
            .TakeLast(lookbackBars)
            .Max(b => b.High);
        return new Price(RoundToTickSize(swingHigh + bufferOffset, symbol.TickSize));
    }
}
```

**Pitfall:** The SL produced by this method may be very wide in trending markets. Always validate that the resulting SL distance does not exceed `MaxSlPips` from the risk profile before accepting the trade intent.

### 5.4 SL validation

After calculating any SL, validate before accepting the trade:

```csharp
public static bool IsSlValid(
    Price entry,
    Price stopLoss,
    TradeDirection direction,
    SymbolInfo symbol,
    RiskProfile profile)
{
    // SL must be on the correct side of entry
    var correctSide = direction == TradeDirection.Long
        ? stopLoss.Value < entry.Value
        : stopLoss.Value > entry.Value;

    if (!correctSide) return false;

    // SL distance must not exceed max allowed pips
    var distance = Distance(entry, stopLoss, symbol);
    if (distance.Value > profile.MaxSlPips) return false;

    // SL distance must not be zero or negative
    if (distance.Value <= 0) return false;

    return true;
}
```

---

## 6. Take Profit Calculation

### 6.1 R:R multiple (most common)

Take profit at a fixed multiple of the risk (SL distance). A 2:1 R:R means TP is twice as far from entry as the SL.

```csharp
public static Price? RRMultiple(
    Price entry,
    Price stopLoss,
    TradeDirection direction,
    double rrRatio,   // e.g. 2.0 for 2:1
    SymbolInfo symbol)
{
    if (rrRatio <= 0) return null; // no TP

    var slDistance = Math.Abs(entry.Value - stopLoss.Value);
    var tpDistance = slDistance * (decimal)rrRatio;

    var rawTp = direction == TradeDirection.Long
        ? entry.Value + tpDistance
        : entry.Value - tpDistance;

    return new Price(RoundToTickSize(rawTp, symbol.TickSize));
}
```

### 6.2 ATR multiple TP

```csharp
public static Price? AtrMultiple(
    Price entry,
    TradeDirection direction,
    double atrValue,
    double multiplier,
    SymbolInfo symbol)
{
    var offset = (decimal)(atrValue * multiplier);
    var rawTp = direction == TradeDirection.Long
        ? entry.Value + offset
        : entry.Value - offset;
    return new Price(RoundToTickSize(rawTp, symbol.TickSize));
}
```

### 6.3 No TP (trail to exit)

Returning `null` from any TP method means no fixed TP is set. The position manager then uses a trailing stop method to exit. This is valid and should be treated as a first-class option, not a bug.

---

## 7. Position Sizing — Full Worked Examples

This is the most important calculation in the engine. An error here loses money.

### 7.1 Formula

```
RiskAmount   = Equity × RiskPercentage
SlPips       = |EntryPrice - StopLossPrice| / PipSize
PipValuePerLot = (see section 3)
RawLots      = RiskAmount / (SlPips × PipValuePerLot)
ScaledLots   = RawLots × DrawdownScaleFactor
FinalLots    = RoundDown(ScaledLots, LotStep) clamped to [MinLots, MaxLots]
```

### 7.2 Example 1 — EURUSD long, USD account

```
Equity          = $10,000
Risk            = 1% → RiskAmount = $100
Entry           = 1.08420 (Ask)
StopLoss        = 1.08210
SlDistance      = |1.08420 - 1.08210| = 0.00210
SlPips          = 0.00210 / 0.0001 = 21 pips
PipValuePerLot  = $10.00 (EURUSD, USD account, Case 1)
RawLots         = 100 / (21 × 10) = 100 / 210 = 0.4762 lots
ScaledLots      = 0.4762 × 1.0 (no DD scaling) = 0.4762
FinalLots       = RoundDown(0.4762 / 0.01) × 0.01 = 0.47 lots
```

### 7.3 Example 2 — USDJPY short, USD account

```
Equity          = $10,000
Risk            = 1% → RiskAmount = $100
Entry           = 149.50 (Bid, short)
StopLoss        = 150.20
SlDistance      = |149.50 - 150.20| = 0.70
SlPips          = 0.70 / 0.01 = 70 pips
PipValuePerLot  = (0.01 × 100,000) / 149.50 = $6.69 (Case 2, price-dependent)
RawLots         = 100 / (70 × 6.69) = 100 / 468.3 = 0.2135 lots
FinalLots       = 0.21 lots
```

### 7.4 Example 3 — GBPJPY long, USD account (cross pair)

```
Equity          = $10,000
Risk            = 1% → RiskAmount = $100
Entry           = 189.50 (Ask)
StopLoss        = 188.80
SlDistance      = 0.70
SlPips          = 0.70 / 0.01 = 70 pips
GBPUSD rate     = 1.2650 (needed for conversion, Case 3)
PipValuePerLot  = 0.01 × 100,000 × (1/149.50) × 1.2650
                  NO — for GBPJPY the quote currency is JPY
                  PipValuePerLot = 0.01 × 100,000 × (GBPUSD / USDJPY)
                  Actually: PipValue in USD = 0.01 × 100,000 × (1/USDJPY) × GBPUSD
                  = 1000 × (1/189.50) × 1.2650  ← use GBPJPY directly as proxy
                  Simpler: PipValuePerLot = 1000 JPY per lot / USDJPY_rate × USD
                  = 1000 / 149.50 = $6.69... wait

Correct approach for GBPJPY (USD account):
  Quote = JPY. Account = USD. Need JPY→USD rate = 1/USDJPY = 1/149.50
  PipValuePerLot = PipSize × ContractSize × (1/USDJPY)
                 = 0.01 × 100,000 × (1/149.50)
                 = 1000 / 149.50
                 = $6.69 per lot

RawLots = 100 / (70 × 6.69) = 0.21 lots
```

**Implementation note:** For cross pairs, always resolve the conversion as `getCrossRate(quoteCurrency, accountCurrency)`. For JPY quote with USD account, this is `getCrossRate("JPY","USD")` = 1/USDJPY. Never hardcode currency relationships — always look up via the rate provider.

---

## 8. Trailing Stop Methods

### 8.1 Step trail

Moves SL by a fixed pip increment only when price has moved that many pips further in profit. SL never moves backward.

```csharp
// Only call this when price has moved in trade's favour
public static Price? StepTrail(
    Position position,
    decimal currentBid, // use Bid for longs (SL checks happen at Bid)
    decimal currentAsk, // use Ask for shorts
    Pips stepPips,
    SymbolInfo symbol)
{
    var step = (decimal)stepPips.Value * symbol.PipSize;

    if (position.Direction == TradeDirection.Long)
    {
        var newSl = currentBid - step;
        // Only move SL up, never down
        return newSl > position.CurrentStopLoss.Value
            ? new Price(RoundToTickSize(newSl, symbol.TickSize))
            : null;
    }
    else
    {
        var newSl = currentAsk + step;
        // Only move SL down, never up
        return newSl < position.CurrentStopLoss.Value
            ? new Price(RoundToTickSize(newSl, symbol.TickSize))
            : null;
    }
}
```

### 8.2 ATR trail

SL trails at a fixed ATR multiple behind the most favourable price reached. Adapts to volatility changes during the trade.

```csharp
public static Price? AtrTrail(
    Position position,
    decimal highestBidSinceEntry, // track in PositionManager state
    decimal lowestAskSinceEntry,
    double currentAtr,
    double multiplier,
    SymbolInfo symbol)
{
    var offset = (decimal)(currentAtr * multiplier);

    if (position.Direction == TradeDirection.Long)
    {
        var newSl = highestBidSinceEntry - offset;
        return newSl > position.CurrentStopLoss.Value
            ? new Price(RoundToTickSize(newSl, symbol.TickSize))
            : null;
    }
    else
    {
        var newSl = lowestAskSinceEntry + offset;
        return newSl < position.CurrentStopLoss.Value
            ? new Price(RoundToTickSize(newSl, symbol.TickSize))
            : null;
    }
}
```

### 8.3 Breakeven

Move SL to entry (+ small buffer) when trade reaches a target R multiple. Protects open profit. Applied once, then optionally continues with another trailing method.

```csharp
public static Price? Breakeven(
    Position position,
    decimal currentBid,
    decimal currentAsk,
    double triggerRMultiple, // e.g. 1.0 = move to BE when +1R in profit
    Pips bufferPips,         // buffer above entry to cover spread/commission
    SymbolInfo symbol)
{
    var slDistance = Math.Abs(position.EntryPrice.Value - position.CurrentStopLoss.Value);
    var triggerDistance = slDistance * (decimal)triggerRMultiple;
    var buffer = (decimal)bufferPips.Value * symbol.PipSize;

    if (position.Direction == TradeDirection.Long)
    {
        var inProfit = currentBid - position.EntryPrice.Value;
        if (inProfit < triggerDistance) return null;            // not at trigger yet

        var beSl = position.EntryPrice.Value + buffer;
        return beSl > position.CurrentStopLoss.Value           // only move forward
            ? new Price(RoundToTickSize(beSl, symbol.TickSize))
            : null;
    }
    else
    {
        var inProfit = position.EntryPrice.Value - currentAsk;
        if (inProfit < triggerDistance) return null;

        var beSl = position.EntryPrice.Value - buffer;
        return beSl < position.CurrentStopLoss.Value
            ? new Price(RoundToTickSize(beSl, symbol.TickSize))
            : null;
    }
}
```

---

## 9. P&L Calculation

### 9.1 Closed trade P&L

```csharp
// Gross P&L in account currency
public static Money GrossPnL(
    TradeDirection direction,
    Price entryPrice,
    Price exitPrice,
    decimal lots,
    SymbolInfo symbol,
    Func<string, string, decimal> getCrossRate)
{
    var priceDiff = direction == TradeDirection.Long
        ? exitPrice.Value - entryPrice.Value
        : entryPrice.Value - exitPrice.Value;

    var pipsMoved = priceDiff / symbol.PipSize;
    var pipValue = PipValuePerLot(symbol, exitPrice.Value, getCrossRate);
    var grossAmount = pipsMoved * pipValue * lots;

    return new Money(grossAmount, symbol.AccountCurrency);
}
```

### 9.2 Floating P&L (open position)

```csharp
public static decimal FloatingPnL(
    Position position,
    Tick currentTick,
    SymbolInfo symbol,
    Func<string, string, decimal> getCrossRate)
{
    // Long positions close at Bid; short positions close at Ask
    var closingPrice = position.Direction == TradeDirection.Long
        ? currentTick.Bid
        : currentTick.Ask;

    var priceDiff = position.Direction == TradeDirection.Long
        ? closingPrice - position.EntryPrice.Value
        : position.EntryPrice.Value - closingPrice;

    var pipValue = PipValuePerLot(symbol, closingPrice, getCrossRate);
    return (priceDiff / symbol.PipSize) * pipValue * position.Lots;
}
```

### 9.3 R-multiple

```csharp
// R = actual PnL / initial risk amount
// +1R = made exactly as much as was risked
// -1R = lost full risk amount (SL hit exactly at planned SL)
public static double RMultiple(Money netPnL, Money initialRiskAmount)
{
    if (initialRiskAmount.Amount == 0) return 0;
    return (double)(netPnL.Amount / initialRiskAmount.Amount);
}
```

---

## 10. MAE and MFE Tracking

MAE (Maximum Adverse Excursion) and MFE (Maximum Favorable Excursion) are diagnostic metrics recorded per trade. They tell you how far price went against you (MAE) and in your favour (MFE) during the life of the trade.

- **MAE close to SL distance** → entries are near the correct level (not much wiggle before moving in your direction)
- **MFE much larger than PnL** → exits are too early (leaving money on the table)
- **MAE > MFE** → the trade premise was wrong; price moved against before reversing

```csharp
// Track in PositionManager state per open position
public sealed class ExcursionTracker
{
    private decimal _worstAdverse = 0m;   // in pips
    private decimal _bestFavorable = 0m;  // in pips

    public void Update(Position position, Tick tick, SymbolInfo symbol)
    {
        // Adverse = price moving against the trade
        // Favorable = price moving in the trade's direction
        decimal adverse, favorable;

        if (position.Direction == TradeDirection.Long)
        {
            adverse   = Math.Max(0, position.EntryPrice.Value - tick.Bid) / symbol.PipSize;
            favorable = Math.Max(0, tick.Bid - position.EntryPrice.Value) / symbol.PipSize;
        }
        else
        {
            adverse   = Math.Max(0, tick.Ask - position.EntryPrice.Value) / symbol.PipSize;
            favorable = Math.Max(0, position.EntryPrice.Value - tick.Ask) / symbol.PipSize;
        }

        _worstAdverse   = Math.Max(_worstAdverse, adverse);
        _bestFavorable  = Math.Max(_bestFavorable, favorable);
    }

    public Pips Mae => new(_worstAdverse);
    public Pips Mfe => new(_bestFavorable);
}
```

---

## 11. FTMO Rule Mechanics — Precise Definitions

This section defines exactly how FTMO rules must be implemented. Ambiguity here = account failure.

### 11.1 Equity definition

```
Equity = Balance + Floating PnL - Open Commissions - Accrued Swaps
```

All four components. Never omit commissions or swaps. On cTrader, all four are available via the account stream.

### 11.2 Daily drawdown

**Trigger condition:** Equity at any point during the trading day falls to or below:
```
DailyLossLimit = InitialAccountBalance × (1 - MaxDailyLossPercent)
```

For FTMO standard ($100k account, 5% daily limit):
```
DailyLossLimit = 100,000 × (1 - 0.05) = $95,000

If equity touches $95,000 at any tick → breach
```

**CRITICAL:** FTMO uses the **initial account balance** (the balance when the challenge started), not the current balance and not today's starting balance. This is a common implementation mistake.

Store `InitialAccountBalance` once at account activation. Never update it.

**Daily reset:** At the configured reset time (midnight Prague time for FTMO), record a new `DailyStartEquity` snapshot. The daily loss is measured from `DailyStartEquity`, not from `InitialAccountBalance`. The absolute floor `DailyLossLimit` computed above still applies regardless of daily reset.

### 11.3 Maximum drawdown

**Trigger condition:** Equity at any point falls to or below:
```
MaxDrawdownFloor = InitialAccountBalance × (1 - MaxTotalLossPercent)
               = 100,000 × 0.90 = $90,000

If equity touches $90,000 at any tick → breach
```

This is a trailing drawdown on some FTMO account types. On trailing DD accounts:
```
MaxDrawdownFloor = PeakEquity × (1 - MaxTotalLossPercent)
```

Where `PeakEquity` is the highest equity ever reached since account opening. On standard accounts it is fixed at initial balance. Confirm account type at activation and load the correct rule set.

### 11.4 Profit target

```
ProfitTarget = InitialAccountBalance × (1 + ProfitTargetPercent)
             = 100,000 × 1.10 = $110,000

Target reached when Balance (not equity) ≥ $110,000
```

Note: profit target is checked against **balance** (realised), not floating equity. An account with $111k equity but $99k balance has not met the target.

### 11.5 Weekend and news rules (configurable, off by default)

**News filter:** If enabled, block all new trade entries within N minutes before and after a high-impact news event for the currencies in the pair being traded. News events must be sourced from an external calendar feed (e.g. ForexFactory CSV). This is a **data input**, not a calculation.

**Weekend holding:** If `AllowWeekendHolding = false`, close all positions before the weekly market close (Friday 22:00 UTC for forex). Do not open new positions after Friday 21:00 UTC. Implement as a session filter check in the risk manager.

### 11.6 FTMO rule set JSON schema

```json
{
  "id": "ftmo-standard",
  "displayName": "FTMO Standard Challenge",
  "drawdownType": "Fixed",              // "Fixed" or "Trailing"
  "maxDailyLossPercent": 0.05,
  "maxTotalLossPercent": 0.10,
  "profitTargetPercent": 0.10,
  "minTradingDays": 4,
  "equityDefinition": "BalancePlusFloatingMinusFeesAndSwaps",
  "dailyResetTimeUtc": "22:00:00",      // 22:00 UTC = midnight Prague (CET)
  "dailyResetTimezone": "Europe/Prague",
  "allowTradesDuringNews": false,
  "newsImpactFilter": "High",           // "High", "Medium", "All"
  "newsWindowMinutesBefore": 30,
  "newsWindowMinutesAfter": 15,
  "allowWeekendHolding": false,
  "weekendCloseUtc": "21:00:00",        // Friday
  "weekendNoOpenUtc": "20:00:00",       // Friday — no new trades after this
  "protectionResetPolicy": "NextTradingDay",
  "forceCloseOnBreach": false           // close all on breach, or just block new
}
```

---

## 12. Trading Sessions

Sessions affect liquidity, spread, and strategy filter logic. Store as UTC ranges.

| Session | Open (UTC) | Close (UTC) | Notes |
|---|---|---|---|
| Sydney | 21:00 | 06:00 | Sun-Fri. Low liquidity, AUD/NZD pairs |
| Tokyo | 23:00 | 08:00 | JPY pairs active |
| London | 07:00 | 16:00 | Highest volume. EUR/GBP pairs |
| New York | 12:00 | 21:00 | USD pairs. London/NY overlap is peak |
| London/NY overlap | 12:00 | 16:00 | Highest liquidity of the day |

**DST:** London and New York observe DST (clocks shift in March and October). Session times in local clock terms are fixed, but UTC equivalent shifts by 1 hour. Implement sessions using `TimeZoneInfo` and convert to UTC — never hardcode UTC session boundaries.

```csharp
public static bool IsInSession(DateTime utcNow, TradingSession session)
{
    var localTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow, session.TimeZone);
    var timeOfDay = localTime.TimeOfDay;
    return timeOfDay >= session.OpenLocal && timeOfDay < session.CloseLocal;
}
```

---

## 13. Common Calculation Mistakes

These are real errors that produce incorrect results without throwing exceptions.

**1. Using `double` for pip arithmetic**
`1.08420 - 1.08210` in `double` = `0.00209999999...` not `0.00210000`. Always use `decimal` for price arithmetic. Convert to `double` only at the boundary where Skender needs it.

**2. Using the wrong price side for entry**
Long entries fill at Ask. If you use Mid or Bid for a long entry in backtest, every simulated entry is more favourable than reality. The spread cost disappears. This makes backtest results systematically better than live.

**3. Not rounding lots DOWN**
`Math.Round(0.476)` = `0.48`. `Math.Floor(0.476 / 0.01) * 0.01` = `0.47`. The difference is 0.01 lots. On a 21-pip SL that is an extra $2.10 of risk beyond the 1% target. At scale across hundreds of trades this compounds.

**4. Caching pip value across ticks for variable symbols**
For USDJPY, USDCAD, and all cross pairs, pip value changes as price moves. If you cache pip value at session start and use it all day, lot size calculations drift from the target risk level.

**5. Assuming all forex symbols have 4 decimal places**
USDJPY is 2 decimal places (pip = 0.01). XAUUSD is 2 decimal places (pip = 0.01 for gold). If you hardcode `0.0001` as pip size, gold and JPY lot sizes will be 100× wrong.

**6. Checking profit target against equity instead of balance**
Floating unrealised profit does not count toward FTMO's profit target. Only closed, realised balance. An account at $110k equity with an open long that is $10k in profit has NOT passed — if that trade closes for less, the target is missed.

**7. Using system timezone for session checks**
The engine may run on a server in UTC, UTC+2, or UTC+8. Never use `DateTime.Now` or local timezone logic for session windows. All times are UTC internally; convert using `TimeZoneInfo` with the rule set's specified timezone.

**8. ATR values on insufficient bars**
Skender returns `null` for ATR when there aren't enough bars for the period. If the engine starts mid-session with only 5 bars and ATR period is 14, the first 13 evaluations will have no ATR. Strategies must check for this and return `null` intent (no signal) until `RequiredBarCount` is satisfied. This is what the `RequiredBarCount` property on `IStrategy` is for — the engine will not call `Evaluate` until that many bars are available.

**9. SL on the wrong side of entry**
A long trade at 1.0842 with an SL at 1.0860 is invalid — the SL is above entry, which means the trade would immediately stop out. Always validate SL direction before submitting a `TradeIntent`. The `IsSlValid` helper in section 5.4 catches this.

**10. Forgetting swap and commission in equity calculation**
On cTrader, swap accrues daily on open positions. Commission is charged at entry and exit. Both reduce equity in real-time. If you omit them from your equity snapshot, your DD calculation understates actual drawdown. For intraday strategies this may be minor, but for swing trades (multi-day holds) swap accumulates meaningfully.

---

## 14. SymbolInfo — Default Values Reference

When loading symbol metadata from the broker is not possible (e.g. backtest with CSV data), use these defaults. Always prefer broker-provided values.

| Symbol | PipSize | ContractSize | TypicalSpread | Category |
|---|---|---|---|---|
| EURUSD | 0.0001 | 100,000 | 0.0001 (1 pip) | Forex |
| GBPUSD | 0.0001 | 100,000 | 0.00012 | Forex |
| USDJPY | 0.01 | 100,000 | 0.010 | Forex |
| USDCHF | 0.0001 | 100,000 | 0.00011 | Forex |
| AUDUSD | 0.0001 | 100,000 | 0.00011 | Forex |
| USDCAD | 0.0001 | 100,000 | 0.00013 | Forex |
| NZDUSD | 0.0001 | 100,000 | 0.00014 | Forex |
| EURGBP | 0.0001 | 100,000 | 0.00013 | Forex |
| EURJPY | 0.01 | 100,000 | 0.012 | Forex |
| GBPJPY | 0.01 | 100,000 | 0.018 | Forex |
| XAUUSD | 0.01 | 100 | 0.30 | Metal |
| XAGUSD | 0.001 | 5,000 | 0.030 | Metal |
| BTCUSD | 1.0 | 1 | 50.0 | Crypto |
| ETHUSD | 0.01 | 1 | 2.0 | Crypto |
| US30 | 1.0 | 1 | 3.0 | Index |
| NAS100 | 0.25 | 1 | 1.0 | Index |

---

## 15. Indicator Reference — Skender Wrapper Guide

The `IIndicatorService` wraps Skender. Below are the Skender method names and the wrapper signatures to implement.

```csharp
// Skender usage (inside IIndicatorService implementation only)
var quotes = bars.Select(b => new SkenderQuote(b)).ToList();

// ATR — returns price units (not pips), period typically 14
var atr = quotes.GetAtr(period).LastOrDefault()?.Atr ?? 0;

// EMA — returns price
var ema = quotes.GetEma(period).LastOrDefault()?.Ema ?? 0;

// SMA — returns price
var sma = quotes.GetSma(period).LastOrDefault()?.Sma ?? 0;

// Bollinger Bands
var bb = quotes.GetBollingerBands(period, stdDevMultiplier).LastOrDefault();
var (upper, middle, lower) = (bb?.UpperBand ?? 0, bb?.Sma ?? 0, bb?.LowerBand ?? 0);

// RSI — returns 0–100
var rsi = quotes.GetRsi(period).LastOrDefault()?.Rsi ?? 50;

// MACD
var macd = quotes.GetMacd(fastPeriod, slowPeriod, signalPeriod).LastOrDefault();
var (macdLine, signal, histogram) = (macd?.Macd ?? 0, macd?.Signal ?? 0, macd?.Histogram ?? 0);
```

**All Skender results are nullable.** Always use `?.Property ?? defaultValue`. Never assume the last result is non-null — it will be null if there are insufficient bars.

**Caching strategy:** After each new closed bar, compute all indicators needed by all active strategies and store results in `MarketContext.IndicatorValues` (a `Dictionary<string, double>`). Use a key convention:

```
"EMA_20"          → EMA with period 20
"ATR_14"          → ATR with period 14
"BB_Upper_20_2"   → Bollinger upper band, period 20, 2 stddev
"RSI_14"          → RSI period 14
```

Strategies look up by key, never call Skender directly.

---

*This document is a companion to `trading-engine-design-v1.md`. Both must be provided to the AI agent at the start of each coding session.*
