using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iec
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            Console.WriteLine("==========================================================================");
            Console.WriteLine("   IEC ENTERPRISE - TCP/UDP SOCKET & 5-STRUCT REDIS ENGINE (.NET 9)       ");
            Console.WriteLine("   Môn học: Lập trình mạng (IEC_CNTT) - Windows & macOS Cross-Platform    ");
            Console.WriteLine("==========================================================================");
            Console.WriteLine(" [1] Khởi chạy ADMIN SERVER + UDP DISCOVERY + WEB COCKPIT (:9000/:9001/:5050)");
            Console.WriteLine(" [2] Khởi chạy EMPLOYEE CLIENT (Hỗ trợ UDP Auto-Discovery tự tìm IP LAN)");
            Console.Write("Chọn vai trò chạy trên cửa sổ Terminal này (1 hoặc 2): ");

            string mode = Console.ReadLine()?.Trim();

            if (mode == "1")
            {
                Console.Write("Nhập địa chỉ Redis (Nhấn Enter để dùng mặc định localhost:6379): ");
                string redisConn = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(redisConn)) redisConn = "localhost:6379";

                // Chạy nhiều Server IEC dùng chung một Redis: mỗi Server một cổng TCP riêng
                Console.Write($"Nhập cổng TCP của Server này (Nhấn Enter để dùng {HrmProtocol.DefaultPort}): ");
                if (!int.TryParse(Console.ReadLine()?.Trim(), out int tcpPort)) tcpPort = HrmProtocol.DefaultPort;

                RedisPresenceService redisService;
                try
                {
                    redisService = new RedisPresenceService(redisConn);
                }
                catch (StackExchange.Redis.RedisConnectionException)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[LỖI REDIS] Không kết nối được Redis tại '{redisConn}'.");
                    Console.WriteLine("-> Hãy bật Redis trước (macOS: brew services start redis | Docker: docker run -p 6379:6379 redis).");
                    Console.ResetColor();
                    return;
                }
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[REDIS OK] Đã kết nối tới Redis Server ({redisConn}).");
                Console.ResetColor();

                using var cts = new CancellationTokenSource();
                var server = new HrmTcpServer(redisService, tcpPort);
                if (!server.StartBackgroundListener(cts.Token))
                {
                    return;
                }

                var webUi = new WebDashboardHost(redisService, server, HrmProtocol.WebPortFor(tcpPort));
                await webUi.StartAsync(cts.Token);

                await server.RunAsync(cts);
            }
            else
            {
                Console.Write("Nhập IP[:cổng] của Admin Server (Nhấn Enter để tự động dò tìm qua UDP Broadcast cổng 9001): ");
                string serverIp = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(serverIp)) serverIp = "AUTO";

                int port = HrmProtocol.DefaultPort;
                int colon = serverIp.LastIndexOf(':');
                if (colon > 0 && int.TryParse(serverIp[(colon + 1)..], out int p))
                {
                    port = p;
                    serverIp = serverIp[..colon];
                }

                var client = new HrmTcpClient(serverIp, port);
                await client.StartClientInteractiveAsync();
            }
        }
    }
}