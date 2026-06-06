# Trading Engine Specification (Prop-Firm-Oriented, Strategy-Agnostic)

## 1. Purpose

The system is a standalone trading engine designed to:

- Execute algorithmic trading strategies consistently across live and backtest environments
- Enforce strict risk and drawdown rules aligned with prop firm requirements
- Enable rapid experimentation with strategies without changing core infrastructure
- Provide reliable reporting, auditability, and replay capabilities

The system is designed to be **broker-agnostic**, with initial integration via cTrader, and future compatibility with other platforms.

---

## 2. Core Principles

### 2.1 Strategy-Agnostic Core

- Strategies are treated as interchangeable components
- The system does not rely on complex or “clever” strategies
- Focus is on robustness, consistency, and risk control rather than prediction

### 2.2 Deterministic Behavior

- The same inputs (market data + events) must produce the same outputs
- Backtesting and live trading must use the same logic and rules

### 2.3 Separation of Concerns

- Trading decisions, risk enforcement, and reporting are logically separated
- Time-critical operations are isolated from background processes

### 2.4 Constraint-First Design

- Risk and prop firm rules are enforced independently of strategies
- No trade is executed unless it passes all constraints

---

## 3. System Scope

The system is responsible for:

- Receiving market data (ticks, bars)
- Running strategies to generate trade intents
- Enforcing constraints (drawdown, exposure, session rules)
- Managing positions and trade lifecycle
- Recording all events for reporting and replay
- Supporting both live trading and backtesting

The system is NOT responsible for:

- Manual trading interfaces
- Broker-specific UI concerns
- Strategy discovery (beyond basic built-in strategies for validation)

---

## 4. Risk & Drawdown Management

### 4.1 Objectives

The system must:

- Prevent violations of prop firm rules
- Maintain capital preservation under all conditions
- Adapt risk dynamically based on performance

---

### 4.2 Supported Constraints

#### Daily Drawdown Limit

- Maximum allowable loss per trading day
- Based on equity (not just balance)
- Resets at a defined time (configurable, e.g. broker time)

#### Maximum Overall Drawdown

- Fixed or trailing drawdown from peak equity
- Must be continuously tracked

#### Position Risk Limits

- Maximum risk per trade (percentage of equity)
- Maximum total exposure across positions

#### Trading Suspension

- Trading must be halted when:
  - Daily drawdown limit is reached
  - Maximum drawdown is breached
  - System enters protection mode

---

### 4.3 Behavior on Breach

- New trades are blocked immediately
- Existing trades may:
  - continue (default), or
  - be force-closed (configurable)

- System enters a “protected” state until reset conditions are met

---

### 4.4 Dynamic Risk Adjustment

The system should support:

- Reduced position sizing after losses
- Risk scaling based on drawdown level
- Conservative mode near constraint limits

---

## 5. Market Data & State Handling

### 5.1 Data Types

- Tick data (bid/ask updates)
- Bar data (multiple timeframes)

---

### 5.2 State Requirements

The system must maintain:

- Current positions and orders
- Real-time equity (including floating PnL)
- Strategy state (indicators, signals)
- Constraint state (drawdown tracking)

---

### 5.3 Restart & Recovery

On restart, the system must:

- Reconstruct current trading state
- Reload or request recent market data
- Resume operation without violating constraints

---

## 6. Strategy Support (Initial Scope)

The system will include a small set of simple strategies for validation purposes.

### 6.1 Purpose of Built-in Strategies

- Validate system correctness
- Test risk and constraint handling
- Provide baseline performance for experimentation

---

### 6.2 Example Strategies

#### Trend Breakout

- Entry on break of recent high/low
- Direction filtered by moving average
- Exit via trailing stop

#### Moving Average Trend

- Entry based on fast/slow MA crossover
- Exit via reversal or trailing stop

#### Volatility Expansion

- Entry when volatility increases beyond threshold
- Exit via fixed or trailing stop

---

### 6.3 Strategy Expectations

- Strategies produce trade intents, not executions
- Strategies do not manage risk or constraints
- Strategies must operate consistently in live and backtest modes

---

## 7. Reporting & Analytics

### 7.1 Objectives

Provide clear visibility into:

- Performance
- Risk exposure
- Rule compliance
- Strategy behavior

---

### 7.2 Data to Capture

- All trades (entry, exit, size, PnL)
- Equity curve over time
- Drawdown metrics
- Strategy signals and decisions
- Constraint violations and blocks

---

### 7.3 Outputs

- Structured logs (for debugging and replay)
- Trade history export (CSV or similar)
- Performance summaries:
  - win rate
  - profit factor
  - max drawdown
  - average trade

---

### 7.4 Replay Capability

The system should support:

- Replaying historical data through the engine
- Reproducing decisions and outcomes
- Validating behavior under different scenarios

---

## 8. Backtesting Support

### 8.1 Requirements

- Use the same engine logic as live trading
- Feed historical market data as input
- Simulate execution events

---

### 8.2 Goals

- Validate strategies
- Validate risk and constraint behavior
- Compare configurations (risk, filters, etc.)

---

## 9. Technical Considerations

### 9.1 Performance

- Real-time decision-making must not be blocked by storage or reporting
- System must handle high-frequency tick data without degradation

---

### 9.2 Reliability

- System must recover cleanly from restarts
- State consistency must be maintained across sessions

---

### 9.3 Extensibility

- New strategies can be added without modifying core logic
- New brokers/adapters can be integrated with minimal changes

---

### 9.4 Observability

- System must expose sufficient logs and metrics for debugging
- Decisions (why a trade was taken or blocked) must be traceable

---

## 10. Success Criteria

The system is considered successful if:

- It enforces prop firm constraints reliably
- It behaves identically in backtest and live environments
- It allows rapid experimentation with strategies and risk models
- It provides clear, reproducible reporting and insights
- It remains stable under continuous operation

---


---
