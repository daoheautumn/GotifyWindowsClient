using System.Net.WebSockets;
using System.Text;

namespace GotifyTest
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            var serverUrl = "http://115.239.248.156:11356"; // Gotify 服务器地址
            var clientToken = "CngNPLT3AKo4lCo";      // 在 Gotify Web 界面获取

            // 转换为 WebSocket 协议
            var wsServerUrl = serverUrl
                .Replace("http://", "ws://")
                .Replace("https://", "wss://"); // 如果是 HTTPS

            // 创建 WebSocket 连接
            using var ws = new ClientWebSocket();
            string url = $"{wsServerUrl}/stream?token={clientToken}";
            await ws.ConnectAsync(new Uri(url), CancellationToken.None);

            // 先获取历史消息
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("X-Gotify-Key", clientToken);
            var history = await httpClient.GetStringAsync($"{serverUrl}/message?limit=5");
            Console.WriteLine("历史消息:\n" + history);

            // 持续接收实时消息
            var buffer = new byte[1024];
            Console.WriteLine("\n等待新消息... (按 Ctrl+C 退出)");
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Console.WriteLine($"\n新消息: {message}");
                }
            }
        }
    }
}
