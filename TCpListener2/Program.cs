using System.Net;
using System.Net.Sockets;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
listenSocket.Bind(new IPEndPoint(IPAddress.Any, 5000));
listenSocket.Listen(backlog: 512);

Console.WriteLine("listening on :5000 (Socket)");

try
{
    while (!cts.IsCancellationRequested)
    {
        var socket = await listenSocket.AcceptAsync(cts.Token); // CT対応の新API
        _ = HandleConnectionAsync(socket, cts.Token); // fire-and-forget（Task.Run不使用）
    }
}
catch (OperationCanceledException) { /* shutting down */ }
finally
{
    try { listenSocket.Close(); } catch { /* ignore */ }
}

static async Task HandleConnectionAsync(Socket socket, CancellationToken ct)
{
    using var s = socket;

    var buffer = new byte[8192]; // 適宜ArrayPoolで最適化可
    while (!ct.IsCancellationRequested)
    {
        int n = await s.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, ct);
        if (n <= 0) break;
        await s.SendAsync(buffer.AsMemory(0, n), SocketFlags.None, ct);
    }

    // 半クローズや linger が必要ならここで制御
}
