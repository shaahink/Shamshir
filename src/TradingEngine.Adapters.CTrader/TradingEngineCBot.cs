using System;
using System.Threading;
using cAlgo.API;

namespace TradingEngine.Adapters.CTrader
{
    [Robot(AccessRights = AccessRights.None)]
    public class TradingEngineCBot : Robot
    {
        private PipeClient _pipe;
        private TickPublisher _tickPublisher;
        private BarPublisher _barPublisher;
        private AccountUpdatePublisher _accountPublisher;
        private ExecutionEventPublisher _executionPublisher;
        private OrderCommandHandler _commandHandler;
        private Thread _readThread;
        private volatile bool _running;

        private static readonly string[] DefaultSymbols = new[] { "EURUSD", "GBPUSD", "USDJPY", "GBPJPY", "XAUUSD" };

        protected override void OnStart()
        {
            _pipe = new PipeClient("trading-engine");
            _pipe.OnMessageReceived += OnPipeMessage;
            _pipe.OnDisconnected += OnPipeDisconnected;

            _tickPublisher = new TickPublisher(_pipe);
            _barPublisher = new BarPublisher(_pipe);
            _accountPublisher = new AccountUpdatePublisher(_pipe);
            _executionPublisher = new ExecutionEventPublisher(_pipe);
            _commandHandler = new OrderCommandHandler(_pipe, this, _executionPublisher, _accountPublisher);

            Print("Shamshir engine cBot starting. Connecting to pipe...");

            if (_pipe.Connect(5000))
            {
                _running = true;
                _readThread = new Thread(ReadLoop);
                _readThread.IsBackground = true;
                _readThread.Start();

                SendInitialState();
                Print("Connected to Shamshir engine.");
            }
            else
            {
                Print("Failed to connect to Shamshir engine. Retrying...");
                _pipe.RetryConnect();
            }
        }

        private void SendInitialState()
        {
            _accountPublisher.Publish(Account.Balance, Account.Equity, Account.Equity - Account.Balance, Server.TimeInUtc);

            foreach (var pos in Positions)
            {
                _executionPublisher.Publish(
                    new Guid(),
                    "Filled",
                    pos.EntryPrice,
                    pos.VolumeInUnits,
                    null,
                    pos.EntryTime);
            }
        }

        protected override void OnTick()
        {
            if (!_running) return;

            _tickPublisher.Publish(SymbolName, Symbol.Bid, Symbol.Ask, Server.TimeInUtc);

            _accountPublisher.Publish(
                Account.Balance,
                Account.Equity,
                Account.Equity - Account.Balance,
                Server.TimeInUtc);
        }

        protected override void OnBar()
        {
            if (!_running) return;

            var bars = MarketData.GetBars(TimeFrame.Hour, SymbolName);
            if (bars == null || bars.Count == 0) return;

            var lastBar = bars.Last(1);
            _barPublisher.Publish(
                SymbolName,
                "H1",
                lastBar.OpenTime,
                lastBar.Open,
                lastBar.High,
                lastBar.Low,
                lastBar.Close,
                lastBar.TickVolume);
        }

        protected override void OnStop()
        {
            _running = false;
            if (_pipe != null)
            {
                _pipe.Disconnect();
            }
        }

        private void ReadLoop()
        {
            while (_running)
            {
                _pipe.ReadMessage();
            }
        }

        private void OnPipeMessage(PipeMessage message)
        {
            if (_commandHandler != null)
            {
                _commandHandler.Handle(message);
            }
        }

        private void OnPipeDisconnected()
        {
            Print("Disconnected from Shamshir engine. Attempting reconnect...");
            _running = false;
            _pipe.RetryConnect();
        }
    }
}
