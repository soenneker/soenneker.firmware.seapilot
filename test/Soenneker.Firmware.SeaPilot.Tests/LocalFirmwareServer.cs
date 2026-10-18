using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Firmware.SeaPilot.Tests;

internal sealed class LocalFirmwareServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public string Url { get; }
    public int Downloads { get; private set; }
    public LocalFirmwareServer()
    {
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/firmware-SN06.bin";
        _loop = Run();
    }
    private async Task Run()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string? request = await reader.ReadLineAsync(_stop.Token);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                byte[] body = [1, 2, 3, 4];
                string headers = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=firmware-SN06.bin\r\nContent-Length: 4\r\nETag: \"version-one\"\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _stop.Token);
                if (request?.StartsWith("HEAD ") != true) { Downloads++; await stream.WriteAsync(body, _stop.Token); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _loop;
        _stop.Dispose();
    }
}
