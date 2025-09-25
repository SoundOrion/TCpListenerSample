using System.Net.Sockets;
using System.Buffers.Binary;
using System.Text;

const string Host = "127.0.0.1";
const int Port = 5000;

static async Task<bool> WaitForServerAsync(TimeSpan timeout, TimeSpan? interval = null)
{
    interval ??= TimeSpan.FromSeconds(1);
    var sw = System.Diagnostics.Stopwatch.StartNew();

    while (sw.Elapsed < timeout)
    {
        try
        {
            using var tcp = new TcpClient();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync(Host, Port, cts.Token);
            using var s = tcp.GetStream();

            await s.WriteAsync(new byte[] { 0x05 }); // PING
            var buf = new byte[5];
            int n = await s.ReadAsync(buf);
            if (n > 0 && Encoding.UTF8.GetString(buf, 0, n).StartsWith("PONG"))
                return true;
        }
        catch { /* retry */ }

        await Task.Delay(interval.Value);
    }
    return false;
}

static async Task<List<(string Name, long Size, long Mtime)>> ListAsync()
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(Host, Port);
    using var s = tcp.GetStream();

    await s.WriteAsync(new byte[] { 0x03 }); // LIST

    var count = await ReadInt32LEAsync(s);
    var result = new List<(string, long, long)>(count);

    for (int i = 0; i < count; i++)
    {
        int nameLen = await ReadInt32LEAsync(s);
        var name = await ReadUtf8Async(s, nameLen);

        long size = await ReadInt64LEAsync(s);
        long mtime = await ReadInt64LEAsync(s);

        result.Add((name, size, mtime));
    }
    return result;
}

static async Task<(bool Exists, long Size, long Mtime)> StatAsync(string remoteName)
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(Host, Port);
    using var s = tcp.GetStream();

    await s.WriteAsync(new byte[] { 0x04 }); // STAT
    var nameBytes = Encoding.UTF8.GetBytes(remoteName);
    await WriteInt32LEAsync(s, nameBytes.Length);
    await s.WriteAsync(nameBytes);

    int exists = s.ReadByte();
    if (exists != 1) return (false, 0, 0);

    long size = await ReadInt64LEAsync(s);
    long mtime = await ReadInt64LEAsync(s);
    return (true, size, mtime);
}

static async Task DownloadAsync(string remoteName, string saveAs)
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(Host, Port);
    using var s = tcp.GetStream();

    await s.WriteAsync(new byte[] { 0x02 }); // DOWNLOAD
    var nameBytes = Encoding.UTF8.GetBytes(remoteName);
    await WriteInt32LEAsync(s, nameBytes.Length);
    await s.WriteAsync(nameBytes);

    long bodyLen = await ReadInt64LEAsync(s);
    if (bodyLen < 0) throw new FileNotFoundException(remoteName);

    await using var fs = File.Create(saveAs);
    long remaining = bodyLen;
    var buf = new byte[81920];
    while (remaining > 0)
    {
        int toRead = (int)Math.Min(buf.Length, remaining);
        int n = await s.ReadAsync(buf.AsMemory(0, toRead));
        if (n == 0) throw new IOException("unexpected EOF");
        await fs.WriteAsync(buf.AsMemory(0, n));
        remaining -= n;
    }
}

static async Task UploadAsync(string localPath)
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(Host, Port);
    using var s = tcp.GetStream();

    await s.WriteAsync(new byte[] { 0x01 }); // UPLOAD
    var fileName = Path.GetFileName(localPath);
    var nameBytes = Encoding.UTF8.GetBytes(fileName);
    await WriteInt32LEAsync(s, nameBytes.Length);
    await s.WriteAsync(nameBytes);

    long len = new FileInfo(localPath).Length;
    await WriteInt64LEAsync(s, len);

    await using var fs = File.OpenRead(localPath);
    var buf = new byte[81920];
    int n;
    while ((n = await fs.ReadAsync(buf)) > 0)
        await s.WriteAsync(buf.AsMemory(0, n));

    var ack = new byte[256];
    int ackN = await s.ReadAsync(ack);
    Console.WriteLine(Encoding.UTF8.GetString(ack, 0, ackN).Trim());
}

// 条件付きダウンロード（ローカルよりサーバーが新しければ取得）
static async Task<bool> DownloadIfNewerAsync(string remoteName, string localPath)
{
    var stat = await StatAsync(remoteName);
    if (!stat.Exists) return false;

    long localMtime = File.Exists(localPath)
        ? new DateTimeOffset(File.GetLastWriteTimeUtc(localPath)).ToUnixTimeSeconds()
        : 0;

    if (stat.Mtime > localMtime)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localPath))!);
        await DownloadAsync(remoteName, localPath);
        // ローカルの mtime をサーバーに合わせる（任意）
        File.SetLastWriteTimeUtc(localPath, DateTimeOffset.FromUnixTimeSeconds(stat.Mtime).UtcDateTime);
        return true;
    }
    return false;
}

// シンプルなポーリング（一定間隔で更新チェック→必要ならDL）
static async Task PollDownloadAsync(string remoteName, string localPath, TimeSpan interval, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            bool updated = await DownloadIfNewerAsync(remoteName, localPath);
            if (updated) Console.WriteLine($"updated: {localPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"poll error: {ex.Message}");
        }
        await Task.Delay(interval, ct);
    }
}

// ---- I/O ヘルパ ----
static async Task<string> ReadUtf8Async(NetworkStream s, int len)
{
    var buf = new byte[len];
    int off = 0;
    while (off < len)
    {
        int n = await s.ReadAsync(buf.AsMemory(off));
        if (n == 0) throw new IOException("unexpected EOF");
        off += n;
    }
    return Encoding.UTF8.GetString(buf);
}

static async Task<int> ReadInt32LEAsync(NetworkStream s)
{
    var buf = new byte[4];
    int off = 0;
    while (off < 4)
    {
        int n = await s.ReadAsync(buf.AsMemory(off));
        if (n == 0) throw new IOException("unexpected EOF");
        off += n;
    }
    return BinaryPrimitives.ReadInt32LittleEndian(buf);
}

static async Task<long> ReadInt64LEAsync(NetworkStream s)
{
    var buf = new byte[8];
    int off = 0;
    while (off < 8)
    {
        int n = await s.ReadAsync(buf.AsMemory(off));
        if (n == 0) throw new IOException("unexpected EOF");
        off += n;
    }
    return BinaryPrimitives.ReadInt64LittleEndian(buf);
}

static async Task WriteInt32LEAsync(NetworkStream s, int value)
{
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(buf, value);
    await s.WriteAsync(buf.ToArray());
}
