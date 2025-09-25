using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Buffers.Binary;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();
Console.WriteLine("listening on :5000 (UPLOAD/DOWNLOAD/LIST/STAT/PING)");

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

    var first = new byte[1];
    int n0 = await s.ReadAsync(first, ct);
    if (n0 == 0) return;
    byte b0 = first[0];

    switch (b0)
    {
        case 0x01: // UPLOAD
            await HandleUploadAsync(s, ct);
            break;

        case 0x02: // DOWNLOAD
            await HandleDownloadAsync(s, ct);
            break;

        case 0x03: // LIST
            await HandleListAsync(s, ct);
            break;

        case 0x04: // STAT
            await HandleStatAsync(s, ct);
            break;

        case 0x05: // PING
            await s.WriteAsync(Encoding.UTF8.GetBytes("PONG\n"), ct);
            break;

        default:
            // 旧クライアント互換：b0はnameLenの下位1バイトだった、という前提でUPLOADに流す
            await HandleLegacyUploadAsync(s, b0, ct);
            break;
    }
}

// ---------- UPLOAD ----------
static async Task HandleUploadAsync(NetworkStream s, CancellationToken ct)
{
    int nameLen = await ReadInt32LEAsync(s, ct);
    if (nameLen <= 0 || nameLen > 4096) throw new InvalidOperationException("bad name length");

    var nameBuf = new byte[nameLen];
    await ReadExactlyAsync(s, nameBuf, ct);
    var fileName = SanitizeFileName(Encoding.UTF8.GetString(nameBuf));

    long fileLen = await ReadInt64LEAsync(s, ct);
    if (fileLen < 0) throw new InvalidOperationException("bad file length");

    var destPath = Path.GetFullPath(fileName);
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

    Console.WriteLine($"[UPLOAD] {destPath} ({fileLen} bytes)");
    await s.WriteAsync(Encoding.UTF8.GetBytes("OK\n"), ct);
}

// ---------- DOWNLOAD ----------
static async Task HandleDownloadAsync(NetworkStream s, CancellationToken ct)
{
    int nameLen = await ReadInt32LEAsync(s, ct);
    if (nameLen <= 0 || nameLen > 4096) throw new InvalidOperationException("bad name length");

    var nameBuf = new byte[nameLen];
    await ReadExactlyAsync(s, nameBuf, ct);
    var fileName = SanitizeFileName(Encoding.UTF8.GetString(nameBuf));

    var srcPath = Path.GetFullPath(fileName);
    if (!File.Exists(srcPath))
    {
        await WriteInt64LEAsync(s, -1, ct);
        await s.FlushAsync(ct);
        Console.WriteLine($"[DOWNLOAD] not found: {srcPath}");
        return;
    }

    var fi = new FileInfo(srcPath);
    long bodyLen = fi.Length;

    await WriteInt64LEAsync(s, bodyLen, ct);
    await s.FlushAsync(ct);

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

// ---------- LIST ----------
static async Task HandleListAsync(NetworkStream s, CancellationToken ct)
{
    // カレントディレクトリ直下のファイルのみ（必要に応じて変更）
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    var files = dir.GetFiles("*", SearchOption.TopDirectoryOnly);

    await WriteInt32LEAsync(s, files.Length, ct);
    foreach (var f in files)
    {
        var nameBytes = Encoding.UTF8.GetBytes(f.Name);
        await WriteInt32LEAsync(s, nameBytes.Length, ct);
        await s.WriteAsync(nameBytes, ct);

        await WriteInt64LEAsync(s, f.Length, ct);
        long mtime = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeSeconds();
        await WriteInt64LEAsync(s, mtime, ct);
    }
    Console.WriteLine($"[LIST] {files.Length} item(s)");
}

// ---------- STAT ----------
static async Task HandleStatAsync(NetworkStream s, CancellationToken ct)
{
    int nameLen = await ReadInt32LEAsync(s, ct);
    if (nameLen <= 0 || nameLen > 4096) throw new InvalidOperationException("bad name length");

    var nameBuf = new byte[nameLen];
    await ReadExactlyAsync(s, nameBuf, ct);
    var fileName = SanitizeFileName(Encoding.UTF8.GetString(nameBuf));

    var path = Path.GetFullPath(fileName);
    if (!File.Exists(path))
    {
        await s.WriteAsync(new byte[] { 0x00 }, ct); // exists = 0
        Console.WriteLine($"[STAT] not found: {path}");
        return;
    }

    var fi = new FileInfo(path);
    await s.WriteAsync(new byte[] { 0x01 }, ct); // exists = 1
    await WriteInt64LEAsync(s, fi.Length, ct);
    long mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
    await WriteInt64LEAsync(s, mtime, ct);

    Console.WriteLine($"[STAT] {path} size={fi.Length} mtime={mtime}");
}

// ---------- レガシーUPLOAD（互換） ----------
static async Task HandleLegacyUploadAsync(NetworkStream s, byte firstLenByte, CancellationToken ct)
{
    var rest3 = new byte[3];
    await ReadExactlyAsync(s, rest3, ct);
    int nameLen = firstLenByte
                | (rest3[0] << 8)
                | (rest3[1] << 16)
                | (rest3[2] << 24);
    if (nameLen <= 0 || nameLen > 4096) throw new InvalidOperationException("bad name length");

    var nameBuf = new byte[nameLen];
    await ReadExactlyAsync(s, nameBuf, ct);
    var fileName = SanitizeFileName(Encoding.UTF8.GetString(nameBuf));

    var i64 = new byte[8];
    await ReadExactlyAsync(s, i64, ct);
    long fileLen = BitConverter.ToInt64(i64, 0);
    if (fileLen < 0) throw new InvalidOperationException("bad file length");

    var destPath = Path.GetFullPath(fileName);
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

    Console.WriteLine($"[UPLOAD-legacy] {destPath} ({fileLen} bytes)");
    await s.WriteAsync(Encoding.UTF8.GetBytes("OK\n"), ct);
}

// ---------- ヘルパ ----------
static string SanitizeFileName(string name)
{
    // 簡易：パス区切りと親ディレクトリ記号を除去
    foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
    name = name.Replace("/", "_").Replace("\\", "_").Replace("..", "_");
    return name;
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

static async Task<int> ReadInt32LEAsync(Stream s, CancellationToken ct)
{
    var buf = new byte[4];
    await ReadExactlyAsync(s, buf, ct);
    return BinaryPrimitives.ReadInt32LittleEndian(buf);
}

static async Task<long> ReadInt64LEAsync(Stream s, CancellationToken ct)
{
    var buf = new byte[8];
    await ReadExactlyAsync(s, buf, ct);
    return BinaryPrimitives.ReadInt64LittleEndian(buf);
}

static async Task WriteInt32LEAsync(Stream s, int value, CancellationToken ct)
{
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(buf, value);
    await s.WriteAsync(buf.ToArray(), ct);
}

static async Task WriteInt64LEAsync(Stream s, long value, CancellationToken ct)
{
    Span<byte> buf = stackalloc byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(buf, value);
    await s.WriteAsync(buf.ToArray(), ct);
}
