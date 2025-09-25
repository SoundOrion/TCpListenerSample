using System.Net.Sockets;
using System.Text;

var filePath = args.Length > 0 ? args[0] : "sample.bin";
var fileName = Path.GetFileName(filePath);

using var tcp = new TcpClient();
await tcp.ConnectAsync("127.0.0.1", 5000);
using var s = tcp.GetStream();

var nameBytes = Encoding.UTF8.GetBytes(fileName);
var nameLen = BitConverter.GetBytes(nameBytes.Length);
await s.WriteAsync(nameLen);          // 1) ファイル名長
await s.WriteAsync(nameBytes);        // 2) ファイル名

var fileLen = new FileInfo(filePath).Length;
await s.WriteAsync(BitConverter.GetBytes(fileLen)); // 3) 本体長

await using var fs = File.OpenRead(filePath);       // 4) 本体
var buffer = new byte[81920];
int n;
while ((n = await fs.ReadAsync(buffer)) > 0)
    await s.WriteAsync(buffer.AsMemory(0, n));

var ackBuf = new byte[256];
int ackN = await s.ReadAsync(ackBuf);
Console.WriteLine(Encoding.UTF8.GetString(ackBuf, 0, ackN));
