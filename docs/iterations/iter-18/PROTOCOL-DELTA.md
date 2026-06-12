# Protocol Delta — Iteration 18

## New Fields in `submit_order` Command

The `bar_done.commands[].submit_order` message gains optional fields for limit order and slippage support.

### Existing fields (unchanged)
```json
{
  "type": "submit_order",
  "clientOrderId": "guid",
  "symbol": "EURUSD",
  "direction": "buy|sell",
  "lots": 0.1,
  "slPrice": 1.08000,
  "tpPrice": 1.09000
}
```

### New optional fields
```json
{
  "orderType": "Market|Limit",        // default: "Market"
  "limitPrice": 1.08500,              // present when orderType == "Limit"
  "expiryBars": 3,                    // cancel if not filled after N bars
  "maxSlippagePips": 2.0              // for Market orders: reject if fill exceeds this
}
```

### New Execution States
- `state: "cancelled"` — limit order expired without fill
- `state: "slippage_exceeded"` — market order fill price exceeded maxSlippagePips

### New Execution Fields (cBot → Engine)
```json
{
  "grossProfit": 123.45,
  "netProfit": 121.00,
  "commission": -2.45,
  "swap": 0.0
}
```

These are already sent by the cBot (iter-17 C1) and are now parsed by `NetMQBrokerAdapter` into `ExecutionEvent`.

## Backward Compatibility
- cBot ignores unknown fields — old cBot works with new engine
- Engine defaults missing fields — old cBot works with new engine
- All existing tests continue passing
