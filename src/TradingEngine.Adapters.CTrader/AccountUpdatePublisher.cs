using System;
using Newtonsoft.Json;

namespace TradingEngine.Adapters.CTrader
{
    public class AccountUpdatePublisher
    {
        private readonly PipeClient _pipe;

        public AccountUpdatePublisher(PipeClient pipe)
        {
            _pipe = pipe;
        }

        public void Publish(double balance, double equity, double floatingPnl, DateTime timestamp)
        {
            var payload = JsonConvert.SerializeObject(new
            {
                Balance = balance,
                Equity = equity,
                FloatingPnL = floatingPnl,
                TimestampUtc = timestamp.ToString("o")
            });

            _pipe.Send(new PipeMessage
            {
                Type = "AccountUpdate",
                Payload = payload
            });
        }
    }
}
