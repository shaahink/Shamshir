using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace TradingEngine.Adapters.CTrader
{
    public class PipeClient
    {
        private const int MaxRetries = 3;
        private static readonly int[] RetryDelays = new[] { 2000, 4000, 8000 };

        private readonly string _pipeName;
        private NamedPipeClientStream _pipe;

        public event Action<PipeMessage> OnMessageReceived;
        public event Action OnDisconnected;

        public PipeClient(string pipeName = "trading-engine")
        {
            _pipeName = pipeName;
            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        }

        public bool Connect(int timeoutMs = 5000)
        {
            try
            {
                _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                _pipe.Connect(timeoutMs);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void RetryConnect()
        {
            for (var i = 0; i < MaxRetries; i++)
            {
                Thread.Sleep(RetryDelays[i]);
                try
                {
                    _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    _pipe.Connect(5000);
                    return;
                }
                catch
                {
                }
            }
        }

        public void Disconnect()
        {
            if (_pipe != null && _pipe.IsConnected)
            {
                try { _pipe.Dispose(); }
                catch { }
            }
        }

        public void Send(PipeMessage message)
        {
            if (_pipe == null || !_pipe.IsConnected)
            {
                return;
            }

            try
            {
                var data = message.ToByteArray();
                _pipe.Write(data, 0, data.Length);
                _pipe.Flush();
            }
            catch
            {
                if (OnDisconnected != null)
                {
                    OnDisconnected();
                }
            }
        }

        public void ReadMessage()
        {
            try
            {
                var lengthBuffer = new byte[4];
                var bytesRead = _pipe.Read(lengthBuffer, 0, 4);
                if (bytesRead < 4)
                {
                    if (OnDisconnected != null)
                    {
                        OnDisconnected();
                    }
                    return;
                }

                var length = BitConverter.ToInt32(lengthBuffer, 0);
                var messageBuffer = new byte[length];
                var totalRead = 0;

                while (totalRead < length)
                {
                    bytesRead = _pipe.Read(messageBuffer, totalRead, length - totalRead);
                    if (bytesRead == 0)
                    {
                        if (OnDisconnected != null)
                        {
                            OnDisconnected();
                        }
                        return;
                    }
                    totalRead += bytesRead;
                }

                var json = System.Text.Encoding.UTF8.GetString(messageBuffer, 0, totalRead);
                var message = PipeMessage.FromJson(json);
                if (OnMessageReceived != null)
                {
                    OnMessageReceived(message);
                }
            }
            catch
            {
                if (OnDisconnected != null)
                {
                    OnDisconnected();
                }
            }
        }
    }
}
