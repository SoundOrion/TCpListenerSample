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


いいね、**サイズ上限**と**アトミック保存**を入れた改修を置いておきます。
ポイントは以下の2つだけです。

* 受信前に `bodyLen` をチェック（上限超なら即エラー）。受信途中でも**累積**を見て超えたら中断＆一時ファイル削除。
* まず**同じディレクトリ**に一時ファイルへ書き切り、`File.Move(temp, dest, overwrite: true)`（同一ボリューム内なら実質アトミック）で置き換え。失敗時は temp を削除。

> OS/ファイルシステムに依存しますが、**同一ボリューム**内の `rename`/`move` は一般にアトミックです。確実性を高めたい場合は Windows では `File.Replace`（バックアップ付き）、Linux/Unix では同一ディレクトリに `rename` が基本です。

---

# 1) TcpListener / NetworkStream 版：`UPLOAD` ハンドラ差し替え

```csharp
// 例: 先のサーバーの定数として
const long MaxUploadBytes = 1L * 1024 * 1024 * 1024; // 1 GiB 上限（適宜変更）

static async Task HandleUploadAsync(NetworkStream s, CancellationToken ct)
{
    int nameLen = await ReadInt32LEAsync(s, ct);
    if (nameLen <= 0 || nameLen > 4096) throw new InvalidOperationException("bad name length");

    var nameBuf = new byte[nameLen];
    await ReadExactlyAsync(s, nameBuf, ct);
    var fileName = SanitizeFileName(Encoding.UTF8.GetString(nameBuf));

    long bodyLen = await ReadInt64LEAsync(s, ct);
    if (bodyLen < 0) throw new InvalidOperationException("bad file length");
    if (bodyLen > MaxUploadBytes) throw new InvalidOperationException($"file too large (>{MaxUploadBytes} bytes)");

    var destPath = Path.GetFullPath(fileName);
    var dir = Path.GetDirectoryName(destPath)!;
    Directory.CreateDirectory(dir);

    // 同一ディレクトリに temp（同一ボリューム＝アトミック move が効く）
    var tempPath = Path.Combine(dir, $".{Path.GetFileName(destPath)}.{Guid.NewGuid():N}.tmp");

    long received = 0;
    try
    {
        // WriteThrough は任意。確実性重視なら true（速度は落ちる）。
        await using var fs = new FileStream(
            tempPath,
            new FileStreamOptions {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous /* | FileOptions.WriteThrough */
            });

        var buffer = new byte[81920];
        long remaining = bodyLen;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await s.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read == 0) throw new IOException("unexpected EOF");
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);

            remaining -= read;
            received += read;
            if (received > MaxUploadBytes) throw new InvalidOperationException("file too large (stream)");
        }

        await fs.FlushAsync(ct);        // バッファ flush
        // 可能ならディスクへも flush（Windows .NET 8 以降は Flush(flushToDisk: true) が使える）
        // fs.Flush(true);

        // 目的ファイルへアトミック置換（.NET 8 以降）
        File.Move(tempPath, destPath, overwrite: true);
        Console.WriteLine($"[UPLOAD] {destPath} ({received} bytes)");

        await s.WriteAsync(Encoding.UTF8.GetBytes("OK\n"), ct);
    }
    catch
    {
        // 失敗時は temp を片付ける
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }
        throw;
    }
}
```

> **レガシー互換 `HandleLegacyUploadAsync`** も同様の手順（`tempPath` に書いてから `Move`）に差し替えてください。受信ループ中に `received` を加算し、上限超で中断＆削除、にするのがコツです。

---

# 2) Pipelines 版：`UPLOAD`/レガシーUPLOAD の保存をアトミックに

`HandleUploadAsync` / `HandleLegacyUploadAsync` の「保存」部分だけを、**一時ファイル→`Move`** にします。`CopyFromPipeToStreamAsync` はそのまま使えます。

```csharp
const long MaxUploadBytes = 1L * 1024 * 1024 * 1024; // 1 GiB

static async Task HandleUploadAsync(PipeReader reader, PipeWriter writer, CancellationToken ct)
{
    int nameLen = await ReadInt32LEAsync(reader, ct);
    if (nameLen <= 0 || nameLen > 4096) throw new InvalidOperationException("bad name length");

    var nameBytes = await ReadExactlyToArrayAsync(reader, nameLen, ct);
    var fileName = SanitizeFileName(Encoding.UTF8.GetString(nameBytes));

    long bodyLen = await ReadInt64LEAsync(reader, ct);
    if (bodyLen < 0) throw new InvalidOperationException("bad file length");
    if (bodyLen > MaxUploadBytes) throw new InvalidOperationException($"file too large (>{MaxUploadBytes} bytes)");

    var destPath = Path.GetFullPath(fileName);
    var dir = Path.GetDirectoryName(destPath)!;
    Directory.CreateDirectory(dir);
    var tempPath = Path.Combine(dir, $".{Path.GetFileName(destPath)}.{Guid.NewGuid():N}.tmp");

    try
    {
        await using (var fs = new FileStream(
            tempPath,
            new FileStreamOptions {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous /* | FileOptions.WriteThrough */
            }))
        {
            // ここで bodyLen 分だけ PipeReader→FileStream にコピー
            await CopyFromPipeToStreamWithMaxAsync(reader, fs, bodyLen, MaxUploadBytes, ct);

            await fs.FlushAsync(ct);
            // fs.Flush(true); // 可能なら
        }

        File.Move(tempPath, destPath, overwrite: true);
        Console.WriteLine($"[UPLOAD] {destPath} ({bodyLen} bytes)");

        WriteUtf8(writer, "OK\n");
        await writer.FlushAsync(ct);
    }
    catch
    {
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        throw;
    }
}

static async Task HandleLegacyUploadAsync(PipeReader reader, PipeWriter writer, byte firstLenByte, CancellationToken ct)
{
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
    if (bodyLen > MaxUploadBytes) throw new InvalidOperationException($"file too large (>{MaxUploadBytes} bytes)");

    var destPath = Path.GetFullPath(fileName);
    var dir = Path.GetDirectoryName(destPath)!;
    Directory.CreateDirectory(dir);
    var tempPath = Path.Combine(dir, $".{Path.GetFileName(destPath)}.{Guid.NewGuid():N}.tmp");

    try
    {
        await using (var fs = new FileStream(
            tempPath,
            new FileStreamOptions {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous /* | FileOptions.WriteThrough */
            }))
        {
            await CopyFromPipeToStreamWithMaxAsync(reader, fs, bodyLen, MaxUploadBytes, ct);
            await fs.FlushAsync(ct);
        }

        File.Move(tempPath, destPath, overwrite: true);
        Console.WriteLine($"[UPLOAD-legacy] {destPath} ({bodyLen} bytes)");

        WriteUtf8(writer, "OK\n");
        await writer.FlushAsync(ct);
    }
    catch
    {
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        throw;
    }
}

// 受信を bodyLen と Max の両方で監視しつつコピー
static async ValueTask CopyFromPipeToStreamWithMaxAsync(
    PipeReader reader, Stream dest, long bodyLen, long maxBytes, CancellationToken ct)
{
    long remaining = bodyLen;
    long received = 0;

    while (remaining > 0)
    {
        var result = await reader.ReadAsync(ct);
        var buf = result.Buffer;
        if (buf.Length == 0 && result.IsCompleted) throw new IOException("unexpected EOF (body)");

        var toTake = Math.Min(remaining, (long)buf.Length);
        var slice = buf.Slice(0, toTake);

        foreach (var seg in slice)
        {
            await dest.WriteAsync(seg, ct);
            received += seg.Length;
            if (received > maxBytes) throw new InvalidOperationException("file too large (stream)");
        }

        reader.AdvanceTo(slice.End);
        remaining -= toTake;
    }
}
```

---

## 補足メモ

* **上限チェック**は「宣言サイズ（`bodyLen`）を先に弾く」＋「実受信量（`received`）でも弾く」の**二重化**が堅いです。
  （宣言が小さくても異常系でオーバーする可能性を潰す）
* **アトミック性**：同一ディレクトリ内 `Move` は一般的にアトミック（同一ボリューム前提）。
  Windows ではさらに確実にするなら `File.Replace(temp, dest, backupPath, ignoreMetadataErrors: true)` も検討可。
* **ディスク flush**：障害耐性重視なら `FileStream.Flush(true)`（.NET 8）や WriteThrough を適用。性能と相談で。

---

必要なら、「上限超過時の**専用エラーコードを返す**」などプロトコルの応答も整えます（`ERR:TOO_LARGE\n` 等）。

できます。意味としては主に2通りあります：

1. **クライアント1台 → 複数サーバーへ同じファイルを配る（マルチターゲット送信）**
2. **クライアント1台 → 1つのサーバーへアップロードすると、サーバーが複数クライアントへ配信（ブロードキャスト／Pub-Sub）**

下に**どちらも最小実装**を置きます。いまのプロトコル（UPLOAD/ DOWNLOAD）を壊さずに拡張します。

---

# 1) クライアント側で複数サーバーへ並列アップロード（最短ルート）

既存の `UPLOAD (0x01)` をそのまま使い、接続先を複数にするだけ。
（※エラー時のリトライ・タイムアウトは適宜足してください）

```csharp
public static async Task MultiUploadAsync(string[] hosts, int port, string localPath, int maxParallel = 4)
{
    using var sem = new SemaphoreSlim(maxParallel);
    var tasks = hosts.Select(async host =>
    {
        await sem.WaitAsync();
        try
        {
            await UploadAsync(host, port, localPath); // 既存の単発 Upload を流用
            Console.WriteLine($"OK: {host}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"NG: {host} -> {ex.Message}");
        }
        finally { sem.Release(); }
    });
    await Task.WhenAll(tasks);
}

// 既存の UPLOAD (0x01) をホスト引数付きに
static async Task UploadAsync(string host, int port, string localPath)
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(host, port);
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

    // ACK
    var ack = new byte[256];
    int ackN = await s.ReadAsync(ack);
    if (ackN <= 0) throw new IOException("no ack");
}
```

これで**1→多**がすぐ動きます。配布先を増やす、レプリカ戦略（全成功/過半数成功）を決める、などはこの層で制御できます。

---

# 2) サーバーで受けた1回のUPLOADを、接続中クライアントへ自動配信（ブロードキャスト）

プロトコルを**後方互換のまま**薄く拡張します：

* `0x06 = SUBSCRIBE` … クライアントが「配信を受けたい」と宣言
* `0x10 = PUSH` … サーバー→クライアントへの**サーバー起点**のファイル配信フレーム

  ```
  [0x10][int32 nameLen][name][int64 bodyLen][body]
  ```

クライアントは接続後に `0x06` を一度送るだけでOK。以降、誰かが `UPLOAD` するとサーバーが全サブスクライバへ `PUSH` を送ります。

## サーバー（Pipelines 版、抜粋パッチ）

* 接続ごとに「書き込みキュー」を直列化するため `SemaphoreSlim` を持たせます（他コマンドの応答と `PUSH` が混ざらないように）。
* サーバーは**保存済みのファイル**を、各サブスクライバの `PipeWriter` へ `PUSH` で送ります。遅いクライアントは個別に遅延するだけで、他に影響しない設計に。

```csharp
// 共有：サブスクライバの管理
static readonly ConcurrentDictionary<Guid, Subscriber> Subscribers = new();

sealed class Subscriber
{
    public required PipeWriter Writer { get; init; }
    public required SemaphoreSlim SendLock { get; init; } // 直列化用
    public required CancellationToken ConnectionToken { get; init; }
}

// ServeAsync 内：接続単位の登録/解除
var subId = Guid.NewGuid();
var sendLock = new SemaphoreSlim(1,1);
try
{
    // ...既存ループ...
    switch (first)
    {
        case 0x06: // SUBSCRIBE
            Subscribers[subId] = new Subscriber { Writer = writer, SendLock = sendLock, ConnectionToken = ct };
            // 任意のACK（省略可）
            WriteUtf8(writer, "SUBSCRIBED\n"); await writer.FlushAsync(ct);
            break;

        // 既存: 0x01..0x05 はそのまま
    }
}
finally
{
    Subscribers.TryRemove(subId, out _);
    sendLock.Dispose();
}
```

### UPLOAD 完了時に配信する

既存の `HandleUploadAsync` の保存成功直後に**ブロードキャスト**を足します。

```csharp
// 保存成功後
Console.WriteLine($"[UPLOAD] {destPath} ({bodyLen} bytes)");
WriteUtf8(writer, "OK\n");
await writer.FlushAsync(ct);

// ここから配信
await BroadcastPushAsync(Path.GetFileName(destPath), destPath, ct);
```

### BroadcastPushAsync の実装

```csharp
static async Task BroadcastPushAsync(string fileName, string fullPath, CancellationToken serverCt)
{
    if (!File.Exists(fullPath)) return;

    var nameBytes = Encoding.UTF8.GetBytes(fileName);
    var fi = new FileInfo(fullPath);
    long bodyLen = fi.Length;

    // 各サブスクライバに独立に送る（並列可だが、送信は各接続で直列化）
    var tasks = Subscribers.Values.Select(async sub =>
    {
        if (sub.ConnectionToken.IsCancellationRequested) return;
        await sub.SendLock.WaitAsync(); // この接続向け送信の直列化
        try
        {
            // PUSH ヘッダ
            WriteByte(sub.Writer, 0x10);
            WriteInt32LE(sub.Writer, nameBytes.Length);
            WriteBytes(sub.Writer, nameBytes);
            WriteInt64LE(sub.Writer, bodyLen);
            await sub.Writer.FlushAsync(serverCt);

            // ボディ（ファイル→PipeWriter）
            await using var fs = File.OpenRead(fullPath);
            await CopyFromStreamToPipeAsync(fs, sub.Writer, bodyLen, serverCt);
        }
        catch
        {
            // エラー時は無視（必要ならここで接続クローズや除名）
        }
        finally
        {
            sub.SendLock.Release();
        }
    });

    await Task.WhenAll(tasks);
    Console.WriteLine($"[PUSH] broadcasted {fileName} to {Subscribers.Count} subscriber(s)");
}
```

### クライアント側（受信）

サブスクライバは、接続直後に `0x06` を1回送ったあと、**常時受信ループ**で `0x10` を待ち受けて保存します：

```
send: [0x06]                   // SUBSCRIBE
loop:
  read: [byte cmd]
  if cmd==0x10 (PUSH):
     read [int32 nameLen][name][int64 bodyLen][body]
     -> save to disk
  else:
     // 既存の応答（PONGやSUBSCRIBEDなど）を適宜ハンドル
```

> 既存の PULL 型 (`DOWNLOAD`) と共存できます。PUSH を嫌う端末は SUBSCRIBE を送らなければ、今まで通り必要なときだけ DOWNLOAD すればOK。

---

## どっちを選ぶべき？

* **手早く“1→多”を実現**したい：➡ **クライアント多重アップロード**（方法1）
  配布先の数が多くても、並列数を制御しやすく、失敗/成功の集計も簡単。

* **サーバーが**「誰かのUPを**自動で**他クライアントへ配りたい」：➡ **ブロードキャスト**（方法2）
  配信サブスクライバを保持するだけで、自動反映ができます（ログ収集・アセット配布などに便利）。

---

## 既存の安全機能との整合

* **サイズ上限**・**アトミック保存**は方法1/2どちらでもそのまま有効。
* ブロードキャスト時は**PUSH先での書き込みもアトミック**にしたいなら、クライアント側保存も「一時ファイル→Move」にしてください。

---

必要なら、**PUSH 受信用の最小クライアント**（`0x06`送って `0x10`を保存）のコードもすぐ出します。どちらの方式から入れますか？

了解！最小限だけど実用的に動く **PUSH 受信用クライアント**（`0x06`=SUBSCRIBE を送って、サーバーからの `0x10`=PUSH を受け取り保存）の .NET 8 コンソール例です。
保存は**アトミック**（一時ファイル→`Move`）で行い、`Ctrl+C` で終了できます。

---

## Program.cs（最小クライアント）

```csharp
// dotnet new console -n PushClient && cd PushClient
// 置き換えて: dotnet run -- 127.0.0.1 5000 ./downloads
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

string host = args.Length >= 1 ? args[0] : "127.0.0.1";
int port    = args.Length >= 2 ? int.Parse(args[1]) : 5000;
string outDir = args.Length >= 3 ? args[2] : "./downloads";

Directory.CreateDirectory(outDir);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"Connecting to {host}:{port} ...");
using var tcp = new TcpClient();
await tcp.ConnectAsync(host, port, cts.Token);
using var s = tcp.GetStream();

// ---- SUBSCRIBE (0x06) ----
await s.WriteAsync(new byte[] { 0x06 }, cts.Token);
// （任意）サーバーから "SUBSCRIBED\n" 等のレスポンスが来る場合もありますが、なくてもOK

Console.WriteLine("Subscribed. Waiting for PUSH frames (0x10) ...  Press Ctrl+C to exit.");

// 受信ループ
try
{
    while (!cts.IsCancellationRequested)
    {
        int cmd = s.ReadByte();
        if (cmd == -1) break; // 切断
        if ((byte)cmd != 0x10)
        {
            // 他のサーバー応答（PONG など）はここに来る可能性あり。今回は読み飛ばし。
            // 必要なら行単位で読む等の処理を足してもよい。
            continue;
        }

        // PUSH frame: [0x10][int32 nameLen][name][int64 bodyLen][body]
        int nameLen = await ReadInt32LEAsync(s, cts.Token);
        if (nameLen <= 0 || nameLen > 4096) throw new InvalidOperationException("bad name length");

        string fileName = await ReadUtf8Async(s, nameLen, cts.Token);
        fileName = SanitizeFileName(fileName);

        long bodyLen = await ReadInt64LEAsync(s, cts.Token);
        if (bodyLen < 0) throw new InvalidOperationException("bad body length");

        string destPath = Path.GetFullPath(Path.Combine(outDir, fileName));
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        // アトミック保存：同一ディレクトリに一時ファイルを作ってから Move
        string tempPath = Path.Combine(Path.GetDirectoryName(destPath)!,
                                       $".{Path.GetFileName(destPath)}.{Guid.NewGuid():N}.tmp");

        long received = 0;
        try
        {
            await using var fs = new FileStream(
                tempPath,
                new FileStreamOptions {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous /* | FileOptions.WriteThrough */
                });

            var buf = new byte[81920];
            long remaining = bodyLen;

            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buf.Length, remaining);
                int n = await s.ReadAsync(buf.AsMemory(0, toRead), cts.Token);
                if (n == 0) throw new IOException("unexpected EOF while receiving body");
                await fs.WriteAsync(buf.AsMemory(0, n), cts.Token);
                remaining -= n;
                received += n;
            }

            await fs.FlushAsync(cts.Token);
            // fs.Flush(true); // .NET 8 以降・信頼性重視なら

            File.Move(tempPath, destPath, overwrite: true);
            Console.WriteLine($"[PUSH] saved: {destPath} ({received} bytes)");
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }
            throw;
        }
    }
}
catch (OperationCanceledException) { /* Ctrl+C */ }

Console.WriteLine("bye");

// ===== helpers =====
static async Task<int> ReadInt32LEAsync(NetworkStream s, CancellationToken ct)
{
    var buf = new byte[4];
    int off = 0;
    while (off < 4)
    {
        int n = await s.ReadAsync(buf.AsMemory(off), ct);
        if (n == 0) throw new IOException("unexpected EOF (int32)");
        off += n;
    }
    return BinaryPrimitives.ReadInt32LittleEndian(buf);
}

static async Task<long> ReadInt64LEAsync(NetworkStream s, CancellationToken ct)
{
    var buf = new byte[8];
    int off = 0;
    while (off < 8)
    {
        int n = await s.ReadAsync(buf.AsMemory(off), ct);
        if (n == 0) throw new IOException("unexpected EOF (int64)");
        off += n;
    }
    return BinaryPrimitives.ReadInt64LittleEndian(buf);
}

static async Task<string> ReadUtf8Async(NetworkStream s, int len, CancellationToken ct)
{
    var buf = new byte[len];
    int off = 0;
    while (off < len)
    {
        int n = await s.ReadAsync(buf.AsMemory(off), ct);
        if (n == 0) throw new IOException("unexpected EOF (utf8)");
        off += n;
    }
    return Encoding.UTF8.GetString(buf);
}

static string SanitizeFileName(string name)
{
    foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
    name = name.Replace("/", "_").Replace("\\", "_").Replace("..", "_");
    return name;
}
```

### 使い方

```bash
dotnet run -- 127.0.0.1 5000 ./downloads
# サーバー側が PUSH (0x10) を送るたびに ./downloads にアトミック保存されます
```

### メモ

* **PUSHの並び**：このクライアントは1接続1スレッドで順次保存（十分シンプル）。大量同時配信でも着実に処理できます。
* **保存先**：第三引数で保存ディレクトリ指定（無指定なら `./downloads`）。サーバーから届くファイル名はサニタイズ済み。
* **TLS** が必要なら `NetworkStream` の上に `SslStream` を載せ替えてください。
* **サイズ上限**を設けたい場合は、ヘッダ受信直後に `bodyLen` をチェックして中断してください。

必要なら **PUSH + 通常応答（PONG 等）の混在をきれいに扱う拡張**や、**到着ファイルのハッシュ検証**も足せます。

いいね。`PUSH`（バイナリ枠）と、`PONG`/`SUBSCRIBED`/`OK`/`ERR:...`（行テキスト応答）が**同じTCPストリームに混在**しても綺麗に扱えるように、
小さな**バッファ付きリーダ（Peek/ReadLine/ReadExactly）**を用意して**フレーム or 行**を判別して処理します。

* `0x10` が来たら **PUSHフレーム**として `nameLen/name/bodyLen/body` を読み取り保存（アトミック）
* それ以外なら **1行テキスト**として読み取りログに出す（`PONG` など）
* ついでに**定期PING**（30秒ごと）も同接続で送る例を入れています

---

## Program.cs（PUSH + 行応答 混在処理クライアント / .NET 8）

```csharp
// dotnet new console -n PushClientMix && cd PushClientMix
// 置き換えて: dotnet run -- 127.0.0.1 5000 ./downloads
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

string host   = args.Length >= 1 ? args[0] : "127.0.0.1";
int    port   = args.Length >= 2 ? int.Parse(args[1]) : 5000;
string outDir = args.Length >= 3 ? args[2] : "./downloads";
Directory.CreateDirectory(outDir);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"Connecting to {host}:{port} ...");
using var tcp = new TcpClient();
await tcp.ConnectAsync(host, port, cts.Token);
using var ns = tcp.GetStream();

// ------- SUBSCRIBE (0x06) -------
await ns.WriteAsync(new byte[] { 0x06 }, cts.Token);
Console.WriteLine("Subscribed. Will accept PUSH (0x10) and line responses together.");

// ------- keepalive ping task (optional) -------
var pingTask = Task.Run(async () =>
{
    var pingBuf = new byte[] { 0x05 }; // PING
    while (!cts.IsCancellationRequested)
    {
        try
        {
            await ns.WriteAsync(pingBuf, cts.Token);
        }
        catch { /* ignore; reader loop will end on disconnect */ }
        await Task.Delay(TimeSpan.FromSeconds(30), cts.Token);
    }
}, cts.Token);

// ------- reader with small internal buffer -------
var br = new BufferedReader(ns);

try
{
    while (!cts.IsCancellationRequested)
    {
        int b = await br.PeekByteAsync(cts.Token);
        if (b < 0) break; // disconnected

        if ((byte)b == 0x10)
        {
            // ---- PUSH frame: [0x10][int32 nameLen][name][int64 bodyLen][body] ----
            await br.ReadByteAsync(cts.Token); // consume 0x10

            int nameLen = await br.ReadInt32LEAsync(cts.Token);
            if (nameLen <= 0 || nameLen > 4096) throw new InvalidOperationException("bad name length");

            var nameBytes = await br.ReadExactlyAsync(nameLen, cts.Token);
            var fileName  = SanitizeFileName(Encoding.UTF8.GetString(nameBytes));

            long bodyLen = await br.ReadInt64LEAsync(cts.Token);
            if (bodyLen < 0) throw new InvalidOperationException("bad body length");

            string destPath = Path.GetFullPath(Path.Combine(outDir, fileName));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            string tempPath = Path.Combine(Path.GetDirectoryName(destPath)!,
                                           $".{Path.GetFileName(destPath)}.{Guid.NewGuid():N}.tmp");

            long remaining = bodyLen;
            long received  = 0;
            try
            {
                await using var fs = new FileStream(
                    tempPath,
                    new FileStreamOptions {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        Options = FileOptions.Asynchronous /* | FileOptions.WriteThrough */
                    });

                var buf = new byte[81920];
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buf.Length, remaining);
                    int n = await br.ReadIntoBufferAsync(buf.AsMemory(0, toRead), cts.Token);
                    if (n == 0) throw new IOException("unexpected EOF (body)");
                    await fs.WriteAsync(buf.AsMemory(0, n), cts.Token);
                    remaining -= n;
                    received  += n;
                }

                await fs.FlushAsync(cts.Token);
                File.Move(tempPath, destPath, overwrite: true);
                Console.WriteLine($"[PUSH] saved: {destPath} ({received} bytes)");
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }
        else
        {
            // ---- Line response (PONG/SUBSCRIBED/OK/ERR:...) ----
            string line = await br.ReadLineAsync(cts.Token);
            if (line == null) break;
            Console.WriteLine($"[LINE] {line}");
        }
    }
}
catch (OperationCanceledException) { /* Ctrl+C */ }

cts.Cancel(); // stop pinger
try { await pingTask; } catch { }
Console.WriteLine("bye");

// ====================== Helpers ======================

static string SanitizeFileName(string name)
{
    foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
    name = name.Replace("/", "_").Replace("\\", "_").Replace("..", "_");
    return name;
}

/// <summary>
/// Small buffered reader: supports PeekByte / ReadByte / ReadLine (LF) / ReadInt32/64 LE / ReadExactly
/// and a method to fill a user buffer (for body streaming).
/// </summary>
sealed class BufferedReader
{
    private readonly NetworkStream _s;
    private byte[] _buf;
    private int _pos;
    private int _len;

    public BufferedReader(NetworkStream s, int capacity = 32 * 1024)
    {
        _s = s;
        _buf = new byte[capacity];
    }

    public async Task<int> PeekByteAsync(CancellationToken ct)
    {
        if (_pos >= _len)
            await FillAsync(ct);
        return _pos < _len ? _buf[_pos] : -1;
    }

    public async Task<int> ReadByteAsync(CancellationToken ct)
    {
        if (_pos >= _len)
            await FillAsync(ct);
        if (_pos >= _len) return -1;
        return _buf[_pos++];
    }

    public async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _len && !await FillAsync(ct))
                return sb.Length == 0 ? null : sb.ToString(); // EOF

            for (; _pos < _len; _pos++)
            {
                byte b = _buf[_pos];
                if (b == (byte)'\n')
                {
                    _pos++; // consume LF
                    // trim optional CR
                    if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                    return sb.ToString();
                }
                sb.Append((char)b);
            }
        }
    }

    public async Task<byte[]> ReadExactlyAsync(int count, CancellationToken ct)
    {
        var dst = new byte[count];
        int off = 0;
        while (off < count)
        {
            int copied = CopyFromInternal(dst.AsSpan(off));
            if (copied == 0)
            {
                if (!await FillAsync(ct)) throw new IOException("unexpected EOF");
                continue;
            }
            off += copied;
        }
        return dst;
    }

    public async Task<int> ReadIntoBufferAsync(Memory<byte> dest, CancellationToken ct)
    {
        // Prefer internal buffer first
        int copied = CopyFromInternal(dest.Span);
        if (copied > 0) return copied;

        // If request is big, read directly into dest to avoid extra copy
        if (dest.Length >= _buf.Length)
        {
            int n = await _s.ReadAsync(dest, ct);
            _pos = _len = 0;
            return n;
        }

        // Otherwise, fill internal and try again
        if (!await FillAsync(ct)) return 0;
        return CopyFromInternal(dest.Span);
    }

    public async Task<int> ReadInt32LEAsync(CancellationToken ct)
    {
        var bytes = await ReadExactlyAsync(4, ct);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    public async Task<long> ReadInt64LEAsync(CancellationToken ct)
    {
        var bytes = await ReadExactlyAsync(8, ct);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    // ---- internal helpers ----
    private int CopyFromInternal(Span<byte> dest)
    {
        int available = _len - _pos;
        if (available <= 0) return 0;
        int toCopy = Math.Min(dest.Length, available);
        _buf.AsSpan(_pos, toCopy).CopyTo(dest);
        _pos += toCopy;
        return toCopy;
    }

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        _pos = 0;
        _len = await _s.ReadAsync(_buf, ct);
        return _len > 0;
    }
}
```

### 何をしているか

* **先読み `PeekByteAsync`** で次の1バイトが `0x10` なら PUSH と判断、そうでなければ行テキストとして処理。
* **BufferedReader** を挟むことで、`ReadLine` と **ボディの大きな連続読み**を同じストリームで安全に両立。
* PUSH保存は**一時ファイル→Move**でアトミック。
* 任意の**定期PING**も同接続で実行（なくてもOK）。

> サーバー側で `SUBSCRIBE` のACKを行テキストで返す（`SUBSCRIBED\n`）、`PING` に `PONG\n` を返す、`UPLOAD`のACKに `OK\n` を返す、のような**行応答**と混在しても安全に処理できます。

必要なら、ここに**最大受信サイズ**のチェックや**保存先のサブディレクトリ分岐**、**ハッシュ検証**を足すパッチも書けます。
