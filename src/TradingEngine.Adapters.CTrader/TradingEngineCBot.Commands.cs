using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using cAlgo.API;
using cAlgo.API.Internals;
using NetMQ;
using NetMQ.Sockets;

namespace TradingEngine.Adapters.CTrader;

// Engine-issued command execution: submit/close/cancel/modify orders arriving inside the
// bar_done round-trip, plus the F33 protection doctrine (absolute prices, snap-and-verify) and
// resting-order expiry. Split from TradingEngineCBot.cs verbatim — file organization only.
public partial class TradingEngineCBot
{
    private object ExecuteSubmitOrder(JsonElement cmd)
    {
        var clientOrderId = cmd.GetProperty("clientOrderId").GetString()!;
        var symbol = cmd.GetProperty("symbol").GetString()!;
        var direction = cmd.GetProperty("direction").GetString()!;
        var lots = cmd.GetProperty("lots").GetDouble();
        var slPrice = cmd.GetProperty("slPrice").GetDouble();
        var tpPrice = cmd.GetProperty("tpPrice").GetDouble();
        var orderType = cmd.TryGetProperty("orderType", out var ot) ? ot.GetString() : "Market";
        var limitPrice = cmd.TryGetProperty("limitPrice", out var lp) ? lp.GetDouble() : 0.0;
        var expiryBars = cmd.TryGetProperty("expiryBars", out var eb) ? eb.GetInt32() : 0;

        Diag($"CMD_RECV|submit_order|{clientOrderId}|{symbol}|{direction}|type={orderType}|lots={lots:F4}|limit={limitPrice:F5}");

        var sym = Symbols.GetSymbol(symbol);
        if (sym is null)
            return MakeExecResult(clientOrderId, "entry_fill", 0, "Rejected", 0, lots, "Unknown symbol: " + symbol);

        var tradeType = direction == "Long" ? TradeType.Buy : TradeType.Sell;
        var volumeInUnits = Math.Floor(lots * sym.LotSize / sym.VolumeInUnitsStep) * sym.VolumeInUnitsStep;

        // F33: slPrice/tpPrice are ABSOLUTE PRICES. The legacy overloads of PlaceLimitOrder /
        // PlaceStopOrder / ExecuteMarketOrder read their stopLoss/takeProfit arguments as a DISTANCE
        // IN PIPS ("Stop loss in pips" — cAlgo.API.xml), so passing a price put every stop
        // `price * pipSize` away from entry: a 1.2-pip stop on EURUSD, a 7,708-point stop on BTCUSD.
        // Pending orders now take ProtectionType.Absolute. ExecuteMarketOrder has no absolute
        // overload, so it gets a pip distance derived from the live reference price (never naked) and
        // is snapped to the exact intended prices immediately after the fill, below.
        double? sl = slPrice > 0 ? slPrice : null;
        double? tp = tpPrice > 0 ? tpPrice : null;

        TradeResult? result;
        if (orderType == "Limit" && limitPrice > 0)
        {
            // P2 (F31): label MUST be clientOrderId, not a shared literal — ProcessLimitExpiry's
            // cancel path and OnPositionOpened's fill-correlation path both match on PendingOrders/
            // Position.Label == clientOrderId. A shared "Shamshir" label made both permanently unable
            // to find their order, so pending orders were never cancelled on expiry AND a resting
            // order that later filled natively in cTrader was invisible to the engine (0 fills
            // reported despite cTrader actually opening positions) — confirmed live 2026-07-11.
            result = PlaceLimitOrder(tradeType, symbol, volumeInUnits, limitPrice, clientOrderId,
                sl, tp, ProtectionType.Absolute);
        }
        else if (orderType == "Stop" && limitPrice > 0)
        {
            result = PlaceStopOrder(tradeType, symbol, volumeInUnits, limitPrice, clientOrderId,
                sl, tp, ProtectionType.Absolute);
        }
        else
        {
            var reference = tradeType == TradeType.Buy ? sym.Ask : sym.Bid;
            result = ExecuteMarketOrder(tradeType, symbol, volumeInUnits, "Shamshir",
                ToProtectionPips(sl, reference, tradeType, isStopLoss: true, sym),
                ToProtectionPips(tp, reference, tradeType, isStopLoss: false, sym),
                clientOrderId);
        }

        // F38: record the venue's own account of this submit — its clock, its quote, and whether it
        // rested the order or filled it on the spot. A resting order that later fills THROUGH its limit
        // price is impossible; an order that was already marketable when it arrived explains it exactly.
        // Only the submit-time quote can tell those apart, and it is recorded nowhere else.
        _tradeLog.RecordOrderSubmit(clientOrderId, orderType ?? "Market", direction,
            limitPrice, sym.Bid, sym.Ask, EpochMs(Server.TimeInUtc),
            result?.IsSuccessful != true ? "rejected"
                : result.Position is null ? "pending"
                : "immediate",
            result?.IsSuccessful != true ? (result?.Error.ToString() ?? "Null result") : "");

        if (result?.IsSuccessful == true)
        {
            var pos = result.Position;

            if ((orderType == "Limit" || orderType == "Stop") && expiryBars > 0 && pos is null)
            {
                _pendingEntryOrders[clientOrderId] = new PendingEntryOrder
                {
                    BarsRemaining = expiryBars,
                    Symbol = symbol,
                    StopLoss = sl,
                    TakeProfit = tp,
                };
                return MakeExecResult(clientOrderId, "pending_limit", 0, "Pending", limitPrice, lots, null);
            }

            if (pos is null)
            {
                _ordersExecuted++;
                PublishAccount();
                return MakeExecResult(clientOrderId, "entry_fill", 0, "Filled", 0, lots, null);
            }

            _positionMap[pos.Id] = Guid.Parse(clientOrderId);
            _ordersExecuted++;

            _tradeLog.RecordOpen(pos.Id, clientOrderId, direction, pos.EntryPrice,
                pos.VolumeInUnits / sym.LotSize, EpochMs(Server.TimeInUtc), Account.Balance, Account.Equity);

            // F33/F37: snap the position to the EXACT intended prices. The market-order path can only
            // express protection as a pip distance from the fill, so it lands a tick or two off. The
            // old guard here used `&&`, which skipped the repair whenever either level already
            // matched, and it was unreachable for pending orders (pos is null at submit time).
            ApplyProtection(pos, sl, tp, clientOrderId);

            PublishAccount();
            return MakeExecResult(clientOrderId, "entry_fill", pos.Id, "Filled", pos.EntryPrice,
                pos.VolumeInUnits / sym.LotSize, null);
        }

        return MakeExecResult(clientOrderId, "entry_fill", 0, "Rejected", 0, lots, result?.Error.ToString() ?? "Null result");
    }

    // F33: convert an absolute protection price into the positive pip distance the legacy
    // market-order overload expects. Returns null when unset, or when the price sits on the wrong
    // side of the reference — a negative distance would be silently rejected by the venue and leave
    // the position naked.
    private static double? ToProtectionPips(double? price, double reference, TradeType tradeType, bool isStopLoss, Symbol sym)
    {
        if (price is null || reference <= 0 || sym.PipSize <= 0) return null;

        // A stop-loss sits below a buy and above a sell; a take-profit is the mirror of that.
        var isBuy = tradeType == TradeType.Buy;
        var distance = isBuy == isStopLoss ? reference - price.Value : price.Value - reference;

        var pips = distance / sym.PipSize;
        return pips > 0 ? pips : null;
    }

    // F33: set the venue's protection to the EXACT prices the engine asked for, then verify the venue
    // agrees. The verification is the point: for four sessions the venue silently held stops at a
    // completely different distance from the ones in our journal, and nothing compared the two.
    private void ApplyProtection(Position pos, double? sl, double? tp, string clientOrderId)
    {
        if (sl is null && tp is null) return;

        if (pos.StopLoss != sl || pos.TakeProfit != tp)
        {
            try
            {
                var modify = ModifyPosition(pos, sl ?? pos.StopLoss, tp ?? pos.TakeProfit, ProtectionType.Absolute);
                if (modify?.IsSuccessful != true)
                    Print($"CBOT|PROTECTION_SET_FAIL|posId={pos.Id}|orderId={clientOrderId}|err={modify?.Error}");
            }
            catch (Exception ex)
            {
                Print($"CBOT|PROTECTION_SET_ERR|posId={pos.Id}|orderId={clientOrderId}|{ex.Message}");
            }
        }

        var sym = Symbols.GetSymbol(pos.SymbolName);
        var tick = sym?.TickSize ?? 0.0;
        if (Off(pos.StopLoss, sl, tick) || Off(pos.TakeProfit, tp, tick))
        {
            _tradeLog.ProtectionMismatches++;
            Print($"CBOT|PROTECTION_MISMATCH|posId={pos.Id}|orderId={clientOrderId}" +
                  $"|wantSl={Fmt(sl)}|gotSl={Fmt(pos.StopLoss)}|wantTp={Fmt(tp)}|gotTp={Fmt(pos.TakeProfit)}");
        }

        static bool Off(double? actual, double? intended, double tick)
            => intended is not null && (actual is null || Math.Abs(actual.Value - intended.Value) > Math.Max(tick, 1e-9));

        static string Fmt(double? v) => v is null ? "-" : v.Value.ToString("F5");
    }

    private void ProcessLimitExpiry(string symbol, string timeframe)
    {
        if (_pendingEntryOrders.Count == 0) return;

        var expired = new List<string>();
        foreach (var (clientOrderId, entry) in _pendingEntryOrders)
        {
            if (!string.Equals(entry.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                continue;

            entry.BarsRemaining--;
            if (entry.BarsRemaining <= 0)
            {
                expired.Add(clientOrderId);
                Print($"CBOT|LIMIT_EXPIRED|orderId={clientOrderId}|symbol={symbol}");
            }
        }

        foreach (var id in expired)
        {
            _pendingEntryOrders.Remove(id);
            foreach (var order in PendingOrders)
            {
                if (!string.Equals(order.Label, id, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    CancelPendingOrder(order);
                }
                catch (Exception ex)
                {
                    Print($"CBOT|CANCEL_PENDING_FAIL|orderId={id}|err={ex.Message}");
                }
                break;
            }
        }
    }

    private object ExecuteCancelOrder(JsonElement cmd)
    {
        var orderIdStr = cmd.GetProperty("orderId").GetString()!;
        foreach (var pos in Positions)
        {
            if (_positionMap.TryGetValue(pos.Id, out var orderGuid) && orderGuid.ToString() == orderIdStr)
            {
                // C2: close the open position. For pending (limit) orders, cancel via the platform.
                var result = ClosePosition(pos);
                Diag($"CANCEL_ORDER|{orderIdStr}|posId={pos.Id}|success={result?.IsSuccessful}");
                if (result?.IsSuccessful == true) PublishAccount();
                return MakeExecResult(orderIdStr, "entry_cancelled", pos.Id,
                    result?.IsSuccessful == true ? "Cancelled" : "Rejected",
                    pos.CurrentPrice > 0 ? pos.CurrentPrice : 0d, pos.VolumeInUnits / (Symbols.GetSymbol(pos.SymbolName)?.LotSize ?? 100000.0),
                    "ENTRY_EXPIRED");
            }
        }
        Print($"CBOT|CANCEL_NOT_FOUND|orderId={orderIdStr}");
        return MakeExecResult(orderIdStr, "entry_cancelled", 0, "Rejected", 0, 0, "Order not found: " + orderIdStr);
    }

    private object ExecuteClosePosition(JsonElement cmd)
    {
        var positionIdStr = cmd.GetProperty("positionId").GetString()!;
        foreach (var pos in Positions)
        {
            if (_positionMap.TryGetValue(pos.Id, out var orderGuid) && orderGuid.ToString() == positionIdStr)
            {
                var clientOrderId = orderGuid;
                // Capture realized PnL BEFORE closing — at the close instant the position's
                // floating Gross/Net P&L equals what's realized. Previously MakeExecResult
                // hard-coded 0, so every engine-requested close (SL/TP via the engine's exit
                // simulation, force-close) recorded $0 PnL in the ledger while only cTrader's own
                // async closes carried real PnL. This corrupted the ledger for most trades.
                var grossProfit = pos.GrossProfit;
                var netProfit = pos.NetProfit;
                var result = ClosePosition(pos);
                // Read commission/swap AFTER the close: before it, only the entry-side commission has
                // been charged, so the venue-reported figure would be half the round-trip cost.
                var commission = pos.Commissions;
                var swap = pos.Swap;
                _commandCloses.Add(pos.Id);
                Diag($"CLOSE_POS|{positionIdStr}|success={result?.IsSuccessful}|net={netProfit:F2}");
                if (result?.IsSuccessful == true) PublishAccount();
                return MakeExecResult(clientOrderId.ToString(), "close", pos.Id,
                    result?.IsSuccessful == true ? "Filled" : "Rejected",
                    pos.CurrentPrice > 0 ? pos.CurrentPrice : 0d, pos.VolumeInUnits / (Symbols.GetSymbol(pos.SymbolName)?.LotSize ?? 100000.0), null,
                    grossProfit, netProfit, commission, swap);
            }
        }
        Print($"CBOT|CLOSE_NOT_FOUND|positionId={positionIdStr}");
        return MakeExecResult(Guid.Empty.ToString(), "close", 0, "Rejected", 0, 0, "Position not found: " + positionIdStr);
    }

    private object ExecuteClosePartialPosition(JsonElement cmd)
    {
        var positionIdStr = cmd.GetProperty("positionId").GetString()!;
        var closeLots = cmd.TryGetProperty("lots", out var lotsEl) ? (decimal)lotsEl.GetDouble() : 0m;
        if (closeLots <= 0) closeLots = 0.01m;

        foreach (var pos in Positions)
        {
            if (_positionMap.TryGetValue(pos.Id, out var orderGuid) && orderGuid.ToString() == positionIdStr)
            {
                var clientOrderId = orderGuid;
                var lotSize = Symbols.GetSymbol(pos.SymbolName)?.LotSize ?? 100000.0;
                var volumeInUnits = (int)((double)closeLots * lotSize);
                if (volumeInUnits <= 0) volumeInUnits = 1000;

                var fraction = pos.VolumeInUnits > 0 ? Math.Min(1.0, volumeInUnits / (double)pos.VolumeInUnits) : 1.0;
                var grossProfit = pos.GrossProfit * fraction;
                var result = ClosePosition(pos, volumeInUnits);
                // M1 (iter-35 B2): read commission/swap AFTER the close. The full-close path does
                // the same — before close only entry-side commission is charged. After partial
                // close these reflect round-trip costs for the remaining portion; scaling by
                // fraction gives a close estimate for the closed portion.
                var commission = pos.Commissions * fraction;
                var swap = pos.Swap * fraction;
                var netProfit = grossProfit + commission + swap;
                Diag($"CLOSE_PARTIAL|{positionIdStr}|lots={closeLots}|units={volumeInUnits}|success={result?.IsSuccessful}|net={netProfit:F2}");
                if (result?.IsSuccessful == true) PublishAccount();

                return MakeExecResult(clientOrderId.ToString(), "partial_close", pos.Id,
                    result?.IsSuccessful == true ? "Filled" : "Rejected",
                    pos.CurrentPrice > 0 ? pos.CurrentPrice : 0d, (double)closeLots, null,
                    grossProfit, netProfit, commission, swap);
            }
        }
        Print($"CBOT|CLOSE_PARTIAL_NOT_FOUND|positionId={positionIdStr}");
        return MakeExecResult(Guid.Empty.ToString(), "partial_close", 0, "Rejected", 0, 0, "Position not found: " + positionIdStr);
    }

    // V3 — apply a stop-loss/take-profit modification and echo the venue-confirmed levels so the
    // engine can write them back. Previously modify_order commands were silently dropped (the
    // bar_done loop had no branch for them), so trailing-stop updates never reached the venue.
    private object ExecuteModifyOrder(JsonElement cmd)
    {
        var orderIdStr = cmd.GetProperty("orderId").GetString()!;
        var newSl = cmd.TryGetProperty("newSl", out var slEl) ? slEl.GetDouble() : 0.0;
        var newTp = cmd.TryGetProperty("newTp", out var tpEl) ? tpEl.GetDouble() : 0.0;

        foreach (var pos in Positions)
        {
            if (_positionMap.TryGetValue(pos.Id, out var guid) && guid.ToString() == orderIdStr)
            {
                var appliedSl = newSl > 0 ? newSl : (pos.StopLoss ?? 0.0);
                var appliedTp = newTp > 0 ? newTp : (pos.TakeProfit ?? 0.0);
                try
                {
                    // F33: newSl/newTp are absolute prices — say so explicitly rather than relying on
                    // the deprecated overload's implicit convention.
                    var result = ModifyPosition(pos, appliedSl > 0 ? appliedSl : (double?)null,
                        appliedTp > 0 ? appliedTp : (double?)null, ProtectionType.Absolute);
                    Diag($"MODIFY|{orderIdStr}|sl={appliedSl:F5}|tp={appliedTp:F5}|success={result?.IsSuccessful}");
                    return MakeModifyResult(guid.ToString(), pos.Id,
                        result?.IsSuccessful == true ? "Filled" : "Rejected", appliedSl, appliedTp);
                }
                catch (Exception ex)
                {
                    Print($"CBOT|MODIFY_ERR|{orderIdStr}|{ex.Message}");
                    return MakeModifyResult(guid.ToString(), pos.Id, "Rejected", 0, 0);
                }
            }
        }
        Print($"CBOT|MODIFY_NOT_FOUND|orderId={orderIdStr}");
        return MakeModifyResult(Guid.Empty.ToString(), 0, "Rejected", 0, 0);
    }

    private object MakeModifyResult(string clientOrderId, long positionId, string state,
        double slPrice, double tpPrice)
    {
        return new
        {
            clientOrderId,
            kind = "modify",
            positionId,
            state,
            slPrice,
            tpPrice,
            fillPrice = 0.0,
            filledLots = 0.0,
            reason = (string?)null,
            simTime = Server.TimeInUtc.ToString("o")
        };
    }

    private object MakeExecResult(string clientOrderId, string kind, long positionId,
        string state, double fillPrice, double filledLots, string? reason,
        double grossProfit = 0.0, double netProfit = 0.0,
        double commission = 0.0, double swap = 0.0)
    {
        return new
        {
            clientOrderId,
            kind,
            positionId,
            state,
            fillPrice,
            filledLots,
            reason,
            simTime = Server.TimeInUtc.ToString("o"),
            grossProfit,
            netProfit,
            commission,
            swap
        };
    }
}
