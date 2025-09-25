using System.Net;
using System.Net.Sockets;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();
Console.WriteLine("listening on :5000 (TcpListener)");

try
{
    while (!cts.IsCancellationRequested)
    {
        var client = await listener.AcceptTcpClientAsync(cts.Token);
        _ = HandleClientAsync(client, cts.Token); // fire-and-forget（Task.Run不使用）
    }
}
catch (OperationCanceledException) { /* shutting down */ }
finally
{
    listener.Stop();
}

static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
{
    using var _ = client;
    using var stream = client.GetStream();

    var buffer = new byte[4096];
    while (!ct.IsCancellationRequested)
    {
        var n = await stream.ReadAsync(buffer.AsMemory(), ct);
        if (n == 0) break; // peer closed
        await stream.WriteAsync(buffer.AsMemory(0, n), ct); // echo back
    }
}
