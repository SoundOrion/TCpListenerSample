using System.Net.Sockets;

using var tcp = new TcpClient();
await tcp.ConnectAsync("127.0.0.1", 5000);
using var s = tcp.GetStream();
var send = System.Text.Encoding.UTF8.GetBytes("hello");
await s.WriteAsync(send);
var buf = new byte[1024];
var n = await s.ReadAsync(buf);
Console.WriteLine(System.Text.Encoding.UTF8.GetString(buf, 0, n)); // -> hello
