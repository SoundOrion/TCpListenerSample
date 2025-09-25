using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Buffers.Binary;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();
Console.WriteLine("listening on :5000 (UPLOAD/DOWNLOAD)");

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

    // --- 先頭1バイトを読み、コマンドかどうか判定 ---
    var first = new byte[1];
    int n0 = await s.ReadAsync(first, ct);
    if (n0 == 0) return; // 接続直後に切断
    byte b0 = first[0];

    if (b0 == 0x01 || b0 == 0x02)
    {
        // ===== 新プロトコル（コマンドあり） =====
        if (b0 == 0x01)
        {
            await HandleUploadAsync(s, ct);   // cmd=UPLOAD
        }
        else
        {
            await HandleDownloadAsync(s, ct); // cmd=DOWNLOAD
        }
    }
    else
    {
        // ===== 旧来互換：b0 を nameLen の下位1バイトとして扱い UPLOAD とみなす =====
        await HandleLegacyUploadAsync(s, b0, ct);
    }
}

// ========== UPLOAD (新プロトコル: cmd(1) + nameLen(4) + name(N) + bodyLen(8) + body(M)) ==========
static async Task HandleUploadAsync(NetworkStream s, CancellationToken ct)
{
    int nameLen = await ReadInt32LEAsync(s, ct);
    if (nameLen <= 0 || nameLen > 1024) throw new InvalidOperationException("bad name length");

    var nameBuf = new byte[nameLen];
    await ReadExactlyAsync(s, nameBuf, ct);
    var fileName = Encoding.UTF8.GetString(nameBuf);

    long fileLen = await ReadInt64LEAsync(s, ct);
    if (fileLen < 0) throw new InvalidOperationException("bad file length");

    var destPath = Path.GetFullPath(fileName); // 実運用はディレクトリ固定＆検証推奨
    await using var fs = File.Create(destPath);

    var buffer = new byte[81920];
    long remaining = fileLen;
    while (remaining > 0)
    {
        int toRead = (int)Math.Min(buffer.Length, remaining);
        int read = await s.ReadAsync(buffer.AsMemory(0, toRead), ct);
        if (read == 0) throw new IOException("unexpected EOF");
        await fs.WriteAsync(buffer.AsMemory(0, read), ct);
        remaining -= read;
    }

    Console.WriteLine($"[UPLOAD] saved: {destPath} ({fileLen} bytes)");

    // ACK（任意）
    var ack = Encoding.UTF8.GetBytes("OK\n");
    await s.WriteAsync(ack, ct);
}

// ========== DOWNLOAD (新プロトコル: cmd(1) + nameLen(4) + name(N) -> server sends bodyLen(8) + body) ==========
static async Task HandleDownloadAsync(NetworkStream s, CancellationToken ct)
{
    int nameLen = await ReadInt32LEAsync(s, ct);
    if (nameLen <= 0 || nameLen > 1024) throw new InvalidOperationException("bad name length");

    var nameBuf = new byte[nameLen];
    await ReadExactlyAsync(s, nameBuf, ct);
    var fileName = Encoding.UTF8.GetString(nameBuf);

    var srcPath = Path.GetFullPath(fileName);

    if (!File.Exists(srcPath))
    {
        // 見つからなければ bodyLen = -1 を返す
        await WriteInt64LEAsync(s, -1, ct);
        await s.FlushAsync(ct);
        Console.WriteLine($"[DOWNLOAD] not found: {srcPath}");
        return;
    }

    var fi = new FileInfo(srcPath);
    long bodyLen = fi.Length;

    // 先に本体長を返す
    await WriteInt64LEAsync(s, bodyLen, ct);
    await s.FlushAsync(ct);

    // 本体をストリーミング送信
    await using var fs = File.OpenRead(srcPath);
    var buffer = new byte[81920];
    int read;
    long remaining = bodyLen;
    while (remaining > 0 && (read = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct)) > 0)
    {
        await s.WriteAsync(buffer.AsMemory(0, read), ct);
        remaining -= read;
    }

    Console.WriteLine($"[DOWNLOAD] sent: {srcPath} ({bodyLen} bytes)");
}

// ========== レガシー互換 UPLOAD（先頭1バイトが nameLen の下位バイト） ==========
static async Task HandleLegacyUploadAsync(NetworkStream s, byte firstLenByte, CancellationToken ct)
{
    // すでに 1 バイト読んでいるので、残りの 3 バイトを読み足す
    var rest3 = new byte[3];
    await ReadExactlyAsync(s, rest3, ct);

    // nameLen（LE）
    int nameLen = firstLenByte
                | (rest3[0] << 8)
                | (rest3[1] << 16)
                | (rest3[2] << 24);

    if (nameLen <= 0 || nameLen > 1024) throw new InvalidOperationException("bad name length");

    // 以降は新UPLAODと同じ
    var nameBuf = new byte[nameLen];
    await ReadExactlyAsync(s, nameBuf, ct);
    var fileName = Encoding.UTF8.GetString(nameBuf);

    var i64 = new byte[8];
    await ReadExactlyAsync(s, i64, ct);
    long fileLen = BitConverter.ToInt64(i64, 0);
    if (fileLen < 0) throw new InvalidOperationException("bad file length");

    var destPath = Path.GetFullPath(fileName);
    await using var fs = File.Create(destPath);

    var buffer = new byte[81920];
    long remaining = fileLen;
