using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();
Console.WriteLine("listening on :5000 (Pipelines)");

// 受け入れループ
try
{
    while (!cts.IsCancellationRequested)
    {
        var client = await listener.AcceptTcpClientAsync(cts.Token);
        _ = ServeAsync(client, cts.Token); // fire-and-forget（Task.Run不使用）
    }
}
catch (OperationCanceledException) { /* shutting down */ }
finally
{
    listener.Stop();
}

static async Task ServeAsync(TcpClient client, CancellationToken ct)
{
    using var _ = client;
    using var stream = client.GetStream();

    // StreamからPipeReader/Writerを生成
    var reader = PipeReader.Create(stream);
    var writer = PipeWriter.Create(stream);

    while (!ct.IsCancellationRequested)
    {
        var readResult = await reader.ReadAsync(ct);
        var buffer = readResult.Buffer;

        if (readResult.IsCanceled) break;

        while (TryReadFrame(ref buffer, out ReadOnlySequence<byte> frame))
        {
            // ここでフレーム単位の処理（例：そのままエコー）
            foreach (var segment in frame)
            {
                var dest = writer.GetMemory(segment.Length);
                segment.CopyTo(dest);
                writer.Advance(segment.Length);
            }
            var flush = await writer.FlushAsync(ct);
            if (flush.IsCanceled || flush.IsCompleted) { break; }
        }

        reader.AdvanceTo(buffer.Start, buffer.End);

        if (readResult.IsCompleted) break;
    }

    await reader.CompleteAsync();
    await writer.CompleteAsync();

    // 簡易的なフレーム切り出し例：
    static bool TryReadFrame(ref ReadOnlySequence<byte> input, out ReadOnlySequence<byte> frame)
    {
        // デモとして「改行(\n)で区切る」
        var pos = input.PositionOf((byte)'\n');
        if (pos == null)
        {
            frame = default;
            return false;
        }
        frame = input.Slice(0, input.GetPosition(1, pos.Value)); // 改行含めて返す
        input = input.Slice(frame.End);
        return true;
    }
}
