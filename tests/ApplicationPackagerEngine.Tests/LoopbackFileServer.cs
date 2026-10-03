using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A loopback socket that serves fixed bodies by path and keeps the head of
/// every request it receives. Nothing leaves the machine.
/// </summary>
internal sealed class LoopbackFileServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;
    private readonly IReadOnlyDictionary<string, byte[]> _files;
    private readonly IReadOnlyDictionary<string, (string StatusAndHeaders, byte[] Body)> _responses;

    public ConcurrentQueue<string> Requests { get; } = new();
    public string BaseUrl { get; }

    /// <param name="responses">Paths answered with a status line and header lines of their own, then the body.</param>
    public LoopbackFileServer(IReadOnlyDictionary<string, byte[]> files,
        IReadOnlyDictionary<string, (string StatusAndHeaders, byte[] Body)>? responses = null)
    {
        _files = files;
        _responses = responses ?? new Dictionary<string, (string, byte[])>();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _serving = Task.Run(ServeAsync);
    }

    public string Url(string path) => $"{BaseUrl}/{path.TrimStart('/')}";

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }

            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var head = new StringBuilder();
                    while (!head.ToString().Contains("\r\n\r\n"))
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read == 0) break;
                        head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }
                    var text = head.ToString();
                    Requests.Enqueue(text);

                    var path = text.Split(' ', 3) is { Length: >= 2 } parts ? parts[1].Split('?')[0].TrimStart('/') : "";
                    if (_responses.TryGetValue(path, out var custom))
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            $"{custom.StatusAndHeaders.TrimEnd()}\r\nContent-Length: {custom.Body.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                        await stream.WriteAsync(custom.Body, _stop.Token);
                    }
                    else if (_files.TryGetValue(path, out var body))
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                        await stream.WriteAsync(body, _stop.Token);
                    }
                    else
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                    }
                    await stream.FlushAsync(_stop.Token);
                }
                catch (Exception)
                {
                    // A client that hangs up early is not a test failure.
                }
            }
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
