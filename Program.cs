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

                var redisService = new RedisPresenceService(redisConn);
                await redisService.CheckConnectionAsync();

                if (redisService.IsUsingFallbackMemory)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[CHẾ ĐỘ GIẢ LẬP REDIS] Chưa phát hiện Redis Server tại '{redisConn}'.");
                    Console.WriteLine("-> Đã kích hoạt In-Memory 5-Struct Redis Engine (SET, HASH, TTL, ZSET, LIST + RateLimit)!");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[REDIS OK] Đã kết nối thành công tới Redis Server thật ({redisConn}).");
                    Console.ResetColor();
                }

                using var cts = new CancellationTokenSource();
                var server = new HrmTcpServer(redisService, HrmProtocol.DefaultPort);
                if (!server.StartBackgroundListener(cts.Token))
                {
                    return;
                }

                var webUi = new WebDashboardHost(redisService, server, HrmProtocol.WebUiPort);
                await webUi.StartAsync(cts.Token);

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[WEB UI READY] Đã mở giao diện quản trị tại: http://localhost:{HrmProtocol.WebUiPort}");
                Console.ResetColor();

                await server.RunAsync(cts);
            }
            else
            {
                Console.Write("Nhập IP của Admin Server (Nhấn Enter để tự động dò tìm qua UDP Broadcast cổng 9001): ");
                string serverIp = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(serverIp)) serverIp = "AUTO";

                var client = new HrmTcpClient(serverIp, HrmProtocol.DefaultPort);
                await client.StartClientInteractiveAsync();
            }
        }
    }
}