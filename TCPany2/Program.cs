// File: Program.cs  (.NET 8)
// dotnet run で :5000 を待受け
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
using System.Text;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();
Console.WriteLine("listening on :5000 (Pipelines; UPLOAD/DOWNLOAD/LIST/STAT/PING + legacy)");

try
{
    while (!cts.IsCancellationRequested)
    {
        var client = await listener.AcceptTcpClientAsync(cts.Token);
        _ = ServeAsync(client, cts.Token);
    }
}
catch (OperationCanceledException) { /* shutting down */ }
finally { listener.Stop(); }

static async Task ServeAsync(TcpClient client, CancellationToken ct)
{
    using var _ = client;
    using var stream = client.GetStream();

    var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
    var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));

    try
    {
        while (!ct.IsCancellationRequested)
        {
            // 先頭1バイト（コマンド or レガシーの nameLen 下位バイト）
            if (!await TryReadByteAsync(reader, out var first, ct))
                break;

            switch (first)
            {
                case 0x01: // UPLOAD
                    await HandleUploadAsync(reader, writer, ct);
                    break;

                case 0x02: // DOWNLOAD
                    await HandleDownloadAsync(reader, writer, ct);
                    break;

                case 0x03: // LIST
                    await HandleListAsync(writer, ct);
                    break;

                case 0x04: // STAT
                    await HandleStatAsync(reader, writer, ct);
                    break;

                case 0x05: // PING
                    WriteUtf8(writer, "PONG\n");
                    await writer.FlushAsync(ct);
                    break;

                default:
                    // レガシー互換: first は nameLen の下位1バイト
                    await HandleLegacyUploadAsync(reader, writer, first, ct);
                    break;
            }
        }
    }
    catch (OperationCanceledException) { /* shutting down */ }
    finally
    {
        await reader.CompleteAsync();
        await writer.CompleteAsync();
    }
}

// ========================= Command Handlers =========================

static async Task HandleUploadAsync(PipeReader reader, PipeWriter writer, CancellationToken ct)
{
    int nameLen = await ReadInt32LEAsync(reader, ct);
    if (nameLen <= 0 || nameLen > 4096) throw new InvalidOperationException("bad name length");

    var nameBytes = await ReadExactlyToArrayAsync(reader, nameLen, ct);
    var fileName = SanitizeFileName(Encoding.UTF8.GetString(nameBytes));

    long bodyLen = await ReadInt64LEAsync(reader, ct);
    if (bodyLen < 0) throw new InvalidOperationException("bad file length");

    var destPath = Path.GetFullPath(fileName);
    await using var fs = File.Create(destPath);

    await CopyFromPipeToStreamAsync(reader, fs, bodyLen, ct);

    Console.WriteLine($"[UPLOAD] {destPath} ({bodyLen} bytes)");
    WriteUtf8(writer, "OK\n");
    await writer.FlushAsync(ct);
}

static async Task HandleDownloadAsync(PipeReader reader, PipeWriter writer, CancellationToken ct)
{
    int nameLen = await ReadInt32LEAsync(reader, ct);
    if (nameLen <= 0 || nameLen > 4096) throw new InvalidOperationException("bad name length");

    var nameBytes = await ReadExactlyToArrayAsync(reader, nameLen, ct);
    var fileName = SanitizeFileName(Encoding.UTF8.GetString(nameBytes));

    var srcPath = Path.GetFullPath(fileName);
    if (!File.Exists(srcPath))
    {
        WriteInt64LE(writer, -1);
        await writer.FlushAsync(ct);
        Console.WriteLine($"[DOWNLOAD] not found: {srcPath}");
        return;
    }

    var fi = new FileInfo(srcPath);
    long bodyLen = fi.Length;

    WriteInt64LE(writer, bodyLen);
    await writer.FlushAsync(ct);

    await using var fs = File.OpenRead(srcPath);
    await CopyFromStreamToPipeAsync(fs, writer, bodyLen, ct);

    Console.WriteLine($"[DOWNLOAD] sent: {srcPath} ({bodyLen} bytes)");
}

static async Task HandleListAsync(PipeWriter writer, CancellationToken ct)
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    var files = dir.GetFiles("*", SearchOption.TopDirectoryOnly);

    WriteInt32LE(writer, files.Length);
    foreach (var f in files)
    {
        var nameBytes = Encoding.UTF8.GetBytes(f.Name);
        WriteInt32LE(writer, nameBytes.Length);
        WriteBytes(writer, nameBytes);

        WriteInt64LE(writer, f.Length);
        long mtime = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeSeconds();
        WriteInt64LE(writer, mtime);
    }
    await writer.FlushAsync(ct);
    Console.WriteLine($"[LIST] {files.Length} item(s)");
}

static async Task HandleStatAsync(PipeReader reader, PipeWriter writer, CancellationToken ct)
{
    int nameLen = await ReadInt32LEAsync(reader, ct);
    if (nameLen <= 0 || nameLen > 4096) throw new InvalidOperationException("bad name length");

    var nameBytes = await ReadExactlyToArrayAsync(reader, nameLen, ct);
    var fileName = SanitizeFileName(Encoding.UTF8.GetString(nameBytes));

    var path = Path.GetFullPath(fileName);
    if (!File.Exists(path))
    {
        WriteByte(writer, 0x00); // exists = 0
        await writer.FlushAsync(ct);
        Console.WriteLine($"[STAT] not found: {path}");
        return;
    }

    var fi = new FileInfo(path);
    WriteByte(writer, 0x01); // exists = 1
    WriteInt64LE(writer, fi.Length);
    long mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
    WriteInt64LE(writer, mtime);
    await writer.FlushAsync(ct);

    Console.WriteLine($"[STAT] {path} size={fi.Length} mtime={mtime}");
}

static async Task HandleLegacyUploadAsync(PipeReader reader, PipeWriter writer, byte firstLenByte, CancellationToken ct)
{
    // nameLen 残り3バイトを読む
    var rest3 = await ReadExactlyToArrayAsync(reader, 3, ct);
    int nameLen = firstLenByte
                | (rest3[0] << 8)
                | (rest3[1] << 16)
                | (rest3[2] << 24);
    if (nameLen <= 0 || nameLen > 4096) throw new InvalidOperationException("bad name length");

    var nameBytes = await ReadExactlyToArrayAsync(reader, nameLen, ct);
    var fileName = SanitizeFileName(Encoding.UTF8.GetString(nameBytes));

    long bodyLen = await ReadInt64LEAsync(reader, ct);
    if (bodyLen < 0) throw new InvalidOperationException("bad file length");

    var destPath = Path.GetFullPath(fileName);
    await using var fs = File.Create(destPath);
    await CopyFromPipeToStreamAsync(reader, fs, bodyLen, ct);

    Console.WriteLine($"[UPLOAD-legacy] {destPath} ({bodyLen} bytes)");
    WriteUtf8(writer, "OK\n");
    await writer.FlushAsync(ct);
}

// ========================= Pipe Helpers =========================

static async ValueTask<bool> TryReadByteAsync(PipeReader reader, out byte b, CancellationToken ct)
{
    while (true)
    {
        var result = await reader.ReadAsync(ct);
        var buf = result.Buffer;

        if (!buf.IsEmpty)
        {
            b = buf.FirstSpan[0];
            reader.AdvanceTo(buf.GetPosition(1));
            return true;
        }
        if (result.IsCompleted)
        {
            b = default;
            reader.AdvanceTo(buf.End);
            return false;
        }
        reader.AdvanceTo(buf.Start, buf.End);
    }
}

static async ValueTask<int> ReadInt32LEAsync(PipeReader reader, CancellationToken ct)
{
    while (true)
    {
        var result = await reader.ReadAsync(ct);
        var buf = result.Buffer;
        if (buf.Length >= 4)
        {
            int v = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(0, 4).FirstSpan);
            reader.AdvanceTo(buf.GetPosition(4));
            return v;
        }
        if (result.IsCompleted) throw new IOException("unexpected EOF (int32)");
        reader.AdvanceTo(buf.Start, buf.End);
    }
}

static async ValueTask<long> ReadInt64LEAsync(PipeReader reader, CancellationToken ct)
{
    while (true)
    {
        var result = await reader.ReadAsync(ct);
        var buf = result.Buffer;
        if (buf.Length >= 8)
        {
            long v = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(0, 8).FirstSpan);
            reader.AdvanceTo(buf.GetPosition(8));
            return v;
        }
        if (result.IsCompleted) throw new IOException("unexpected EOF (int64)");
        reader.AdvanceTo(buf.Start, buf.End);
    }
}

static async ValueTask<byte[]> ReadExactlyToArrayAsync(PipeReader reader, int count, CancellationToken ct)
{
    var dst = new byte[count];
    int copied = 0;

    while (copied < count)
    {
        var result = await reader.ReadAsync(ct);
        var buf = result.Buffer;
        if (buf.Length == 0 && result.IsCompleted) throw new IOException("unexpected EOF (blob)");

        var toTake = (int)Math.Min(buf.Length, (long)count - copied);
        var slice = buf.Slice(0, toTake);

        foreach (var seg in slice)
        {
            seg.CopyTo(dst.AsSpan(copied));
            copied += seg.Length;
        }
        reader.AdvanceTo(slice.End);
    }
    return dst;
}

static async ValueTask CopyFromPipeToStreamAsync(PipeReader reader, Stream dest, long bodyLen, CancellationToken ct)
{
    long remaining = bodyLen;
    while (remaining > 0)
    {
        var result = await reader.ReadAsync(ct);
        var buf = result.Buffer;
        if (buf.Length == 0 && result.IsCompleted) throw new IOException("unexpected EOF (body)");

        var toTake = Math.Min(remaining, (long)buf.Length);
        var slice = buf.Slice(0, toTake);

        foreach (var seg in slice)
            await dest.WriteAsync(seg, ct);

        reader.AdvanceTo(slice.End);
        remaining -= toTake;
    }
}

static async ValueTask CopyFromStreamToPipeAsync(Stream src, PipeWriter writer, long bodyLen, CancellationToken ct)
{
    long remaining = bodyLen;
    while (remaining > 0)
    {
        var mem = writer.GetMemory((int)Math.Min(65536, remaining));
        int n = await src.ReadAsync(mem, ct);
        if (n <= 0) throw new IOException("unexpected EOF from file");

        writer.Advance(n);
        remaining -= n;

        var res = await writer.FlushAsync(ct);
        if (res.IsCompleted || res.IsCanceled) break;
    }
}

static void WriteByte(PipeWriter writer, byte value)
{
    var span = writer.GetSpan(1);
    span[0] = value;
    writer.Advance(1);
}

static void WriteBytes(PipeWriter writer, ReadOnlySpan<byte> data)
{
    var mem = writer.GetMemory(data.Length);
    data.CopyTo(mem.Span);
    writer.Advance(data.Length);
}

static void WriteUtf8(PipeWriter writer, string s)
    => WriteBytes(writer, Encoding.UTF8.GetBytes(s));

static void WriteInt32LE(PipeWriter writer, int value)
{
    var span = writer.GetSpan(4);
    BinaryPrimitives.WriteInt32LittleEndian(span, value);
    writer.Advance(4);
}

static void WriteInt64LE(PipeWriter writer, long value)
{
    var span = writer.GetSpan(8);
    BinaryPrimitives.WriteInt64LittleEndian(span, value);
    writer.Advance(8);
}

static string SanitizeFileName(string name)
{
    foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
    name = name.Replace("/", "_").Replace("\\", "_").Replace("..", "_");
    return name;
}
