using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A loopback socket that answers one request. It either serves a complete body and closes,
/// or drips bytes indefinitely so a transfer can be interrupted part-way. Nothing leaves the
/// machine and no vendor is contacted.
/// </summary>
internal sealed class SlowLoopbackServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;
    private readonly TaskCompletionSource _sending =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Url { get; }

    public SlowLoopbackServer(string? completeBody = null, bool truncateResponse = false)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Url = $"http://127.0.0.1:{port}/payload.bin";

        _serving = Task.Run(() => ServeAsync(completeBody, truncateResponse));
    }

    /// <summary>Completes once the response body has started arriving.</summary>
    public Task WaitUntilSending() => _sending.Task.WaitAsync(TimeSpan.FromSeconds(20));

    // On a slow response this completes only when the client disconnects or the test
    // disposes the server. Call before disposal to observe this specific transfer ending.
    public Task WaitUntilTransferStops() => _serving.WaitAsync(TimeSpan.FromSeconds(5));

    private async Task ServeAsync(string? completeBody, bool truncateResponse)
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            using var stream = client.GetStream();

            var request = new byte[4096];
            if (await stream.ReadAsync(request, _stop.Token) == 0) return;

            if (completeBody is not null)
            {
                var body = Encoding.ASCII.GetBytes(completeBody);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length + (truncateResponse ? 100 : 0)}\r\nConnection: close\r\n\r\n"), _stop.Token);
                await stream.WriteAsync(body, _stop.Token);
                await stream.FlushAsync(_stop.Token);
                _sending.TrySetResult();
                return;
            }

            // A length far larger than anything that will be sent, so the transfer stays
            // open until the test interrupts it.
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 100000000\r\nConnection: close\r\n\r\n"), _stop.Token);

            var block = new byte[65536];
            while (!_stop.IsCancellationRequested)
            {
                await stream.WriteAsync(block, _stop.Token);
                await stream.FlushAsync(_stop.Token);
                _sending.TrySetResult();
                await Task.Delay(35, _stop.Token);
            }
        }
        catch (Exception)
        {
            // The test tears the connection down; nothing here is a failure.
        }
        finally
        {
            _sending.TrySetResult();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch (Exception) { }
        try { _serving.Wait(TimeSpan.FromSeconds(10)); } catch (Exception) { }
        _stop.Dispose();
    }
}
