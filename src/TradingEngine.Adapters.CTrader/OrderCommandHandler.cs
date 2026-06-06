using System;
using cAlgo.API;

namespace TradingEngine.Adapters.CTrader
{
    public class OrderCommandHandler
    {
        private readonly PipeClient _pipe;
        private readonly Robot _robot;
        private readonly ExecutionEventPublisher _executionPublisher;
        private readonly AccountUpdatePublisher _accountPublisher;

        public OrderCommandHandler(PipeClient pipe, Robot robot,
            ExecutionEventPublisher executionPublisher,
            AccountUpdatePublisher accountPublisher)
        {
            _pipe = pipe;
            _robot = robot;
            _executionPublisher = executionPublisher;
            _accountPublisher = accountPublisher;
        }

        public void Handle(PipeMessage message)
        {
            switch (message.Type)
            {
                case "SubmitOrder":
                    HandleSubmitOrder(message.Payload);
                    break;
                case "ModifyOrder":
                    HandleModifyOrder(message.Payload);
                    break;
                case "CancelOrder":
                    HandleCancelOrder(message.Payload);
                    break;
                case "ClosePosition":
                    HandleClosePosition(message.Payload);
                    break;
            }
        }

        private void HandleSubmitOrder(string payload)
        {
            try
            {
                var data = MessageSerializer.Deserialize<SubmitOrderData>(payload);
                var symbol = _robot.Symbols.GetSymbol(data.Symbol);
                if (symbol == null)
                {
                    _executionPublisher.Publish(data.CorrelationId, "Rejected", null, 0,
                        "Unknown symbol: " + data.Symbol, DateTime.UtcNow);
                    return;
                }

                var tradeType = data.Direction == "Long" ? TradeType.Buy : TradeType.Sell;
                var volumeInUnits = LotsToVolume(data.Lots, symbol);
                var slPips = PriceToPips(data.SlPrice, symbol);
                var tpPips = PriceToPips(data.TpPrice, symbol);

                var result = _robot.ExecuteMarketOrder(
                    tradeType,
                    data.Symbol,
                    volumeInUnits,
                    "Shamshir",
                    slPips,
                    tpPips);

                if (result != null && result.IsSuccessful)
                {
                    var pos = result.Position;
                    _executionPublisher.Publish(
                        data.CorrelationId,
                        "Filled",
                        pos.EntryPrice,
                        pos.VolumeInUnits,
                        null,
                        pos.EntryTime);

                    _accountPublisher.Publish(
                        _robot.Account.Balance,
                        _robot.Account.Equity,
                        _robot.Account.Equity - _robot.Account.Balance,
                        DateTime.UtcNow);
                }
                else
                {
                    var error = result != null ? result.Error.ToString() : "Null result";
                    _executionPublisher.Publish(
                        data.CorrelationId,
                        "Rejected",
                        null,
                        0,
                        error,
                        DateTime.UtcNow);
                }
            }
            catch (Exception ex)
            {
                var data2 = MessageSerializer.Deserialize<SubmitOrderData>(payload);
                _executionPublisher.Publish(data2.CorrelationId, "Rejected", null, 0,
                    ex.Message, DateTime.UtcNow);
            }
        }

        private void HandleModifyOrder(string payload)
        {
            try
            {
                var data = MessageSerializer.Deserialize<ModifyOrderData>(payload);
                foreach (var pos in _robot.Positions)
                {
                    if (pos.Label == "Shamshir")
                    {
                        _robot.ModifyPosition(pos, pos.VolumeInUnits);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _robot.Print("ModifyOrder error: " + ex.Message);
            }
        }

        private void HandleCancelOrder(string payload)
        {
            try
            {
                var data = MessageSerializer.Deserialize<CancelOrderData>(payload);
                foreach (var order in _robot.PendingOrders)
                {
                    if (order.Label == "Shamshir")
                    {
                        _robot.CancelPendingOrder(order);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _robot.Print("CancelOrder error: " + ex.Message);
            }
        }

        private void HandleClosePosition(string payload)
        {
            try
            {
                foreach (var pos in _robot.Positions)
                {
                    if (pos.Label == "Shamshir")
                    {
                        var result = _robot.ClosePosition(pos);
                        if (result != null && result.IsSuccessful)
                        {
                            _accountPublisher.Publish(
                                _robot.Account.Balance,
                                _robot.Account.Equity,
                                _robot.Account.Equity - _robot.Account.Balance,
                                DateTime.UtcNow);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _robot.Print("ClosePosition error: " + ex.Message);
            }
        }

        private static double LotsToVolume(double lots, cAlgo.API.Internals.Symbol symbol)
        {
            var rawVolume = lots * 100000.0;
            var step = symbol.VolumeInUnitsStep;
            if (step <= 0) return rawVolume;
            return Math.Floor(rawVolume / step) * step;
        }

        private static double? PriceToPips(double price, cAlgo.API.Internals.Symbol symbol)
        {
            if (price <= 0) return null;
            var diff = Math.Abs(price - symbol.Bid);
            return diff / symbol.PipSize;
        }

        private class SubmitOrderData
        {
            public Guid CorrelationId { get; set; }
            public string Symbol { get; set; }
            public string Direction { get; set; }
            public double Lots { get; set; }
            public double SlPrice { get; set; }
            public double TpPrice { get; set; }
        }

        private class ModifyOrderData
        {
            public Guid OrderId { get; set; }
            public double NewStopLoss { get; set; }
            public double NewTakeProfit { get; set; }
        }

        private class CancelOrderData
        {
            public Guid OrderId { get; set; }
        }

        private class ClosePositionData
        {
            public Guid PositionId { get; set; }
        }
    }
}
