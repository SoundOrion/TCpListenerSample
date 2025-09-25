// File: FileServerPipelines.csproj の Program.cs など
// <TargetFramework>net8.0</TargetFramework>

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
Console.WriteLine("listening on :5000 (Pipelines, UPLOAD/DOWNLOAD)");

try
{
    while (!cts.IsCancellationRequested)
    {
        var client = await listener.AcceptTcpClientAsync(cts.Token);
        _ = ServeAsync(client, cts.Token);
    }
}
catch (OperationCanceledException) { }
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
            // 1) コマンド
            if (!await TryReadByteAsync(reader, out var cmd, ct)) break;

            // 2) ファイル名長
            int nameLen = await ReadInt32LEAsync(reader, ct);
            if (nameLen <= 0 || nameLen > 4096) throw new InvalidOperationException("bad name length");

            // 3) ファイル名
            var nameBytes = await ReadExactlyToArrayAsync(reader, nameLen, ct);
            var fileName = Encoding.UTF8.GetString(nameBytes);

            switch (cmd)
            {
                case 0x01: // UPLOAD
                    {
                        long bodyLen = await ReadInt64LEAsync(reader, ct);
                        if (bodyLen < 0) throw new InvalidOperationException("bad file length");

                        var destPath = Path.GetFullPath(fileName); // 実運用は保存先ディレクトリ固定＋検証推奨
                        await using var fs = File.Create(destPath);

                        // PipeReader から FileStream へストリームコピー（ゼロコピー近似）
                        await CopyFromPipeToStreamAsync(reader, fs, bodyLen, ct);

                        Console.WriteLine($"[UPLOAD] saved {destPath} ({bodyLen} bytes)");

                        // ACK
                        await writer.WriteAsync(Encoding.UTF8.GetBytes("OK\n"), ct);
                        await writer.FlushAsync(ct);
                        break;
                    }
                case 0x02: // DOWNLOAD
                    {
                        var srcPath = Path.GetFullPath(fileName);
                        if (!File.Exists(srcPath))
                        {
                            await writer.WriteAsync(Encoding.UTF8.GetBytes("ERR:not found\n"), ct);
                            await writer.FlushAsync(ct);
                            break;
                        }

                        var fi = new FileInfo(srcPath);
                        long bodyLen = fi.Length;

                        // 本体長を先に送る
                        WriteInt64LE(writer, bodyLen);
                        await writer.FlushAsync(ct);

                        await using var fs = File.OpenRead(srcPath);
                        await CopyFromStreamToPipeAsync(fs, writer, bodyLen, ct);

                        Console.WriteLine($"[DOWNLOAD] sent {srcPath} ({bodyLen} bytes)");
                        break;
                    }
                default:
                    throw new InvalidOperationException($"unknown cmd: {cmd}");
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

// ======================== Pipelines Helpers ========================

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

static async ValueTask<bool> TryReadByteAsync(PipeReader reader, out byte b, CancellationToken ct)
{
    while (true)
    {
        var result = await reader.ReadAsync(ct);
        var buf = result.Buffer;

        if (buf.Length > 0)
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
    var resultBytes = new byte[count];
    int copied = 0;

    while (copied < count)
    {
        var result = await reader.ReadAsync(ct);
        var buf = result.Buffer;
        if (buf.Length == 0 && result.IsCompleted) throw new IOException("unexpected EOF (blob)");

        var toTake = (int)Math.Min(buf.Length, count - copied);
        buf = buf.Slice(0, toTake);

        foreach (var segment in buf)
        {
            segment.CopyTo(resultBytes.AsSpan(copied));
            copied += segment.Length;
        }
        reader.AdvanceTo(buf.End);
    }
    return resultBytes;
}

// PipeReader -> Stream（既知の bodyLen 分だけをコピー）
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

// Stream -> PipeWriter（既知の bodyLen を送る）
static async ValueTask CopyFromStreamToPipeAsync(Stream src, PipeWriter writer, long bodyLen, CancellationToken ct)
{
    long remaining = bodyLen;

    while (remaining > 0)
    {
        var mem = writer.GetMemory((int)Math.Min(65536, remaining));
        int read = await src.ReadAsync(mem, ct);
        if (read <= 0) throw new IOException("unexpected EOF from file");

        writer.Advance(read);
        remaining -= read;

        // ある程度溜めてからでも良いが、単純化のため毎回 Flush
        var res = await writer.FlushAsync(ct);
        if (res.IsCompleted || res.IsCanceled) break;
    }
}
