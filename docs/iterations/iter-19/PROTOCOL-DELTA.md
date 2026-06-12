# Iteration 19 — Protocol Delta (T3: Partial Take-Profit)

**Date**: 2026-06-12
**Branch**: `iter/19-trade-intelligence`
**Applies to**: NetMQ broker protocol between TradingEngine and cBot

---

## New Command: `close_partial`

### Direction: Engine → cBot (via `bar_done.commands[]`)

```json
{
  "type": "close_partial",
  "positionId": "<guid>",
  "lots": 0.01
}
```

| Field | Type | Description |
|-------|------|-------------|
| `type` | string | Always `"close_partial"` |
| `positionId` | string | Engine's GUID for the position |
| `lots` | double | Number of lots to close (partial amount) |

### Behavior

- cBot finds the cTrader position via `_positionMap` reverse-lookup (cTrader long id → engine Guid)
- Calls `ClosePosition(pos, volumeInUnits)` with partial volume
- Does NOT add to `_commandCloses` set (position stays open)
- Returns exec result in `bar_result.execs[]`

---

## New Exec Kind: `partial_close`

### Direction: cBot → Engine (via `bar_result.execs[]`)

```json
{
  "clientOrderId": "<guid>",
  "kind": "partial_close",
  "positionId": 12345,
  "state": "Filled",
  "fillPrice": 1.09200,
  "filledLots": 0.01
}
```

| Field | Type | Description |
|-------|------|-------------|
| `kind` | string | `"partial_close"` |
| `filledLots` | double | The lots closed in this partial close (not total position size) |

### Engine handling

- `PositionTracker.OnExecutionAsync` checks `FilledLots < position.Lots` → partial close
- Reduces position lots, emits `PositionPartiallyClosed` event
- Does NOT deregister or publish `TradeClosed`
- Multiple partial closes + one final full close per position are valid

---

## Dedup Safety

- `NetMQBrokerAdapter.TryWriteExec` dedup uses signature `$"{exec.OrderId}|{exec.NewState}|{exec.FillPrice}|{exec.FilledLots}"`
- Partial close has different `FilledLots` → different signature → NOT deduped
- PositionTracker dedup: `_processedExecutionIds` check skips only if `!openPositions.ContainsKey(orderId)` → partial close keeps position open → subsequent full close NOT deduped

---

## cBot Changes (net6.0 / C# 10)

- Added `ExecuteClosePartialPosition(JsonElement cmd)`
- Parses `positionId` (Guid string) and `lots` (double)
- Converts lots to cTrader volume-in-units: `(int)(lots * lotSize)`
- Calls `ClosePosition(pos, volumeInUnits)` — cTrader partial close API
- Returns exec with `kind = "partial_close"`, `filledLots = partial lots`
- `OnPositionClosed` handler is NOT triggered for partial close (cTrader only fires on full close)

---

## Backward Compatibility

- All existing protocol messages unchanged
- Engine that doesn't support partial close: `ClosePartialPositionAsync` defaults to `ClosePositionAsync` (IBrokerAdapter interface default)
- cBot that doesn't support `close_partial`: command is unknown → ignored silently (no crash)
