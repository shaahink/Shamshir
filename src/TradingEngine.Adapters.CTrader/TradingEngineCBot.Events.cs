using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using cAlgo.API;
using cAlgo.API.Internals;
using NetMQ;
using NetMQ.Sockets;

namespace TradingEngine.Adapters.CTrader;

// Venue event handlers: the lock-step bar loop (BarClosed -> publish -> await bar_done),
// dealer-socket receive, resting-order fills (F31) and venue-initiated closes, plus the
// position-map rebuild/snapshot used for reconnect reconciliation (V1/V2). Split from
// TradingEngineCBot.cs verbatim — file organization only.
public partial class TradingEngineCBot
{
    private void OnBarClosed(BarClosedEventArgs args)
    {
        // iter-marketdata-tape P2: in recorder mode a closed bar is captured to a shard, not sent to the engine.
        if (Record) { RecordBar(args); return; }

        _barEventCount++;
        var bars = args.Bars;

        // F38: publish the bar that JUST closed — which, when BarClosed fires, is Last(0). This read was
        // Last(1), i.e. the bar before it, so the engine was fed every bar one full bar stale: it decided
        // on 4-hour-old prices and its orders reached the venue a bar late. Measured on the venue's own
        // clock (report barClock): openTime→publish was a steady 8h on H4 where publishing at the close
        // is 4h. That single bar of lag is what made a limit order arrive already marketable — cTrader
        // filled it at market, THROUGH the limit price, while the tape rested it and filled at the limit.
        // Not lookahead: this bar is complete and closed at the instant the event fires (a bar still
        // forming would have shown a 0h gap, not 4h).
        var bar = bars.Last(0);
        if (bar.Open == 0 && bar.High == 0) return;

        ProcessLimitExpiry(bars.SymbolName, bars.TimeFrame.ShortName);

        var key = (bars.SymbolName, bars.TimeFrame.ShortName, bar.OpenTime);
        if (!_publishedBars.Add(key)) { _duplicateCount++; return; }

        if (Verbose)
            Print($"CBOT|BAR_EVENT|seq={_barEventCount}|" +
                  $"count={bars.Count}|" +
                  $"openTime={bar.OpenTime:yyyy-MM-dd HH:mm}|" +
                  $"open={bar.Open:F5}|high={bar.High:F5}|low={bar.Low:F5}|close={bar.Close:F5}|" +
                  $"dup=false");

        var openTimeUtc = DateTime.SpecifyKind(bar.OpenTime, DateTimeKind.Utc);

        _tradeLog.RecordBarClock(EpochMs(openTimeUtc), EpochMs(Server.TimeInUtc));
        _tradeLog.RecordEquity(Account.Balance, Account.Equity, EpochMs(openTimeUtc));
        if (++_reportCheckpoint % ReportCheckpointEveryNBars == 0)
        {
            var ckptSw = Diagnostics ? System.Diagnostics.Stopwatch.StartNew() : null;
            try { TryWriteReport(); }
            finally { if (ckptSw is not null) _timingCheckpointTotalMs += ckptSw.ElapsedMilliseconds; }
        }

        var symInfo = Symbols.GetSymbol(bars.SymbolName);
        var barJson = Serialize("bar", new
        {
            v = 1,
            seq = _barEventCount,
            symbol = bars.SymbolName,
            period = bars.TimeFrame.ShortName,
            openTime = openTimeUtc.ToString("o"),
            open = bar.Open,
            high = bar.High,
            low = bar.Low,
            close = bar.Close,
            volume = (long)bar.TickVolume,
            spread = (double)(symInfo?.Spread ?? 0),
            simTime = Server.TimeInUtc.ToString("o"),
            account = new { balance = Account.Balance, equity = Account.Equity }
        });
        _dealer!.SendFrame(barJson);
        if (Verbose) Diag($"BAR_SENT|{bars.SymbolName}|{bars.TimeFrame.ShortName}|{openTimeUtc:o}|close={bar.Close:F5}|seq={_barEventCount}");

        var rtSw = Diagnostics ? System.Diagnostics.Stopwatch.StartNew() : null;

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_inbox.TryTake(out var json, 100))
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

                    if (type == "bar_done" && doc.RootElement.TryGetProperty("seq", out var seqEl) && seqEl.GetInt32() == _barEventCount)
                    {
                        if (rtSw is not null)
                        {
                            var elapsed = rtSw.ElapsedMilliseconds;
                            _timingRoundTrips++;
                            _timingRoundTripTotalMs += elapsed;
                            if (elapsed > _timingRoundTripMaxMs) _timingRoundTripMaxMs = elapsed;
                            rtSw = null;
                        }

                        var commands = doc.RootElement.TryGetProperty("commands", out var cmds) ? cmds : default;
                        _cmdsReceived += commands.ValueKind == JsonValueKind.Array ? commands.GetArrayLength() : 0;

                        var procSw = Diagnostics ? System.Diagnostics.Stopwatch.StartNew() : null;
                        var execs = new List<object>();
                        if (commands.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var cmd in commands.EnumerateArray())
                            {
                                var cmdType = cmd.GetProperty("type").GetString();
                                if (cmdType == "submit_order")
                                {
                                    var result = ExecuteSubmitOrder(cmd);
                                    execs.Add(result);
                                }
                                else if (cmdType == "close_position")
                                {
                                    var result = ExecuteClosePosition(cmd);
                                    execs.Add(result);
                                }
                                else if (cmdType == "close_partial")
                                {
                                    var result = ExecuteClosePartialPosition(cmd);
                                    execs.Add(result);
                                }
                                else if (cmdType == "cancel_order")
                                {
                                    var result = ExecuteCancelOrder(cmd);
                                    execs.Add(result);
                                }
                                else if (cmdType == "modify_order")
                                {
                                    var result = ExecuteModifyOrder(cmd);
                                    execs.Add(result);
                                }
                                else if (cmdType == "shutdown")
                                {
                                    Print("CBOT|SHUTDOWN|received via bar_done");
                                    Stop();
                                    return;
                                }
                            }
                        }
                        if (procSw is not null) _timingBarProcTotalMs += procSw.ElapsedMilliseconds;

                        _execsSent += execs.Count;
                        var barResult = Serialize("bar_result", new
                        {
                            v = 1,
                            seq = _barEventCount,
                            execs = execs,
                            account = new { balance = Account.Balance, equity = Account.Equity }
                        });
                        _dealer!.SendFrame(barResult);
                        if (Verbose) Diag($"BAR_RESULT|seq={_barEventCount}|execs={execs.Count}");
                        return;
                    }
                    else if (type == "shutdown")
                    {
                        Print("CBOT|SHUTDOWN|received during bar processing");
                        Stop();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Print($"CBOT|BAR_PROC_ERR|{ex.Message}");
                }
            }
        }

        Print($"CBOT|BAR_TIMEOUT|seq={_barEventCount}|bar_done not received within 30s");
        Stop();
    }

    private void OnDealerReceive(object? sender, NetMQSocketEventArgs e)
    {
        if (!e.Socket.TryReceiveFrameString(out var json) || json is null) return;
        var captured = json;

        try
        {
            using var doc = JsonDocument.Parse(captured);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "hello_ack")
            {
                _connected = true;
                if (Verbose) Diag("HELLO_ACK|received");
                return;
            }
            if (type == "shutdown")
            {
                Print("CBOT|SHUTDOWN|received shutdown, stopping");
                Stop();
                return;
            }
        }
        catch (Exception ex)
        {
            Print($"CBOT|DEALER_RECV_ERR|{ex.Message}|raw={captured.Substring(0, Math.Min(captured.Length, 200))}");
        }

        _inbox.Add(captured);
        if (Verbose) Diag($"DEALER_RECV|inboxDepth={_inbox.Count}|jsonLen={captured.Length}");
    }

    // P2 (F31): a resting Limit/Stop entry order fills OUTSIDE our command flow — cTrader's own
    // backtest engine converts it to a position on its own schedule, with no engine-initiated
    // submit_order round-trip to piggyback a result on. Without this handler the fill is invisible:
    // _positionMap never gets the entry, RecordOpen never fires, and the engine sees 0 trades even
    // though cTrader's own account balance moved. Correlates via Position.Label == clientOrderId
    // (see ExecuteSubmitOrder's PlaceLimitOrder/PlaceStopOrder call — label is now the clientOrderId,
    // not a shared literal, specifically so this lookup can work).
    private void OnPositionOpened(PositionOpenedEventArgs args)
    {
        var pos = args.Position;
        if (!Guid.TryParse(pos.Label, out var clientOrderId)) return; // not one of ours (or a market fill, already handled synchronously)
        if (_positionMap.ContainsKey(pos.Id)) return; // already recorded via the synchronous ExecuteSubmitOrder path

        var clientOrderIdStr = clientOrderId.ToString();
        if (!_pendingEntryOrders.Remove(clientOrderIdStr, out var pending)) return; // not a tracked pending order

        // F33: the resting order was placed with ProtectionType.Absolute, so the position should
        // already carry the exact intended levels — verify that it does rather than assume it.
        ApplyProtection(pos, pending.StopLoss, pending.TakeProfit, clientOrderIdStr);

        var sym = Symbols.GetSymbol(pos.SymbolName);
        var lots = sym is not null ? pos.VolumeInUnits / sym.LotSize : pos.VolumeInUnits / 100_000.0;
        var direction = pos.TradeType == TradeType.Buy ? "Long" : "Short";

        _positionMap[pos.Id] = clientOrderId;
        _ordersExecuted++;

        _tradeLog.RecordOpen(pos.Id, clientOrderIdStr, direction, pos.EntryPrice, lots,
            EpochMs(Server.TimeInUtc), Account.Balance, Account.Equity);

        var execJson = Serialize("exec", new
        {
            v = 1,
            clientOrderId = clientOrderIdStr,
            kind = "entry_fill",
            positionId = pos.Id,
            state = "Filled",
            fillPrice = pos.EntryPrice,
            filledLots = lots,
            reason = (string?)null,
            simTime = Server.TimeInUtc.ToString("o"),
            grossProfit = 0.0,
            netProfit = 0.0,
            commission = 0.0,
            swap = 0.0
        });
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { _dealer?.SendFrame(execJson); break; }
            catch when (attempt < 2) { Thread.Sleep(100); }
            catch (Exception ex) { Print($"CBOT|EXEC_SEND_FAIL|clientOrderId={clientOrderIdStr}|ex={ex.Message}"); }
        }
        _execsSent++;
        if (Verbose) Diag($"EXEC_SENT|{clientOrderIdStr}|Filled|kind=entry_fill|resting-order|fill={pos.EntryPrice:F5}|lots={lots:F4}");
        Print($"CBOT|RESTING_ORDER_FILLED|orderId={clientOrderIdStr}|posId={pos.Id}|fill={pos.EntryPrice:F5}|lots={lots:F4}");
        PublishAccount();
    }

    private void OnPositionClosed(PositionClosedEventArgs args)
    {
        var pos = args.Position;
        if (_positionMap.TryGetValue(pos.Id, out var clientOrderId))
        {
            var sym = Symbols.GetSymbol(pos.SymbolName);
            var lots = sym is not null ? pos.VolumeInUnits / sym.LotSize : pos.VolumeInUnits / 100_000.0;
            var closePrice = pos.CurrentPrice > 0 ? pos.CurrentPrice : pos.EntryPrice;

            // Log EVERY close (command- and venue-initiated) to our own ledger with cTrader's realized
            // values. This is the single source of truth for the report.json/events.json we write.
            var closeEventName = args.Reason switch
            {
                PositionCloseReason.StopLoss => "Stop Loss Hit",
                PositionCloseReason.TakeProfit => "Take Profit Hit",
                _ => "Closed",
            };
            _tradeLog.RecordClose(pos.Id, clientOrderId.ToString(), closeEventName, closePrice,
                pos.GrossProfit, pos.NetProfit, pos.Commissions, pos.Swap, pos.Pips,
                EpochMs(Server.TimeInUtc), Account.Balance, Account.Equity);

            if (_commandCloses.Remove(pos.Id))
            {
                // Command-initiated close: exec already reported via bar_result.execs[]
                _positionMap.Remove(pos.Id);
                PublishAccount();
                return;
            }

            // Venue-initiated close (server-side SL/TP/stop-out, or manual). Propagate cTrader's
            // own close reason so the engine journals "SL"/"TP" instead of the generic "FORCE" —
            // these close intrabar before the engine's bar-level exit detection runs.
            var closeReason = MapCloseReason(args.Reason);
            var execJson = Serialize("exec", new
            {
                v = 1,
                clientOrderId = clientOrderId.ToString(),
                kind = "close",
                positionId = pos.Id,
                state = "Filled",
                fillPrice = closePrice,
                filledLots = lots,
                reason = (string?)null,
                closeReason,
                simTime = Server.TimeInUtc.ToString("o"),
                grossProfit = pos.GrossProfit,
                netProfit = pos.NetProfit,
                commission = pos.Commissions,
                swap = pos.Swap
            });
            // iter-39 C1/C2: retry exec frame send — a single lost close exec = missing trade in DB.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { _dealer?.SendFrame(execJson); break; }
                catch when (attempt < 2) { Thread.Sleep(100); }
                catch (Exception ex) { Print($"CBOT|EXEC_SEND_FAIL|clientOrderId={clientOrderId}|ex={ex.Message}"); }
            }
            _execsSent++;
            if (Verbose) Diag($"EXEC_SENT|{clientOrderId}|Filled|kind=close|reason={closeReason}|fill={closePrice:F5}|lots={lots:F4}|pnl={pos.NetProfit:F2}");
            _positionMap.Remove(pos.Id);
            PublishAccount();
        }
    }

    private static string MapCloseReason(PositionCloseReason reason) => reason switch
    {
        PositionCloseReason.StopLoss => "SL",
        PositionCloseReason.TakeProfit => "TP",
        PositionCloseReason.StopOut => "STOPOUT",
        _ => "CLOSED",
    };

    // V2 — rebuild the venue-id → engine-Guid map from positions that survived a cBot restart,
    // reading the durable Guid stored in each Shamshir position's Comment.
    private void RebuildPositionMap()
    {
        var remapped = 0;
        foreach (var pos in Positions)
        {
            if (pos.Label != "Shamshir") continue;
            if (Guid.TryParse(pos.Comment, out var guid))
            {
                _positionMap[pos.Id] = guid;
                remapped++;
            }
            else
            {
                Print($"CBOT|REMAP_SKIP|posId={pos.Id}|no durable Guid in comment");
            }
        }
        Print($"CBOT|REMAP|positions={remapped}");
    }

    // V1 — snapshot of currently-open Shamshir positions, keyed by the engine clientOrderId Guid,
    // for the engine to reconcile its tracker after a (re)connect.
    private object[] BuildPositionSnapshot()
    {
        var list = new List<object>();
        foreach (var pos in Positions)
        {
            if (!_positionMap.TryGetValue(pos.Id, out var guid)) continue;
            var sym = Symbols.GetSymbol(pos.SymbolName);
            var lots = sym is not null ? pos.VolumeInUnits / sym.LotSize : pos.VolumeInUnits / 100_000.0;
            list.Add(new
            {
                clientOrderId = guid.ToString(),
                symbol = pos.SymbolName,
                direction = pos.TradeType == TradeType.Buy ? "Long" : "Short",
                lots,
                entryPrice = pos.EntryPrice,
                stopLoss = pos.StopLoss ?? 0.0,
                takeProfit = pos.TakeProfit ?? 0.0,
                venuePositionId = pos.Id
            });
        }
        return list.ToArray();
    }
}
