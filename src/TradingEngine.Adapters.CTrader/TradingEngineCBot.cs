using System;
using System.Threading;

namespace TradingEngine.Adapters.CTrader
{
    public class TradingEngineCBot
    {
        private PipeClient _pipe;
        private TickPublisher _tickPublisher;
        private BarPublisher _barPublisher;
        private AccountUpdatePublisher _accountPublisher;
        private OrderCommandHandler _commandHandler;
        private volatile bool _running;

        public void Start()
        {
            _pipe = new PipeClient("trading-engine");
            _pipe.OnMessageReceived += OnPipeMessage;
            _pipe.OnDisconnected += OnPipeDisconnected;

            _tickPublisher = new TickPublisher(_pipe);
            _barPublisher = new BarPublisher(_pipe);
            _accountPublisher = new AccountUpdatePublisher(_pipe);
            _commandHandler = new OrderCommandHandler(_pipe);

            Console.WriteLine("Connecting to engine...");
            if (_pipe.Connect(5000))
            {
                Console.WriteLine("Connected to Shamshir engine.");
                _running = true;
            }
            else
            {
                Console.WriteLine("Failed to connect to engine. Retrying in 10s...");
            }
        }

        public void Stop()
        {
            _running = false;
            if (_pipe != null)
            {
                _pipe.Disconnect();
            }
        }

        public void OnTick(string symbol, double bid, double ask, DateTime timestamp)
        {
            if (_running && _pipe != null)
            {
                _tickPublisher.Publish(symbol, bid, ask, timestamp);
                _accountPublisher.Publish(100000, 100000, 0, timestamp);
            }
        }

        public void OnBar(string symbol, string timeframe, DateTime openTime,
            double open, double high, double low, double close, double volume)
        {
            if (_running && _pipe != null)
            {
                _barPublisher.Publish(symbol, timeframe, openTime, open, high, low, close, volume);
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
            Console.WriteLine("Disconnected from engine. Attempting reconnect...");
            _running = false;
        }
    }
}
