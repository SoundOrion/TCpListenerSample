// File: FileClient.csproj の Program.cs など
// <TargetFramework>net8.0</TargetFramework>

using System.Net.Sockets;
using System.Buffers.Binary;
using System.Text;

if (args.Length < 2)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  upload   <localPath>                 # サーバーにアップロード");
    Console.WriteLine("  download <remoteFileName> <saveAs?>  # サーバーからダウンロード");
    return;
}

var host = "127.0.0.1";
var port = 5000;

switch (args[0].ToLowerInvariant())
{
    case "upload":
        if (args.Length < 2) { Console.WriteLine("missing localPath"); return; }
        await UploadAsync(args[1]);
        break;

    case "download":
        if (args.Length < 2) { Console.WriteLine("missing remoteFileName"); return; }
        var saveAs = args.Length >= 3 ? args[2] : args[1];
        await DownloadAsync(args[1], saveAs);
        break;

    default:
        Console.WriteLine("unknown command");
        break;
}

async Task UploadAsync(string localPath)
{
    var fileName = Path.GetFileName(localPath);
    var fileLen = new FileInfo(localPath).Length;

    using var tcp = new TcpClient();
    await tcp.ConnectAsync(host, port);
    using var s = tcp.GetStream();

    // cmd
    await s.WriteAsync(new byte[] { 0x01 }); // UPLOAD
    // name
    var nameBytes = Encoding.UTF8.GetBytes(fileName);
    var span4 = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(span4.AsSpan(), nameBytes.Length);
    await s.WriteAsync(span4);
    await s.WriteAsync(nameBytes);
    // body len
    var span8 = new byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(span8.AsSpan(), fileLen);
    await s.WriteAsync(span8);
    // body
    await using (var fs = File.OpenRead(localPath))
    {
        var buf = new byte[81920];
        int n;
        while ((n = await fs.ReadAsync(buf)) > 0)
            await s.WriteAsync(buf.AsMemory(0, n));
    }

    // ACK
    var ack = new byte[256];
    int ackN = await s.ReadAsync(ack);
    Console.WriteLine(Encoding.UTF8.GetString(ack, 0, ackN).Trim());
}

async Task DownloadAsync(string remoteName, string saveAs)
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(host, port);
    using var s = tcp.GetStream();

    // cmd
    await s.WriteAsync(new byte[] { 0x02 }); // DOWNLOAD
    // name
    var nameBytes = Encoding.UTF8.GetBytes(remoteName);
    var span4 = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(span4.AsSpan(), nameBytes.Length);
    await s.WriteAsync(span4);
    await s.WriteAsync(nameBytes);

    // body len
    var span8 = await ReadExactlyAsync(s, 8);
    long bodyLen = BinaryPrimitives.ReadInt64LittleEndian(span8);

    // 本体
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
    Console.WriteLine($"downloaded: {saveAs} ({bodyLen} bytes)");
}

static async Task<byte[]> ReadExactlyAsync(NetworkStream s, int count)
{
    var buf = new byte[count];
    int off = 0;
    while (off < count)
    {
        int n = await s.ReadAsync(buf.AsMemory(off));
        if (n == 0) throw new IOException("unexpected EOF");
        off += n;
    }
    return buf;
}
