# Shamshir Trading Engine

Algorithmic trading engine with prop-firm-oriented risk management. .NET 10, C# 13.

**For agents:** Start with `AGENTS.md` — it lists the reading order.

## Quick Start

```powershell
# Build
dotnet build

# Run unit tests (~207 pass)
dotnet test tests/TradingEngine.Tests.Unit

# Run simulation tests (FTMO + replay, credential-free)
dotnet test tests/TradingEngine.Tests.Simulation

# Run web UI
dotnet run --project src/TradingEngine.Web
# → http://localhost:5000
```

## Docs at a glance

| Document | What |
|----------|------|
| `AGENTS.md` | Session startup — read first |
| `DECISIONS.md` | All decisions D1–D80 |
| `docs/reference/SYSTEM-REFERENCE.md` | System overview + detailed reference |
| `docs/reference/CODE-MAP.md` | Feature→file index + process walkthroughs |
| `docs/reference/BACKTEST-ARCHITECTURE.md` | How backtesting works (both venue paths) |
| `docs/reference/TEST-ARCHITECTURE.md` | Test tiers, harnesses, cTrader vs mock |

## Architecture

```
src/
  TradingEngine.Domain/          # Pure domain — zero infra deps
  TradingEngine.Application/     # Assembly marker only
  TradingEngine.Infrastructure/  # EF Core, Skender, adapters, persistence
  TradingEngine.Risk/            # Risk engine, position sizing, prop firm rules
  TradingEngine.Strategies/      # Strategy implementations
  TradingEngine.Services/        # PipCalc, SL/TP, trailing, EntryPlanner, TradeCost
  TradingEngine.Host/            # EngineWorker, DI wiring, Program.cs
  TradingEngine.Web/             # Razor Pages, API controllers, SSE/SignalR
  TradingEngine.Adapters.CTrader/ # C# 6 cBot (cTrader integration)
```

## Tech Stack

- **Runtime:** .NET 10, C# 13
- **Persistence:** EF Core + SQLite
- **Indicators:** Skender.Stock.Indicators
- **Logging:** Serilog
- **Testing:** xUnit + FluentAssertions + NSubstitute
- **Web:** ASP.NET Core Razor Pages + Chart.js
