# 拡張プロトコル（互換あり）

先頭1バイトが**コマンド**なら新プロトコル、そうでなければ**従来UPLOAD**として扱う方針はそのままです。

* `0x01` = `UPLOAD`（従来通り）
* `0x02` = `DOWNLOAD`
* `0x03` = `LIST`（一覧：サーバーディレクトリのファイルを返す）
* `0x04` = `STAT`（メタデータ：存在有無・サイズ・更新時刻を返す）
* `0x05` = `PING`（ヘルスチェック：疎通確認）

## 各コマンドのI/O

### PING（ヘルスチェック）

* C → S: `[0x05]`
* S → C: `"PONG\n"`（テキスト）

### LIST（一覧）

* C → S: `[0x03]`
* S → C:

  ```
  [int32] count
  （count 回繰り返し）
    [int32] nameLen
    [bytes] name (UTF-8)
    [int64] size
    [int64] mtimeUnixSeconds
  ```

### STAT（メタデータ照会）

* C → S:

  ```
  [0x04]
  [int32] nameLen
  [bytes]  name
  ```
* S → C:

  ```
  [byte]  exists (0/1)
  if exists==1:
     [int64] size
     [int64] mtimeUnixSeconds
  ```

### DOWNLOAD（既出）

* C → S:

  ```
  [0x02]
  [int32] nameLen
  [bytes]  name
  ```
* S → C:

  ```
  [int64] bodyLen  （存在しない場合は -1）
  [bytes] body (bodyLen 分)
  ```

### UPLOAD（既出）

* C → S:

  ```
  [0x01]
  [int32] nameLen
  [bytes]  name
  [int64] bodyLen
  [bytes]  body
  ```
* S → C: `"OK\n"`

---

# サーバー（TcpListener/NetworkStream版・LIST/STAT/PING 追加）

> 既存のアップロード専用サーバーに**追記**する形です。保存先は作業ディレクトリ直下（必要なら固定ディレクトリに変更してください）。

```csharp
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
```

---

# クライアント・ユーティリティ（手動確認／条件付きDL／ポーリング／待機）

> 既存のアップロード・ダウンロードに加え、`PING`・`LIST`・`STAT`・`WaitForServer`・`DownloadIfNewer` を追加した例です。

```csharp
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
```

---

# よくある使い方レシピ

* **サーバーが起動するまで待機してから開始**

  ```csharp
  if (!await WaitForServerAsync(TimeSpan.FromSeconds(20)))
  {
      Console.WriteLine("server not ready");
      return;
  }
  ```

* **手動で存在確認して、あれば取得**

  ```csharp
  var st = await StatAsync("report.pdf");
  if (st.Exists)
      await DownloadAsync("report.pdf", "./report.pdf");
  ```

* **リスト表示**

  ```csharp
  var items = await ListAsync();
  foreach (var (name, size, mtime) in items)
      Console.WriteLine($"{mtime} {size,8}  {name}");
  ```

* **定期的に更新されていればダウンロード**

  ```csharp
  using var cts = new CancellationTokenSource();
  Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
  await PollDownloadAsync("data.json", "./data.json", TimeSpan.FromSeconds(10), cts.Token);
  ```

---

# 補足（実運用TIPS）

* **保存先固定**＆**ファイル名サニタイズ**は必須（上のサンプルに簡易版あり）。
* **LISTの範囲**（サブディレクトリ含めるか、拡張子フィルタ等）は実情に合わせて。
* **時刻比較**はUTCで統一（上は `UnixTimeSeconds`）。
* セキュアにするなら `SslStream`（TLS）＋トークン認証をプロトコルに追加。

---

この形なら「手動確認」「更新があれば取得」「サーバーの起動待ち」が全部こなせます。
必要なら **Pipelines 版**でも同じコマンドを実装したサンプルを出します（大容量・高頻度の更新に強いです）。
