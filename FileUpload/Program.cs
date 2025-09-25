using System.Net;
using System.Net.Sockets;
using System.Text;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();
Console.WriteLine("listening on :5000 for file upload");

try
{
    while (!cts.IsCancellationRequested)
    {
        var client = await listener.AcceptTcpClientAsync(cts.Token);
        _ = HandleClientAsync(client, cts.Token);
    }
}
catch (OperationCanceledException) { }
finally { listener.Stop(); }

static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
{
    using var _ = client;
    using var s = client.GetStream();

    // 1) ファイル名長(int32)
    var i32 = new byte[4];
    await ReadExactlyAsync(s, i32, ct);
    int nameLen = BitConverter.ToInt32(i32, 0);
    if (nameLen <= 0 || nameLen > 1024) throw new InvalidOperationException("bad name length");

    // 2) ファイル名
    var nameBuf = new byte[nameLen];
    await ReadExactlyAsync(s, nameBuf, ct);
    var fileName = Encoding.UTF8.GetString(nameBuf);

    // 3) 本体長(int64)
    var i64 = new byte[8];
    await ReadExactlyAsync(s, i64, ct);
    long fileLen = BitConverter.ToInt64(i64, 0);
    if (fileLen < 0) throw new InvalidOperationException("bad file length");

    // 4) 本体を受信して保存
    var destPath = Path.GetFullPath(fileName); // 必要なら保存先ディレクトリを固定推奨
    await using var fs = File.Create(destPath);

    var buffer = new byte[81920]; // .NET既定に近いサイズ
    long remaining = fileLen;
    while (remaining > 0)
    {
        int toRead = (int)Math.Min(buffer.Length, remaining);
        int read = await s.ReadAsync(buffer.AsMemory(0, toRead), ct);
        if (read == 0) throw new IOException("unexpected EOF");
        await fs.WriteAsync(buffer.AsMemory(0, read), ct);
        remaining -= read;
    }

    Console.WriteLine($"saved: {destPath} ({fileLen} bytes)");

    // 簡単なACK（任意）
    var ack = Encoding.UTF8.GetBytes("OK\n");
    await s.WriteAsync(ack, ct);
}

static async Task ReadExactlyAsync(Stream s, byte[] buf, CancellationToken ct)
{
    int off = 0;
    while (off < buf.Length)
    {
        int n = await s.ReadAsync(buf.AsMemory(off), ct);
        if (n == 0) throw new IOException("unexpected EOF");
        off += n;
    }
}
