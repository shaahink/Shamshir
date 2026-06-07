using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace TradingEngine.Adapters.CTrader;

public class PipeClient
{
    private const int MaxRetries = 3;
    private static readonly int[] RetryDelays = new[] { 2000, 4000, 8000 };

    private readonly string _pipeName;
    private NamedPipeClientStream _pipe;
    private volatile bool _running;
    private string? _lastConnectError;

    public event Action<PipeMessage>? OnMessageReceived;
    public event Action? OnDisconnected;
    public event Action? OnReconnected;

    public string? LastConnectError => _lastConnectError;

    public PipeClient(string pipeName = "trading-engine")
    {
        _pipeName = pipeName;
        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    }

    public bool IsConnected => _pipe?.IsConnected ?? false;
    public bool IsRunning => _running;

    public bool Connect(int timeoutMs = 5000)
    {
        try
        {
            _pipe?.Dispose();
            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            _pipe.Connect(timeoutMs);
            _running = true;
            _lastConnectError = null;
            return true;
        }
        catch (Exception ex)
        {
            _lastConnectError = $"{ex.GetType().Name}: {ex.Message} | Win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
            return false;
        }
    }

    public void RetryConnect()
    {
        Console.Error.WriteLine($"PIPE_DIAG|RETRY_START|pipe={_pipeName}|maxRetries={MaxRetries}");
        for (var i = 0; i < MaxRetries; i++)
        {
            Thread.Sleep(RetryDelays[i]);
            if (Connect(5000))
            {
                Console.Error.WriteLine($"PIPE_DIAG|RETRY_SUCCESS|pipe={_pipeName}|attempt={i+1}");
                OnReconnected?.Invoke();
                return;
            }
            Console.Error.WriteLine($"PIPE_DIAG|RETRY_FAILED|pipe={_pipeName}|attempt={i+1}|error={_lastConnectError}");
        }
        Console.Error.WriteLine($"PIPE_DIAG|GIVE_UP|pipe={_pipeName}|allAttemptsFailed");
    }

    public void Disconnect()
    {
        _running = false;
        if (_pipe is not null && _pipe.IsConnected)
        {
            try { _pipe.Dispose(); } catch { }
        }
    }

    public void Send(PipeMessage message)
    {
        if (_pipe is null || !_pipe.IsConnected) return;

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

    public bool ReadMessage()
    {
        try
        {
            var lengthBuffer = new byte[4];
            var bytesRead = _pipe.Read(lengthBuffer, 0, 4);
            if (bytesRead < 4)
            {
                OnDisconnected?.Invoke();
                return false;
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
                    return false;
                }
                totalRead += bytesRead;
            }

            var json = System.Text.Encoding.UTF8.GetString(messageBuffer, 0, totalRead);
            var message = PipeMessage.FromJson(json);
            OnMessageReceived?.Invoke(message);
            return true;
        }
        catch
        {
            OnDisconnected?.Invoke();
            return false;
        }
    }
}
