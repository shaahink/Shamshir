using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace TradingEngine.Adapters.CTrader
{
    public class PipeClient
    {
        private readonly string _pipeName;
        private NamedPipeClientStream _pipe;
        private Thread _readThread;
        private volatile bool _running;

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
                _pipe.Connect(timeoutMs);
                _running = true;
                _readThread = new Thread(ReadLoop);
                _readThread.IsBackground = true;
                _readThread.Start();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Disconnect()
        {
            _running = false;
            if (_pipe != null && _pipe.IsConnected)
            {
                _pipe.Dispose();
            }
            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        }

        public void Send(PipeMessage message)
        {
            if (_pipe == null || !_pipe.IsConnected)
                return;

            try
            {
                var data = message.ToByteArray();
                _pipe.Write(data, 0, data.Length);
                _pipe.Flush();
            }
            catch
            {
                OnDisconnected?.Invoke();
            }
        }

        private void ReadLoop()
        {
            var lengthBuffer = new byte[4];

            while (_running)
            {
                try
                {
                    var bytesRead = _pipe.Read(lengthBuffer, 0, 4);
                    if (bytesRead < 4)
                    {
                        OnDisconnected?.Invoke();
                        break;
                    }

                    var length = BitConverter.ToInt32(lengthBuffer, 0);
                    var messageBuffer = new byte[length];
                    var totalRead = 0;

                    while (totalRead < length)
                    {
                        bytesRead = _pipe.Read(messageBuffer, totalRead, length - totalRead);
                        if (bytesRead == 0)
                        {
                            OnDisconnected?.Invoke();
                            return;
                        }
                        totalRead += bytesRead;
                    }

                    var message = PipeMessage.FromByteArray(messageBuffer);
                    OnMessageReceived?.Invoke(message);
                }
                catch
                {
                    OnDisconnected?.Invoke();
                    break;
                }
            }
        }
    }
}
