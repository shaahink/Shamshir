using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TradingEngine.Adapters.CTrader
{
    public class OrderCommandHandler
    {
        private readonly PipeClient _pipe;

        public OrderCommandHandler(PipeClient pipe)
        {
            _pipe = pipe;
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
            Console.WriteLine("SubmitOrder: " + payload);
        }

        private void HandleModifyOrder(string payload)
        {
            Console.WriteLine("ModifyOrder: " + payload);
        }

        private void HandleCancelOrder(string payload)
        {
            Console.WriteLine("CancelOrder: " + payload);
        }

        private void HandleClosePosition(string payload)
        {
            Console.WriteLine("ClosePosition: " + payload);
        }
    }
}
