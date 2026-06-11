# Iteration 17 — Lock-Step Protocol (ADR)

**Version**: 1
**Transport**: NetMQ DEALER (cBot) ↔ ROUTER (engine), all correctness traffic
**Encoding**: JSON, camelCase
**Threading**: All sends on poller thread via NetMQQueue

---

## Message Schemas

### cBot → engine

#### hello (handshake, replaces sleeps/heartbeats)

```json
{ "type": "hello", "v": 1, "symbols": ["EURUSD"], "periods": ["H1"], "barsLoaded": 419 }
```

Fields:
- `v` (int): protocol version, always 1
- `symbols` (string[]): symbols the cBot will publish
- `periods` (string[]): timeframes (H1, M15, etc.)
- `barsLoaded` (int): total bars loaded across all subscriptions

#### bar (lock-step barrier — sent via DEALER, NOT PUB)

```json
{
  "type": "bar", "v": 1, "seq": 42, "symbol": "EURUSD", "period": "H1",
  "openTime": "2024-01-15T12:00:00Z", "open": 1.09120, "high": 1.09250, "low": 1.09080,
  "close": 1.09196, "volume": 1234.0,
  "simTime": "2024-01-15T13:00:00Z",
  "account": { "balance": 100000.0, "equity": 100000.0 }
}
```

Receiving side (engine) responds with `bar_done` (see below).
cBot MUST block until `bar_done` for this seq arrives. Timeout: 30 s → `Print` + `Stop()`.

#### bar_result (response to bar_done commands — always sent, even with empty execs)

```json
{
  "type": "bar_result", "v": 1, "seq": 42,
  "execs": [
    {
      "clientOrderId": "guid", "kind": "entry_fill", "positionId": 12345,
      "state": "Filled", "fillPrice": 1.09200, "filledLots": 0.05,
      "reason": null, "simTime": "2024-01-15T13:00:00Z",
      "grossProfit": 0.0, "netProfit": 0.0
    }
  ],
  "account": { "balance": 100000.0, "equity": 99950.0 }
}
```

#### exec (async — between-bar fills: SL/TP hit, margin call)

```json
{
  "type": "exec", "v": 1, "clientOrderId": "guid", "kind": "close",
  "positionId": 12345, "state": "Filled", "fillPrice": 1.09350, "filledLots": 0.05,
  "reason": "TP", "simTime": "2024-01-15T14:30:00Z",
  "grossProfit": 7.50, "netProfit": 7.20
}
```

#### stats (sent from OnStop before disposal)

```json
{ "type": "stats", "v": 1, "barsSent": 419, "cmdsReceived": 12, "ordersExecuted": 12, "execsSent": 24 }
```

#### acct (account snapshot — via PUB for telemetry)

```json
{ "type": "acct", "balance": 100000.0, "equity": 100052.0, "floatingPnL": 52.0, "time": "2024-01-15T14:30:00Z" }
```

### engine → cBot

#### hello_ack

```json
{ "type": "hello_ack", "v": 1 }
```

#### bar_done

```json
{
  "type": "bar_done", "v": 1, "seq": 42,
  "commands": [
    { "type": "submit_order", "clientOrderId": "guid", "symbol": "EURUSD", "direction": "Long",
      "lots": 0.05, "slPrice": 1.09062, "tpPrice": 1.09335 },
    { "type": "close_position", "positionId": "12345" }
  ]
}
```

#### shutdown

```json
{ "type": "shutdown", "v": 1 }
```

---

## Sequencing Rules

1. **Handshake**: cBot sends `hello` → engine sends `hello_ack`. No bars flow until handshake complete.
2. **Bar barrier**: cBot sends `bar`(seq=N) → engine processes, buffers commands → sends `bar_done`(seq=N) → cBot executes commands, sends `bar_result`(seq=N). Then and only then does cBot advance to bar N+1.
3. **Async execs**: SL/TP fills that occur between bars are sent as `exec` messages. They are serialized on the FIFO DEALER→ROUTER socket and arrive before the next `bar`. Engine processes them in order.
4. **Shutdown**: engine sends `shutdown` → cBot drains remaining actions, sends `stats`, disposes.
5. **Error: unknown seq**: if cBot receives `bar_done` with a seq it didn't send, log + ignore.
6. **Error: timeout**: if cBot waits > 30 s for `bar_done`, `Print("CBOT|BAR_TIMEOUT|seq=N")` + `Stop()`.
7. **Error: engine crash mid-run**: cBot's DEALER poller detects socket error → `Print("CBOT|ENGINE_DISCONNECTED")` + `Stop()`.

---

## Threading Contract

- Engine ROUTER sends go through `NetMQQueue<T>` registered in the same `NetMQPoller` as the ROUTER socket. No cross-thread socket access.
- cBot DEALER receive happens on the poller thread, which enqueues to `BlockingCollection<JsonElement>` (or `ConcurrentQueue` + `ManualResetEventSlim`). The bar handler thread drains this collection while blocked waiting for `bar_done`.
- cBot bar handler (`OnBarClosed`) runs on cTrader's main thread. Blocking here pauses the simulation — this is intentional.

## Determinism Guarantee

Same bar fixture + same strategy + same risk config → byte-identical trade list because:
1. Single ordered socket stream (DEALER↔ROUTER FIFO)
2. Simulation paused per bar (cBot blocks in bar handler)
3. Orders executed at their own bar's simulated time
4. No `Task.WhenAll`, no competing consumers, no fire-and-forget state mutations
