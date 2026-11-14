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

            Console.WriteLine("================================================================");
            Console.WriteLine("   PULSE HRM MINI - TCP SOCKET & REDIS (.NET 9 CROSS-PLATFORM)  ");
            Console.WriteLine("   Môn học: Lập trình mạng (IEC_CNTT) - Windows & macOS Ready   ");
            Console.WriteLine("================================================================");
            Console.WriteLine(" [1] Khởi chạy ADMIN SERVER + GIAO DIỆN WEB COCKPIT (Port 9000 & 5050)");
            Console.WriteLine(" [2] Khởi chạy EMPLOYEE CLIENT TRÊN TERMINAL (TcpClient + Heartbeat 5s)");
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
                    Console.WriteLine("-> Đã tự động kích hoạt In-Memory Redis Engine (Hỗ trợ đầy đủ Hash + Set + TTL 15s)!");
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
                server.StartBackgroundListener(cts.Token);

                var webUi = new WebDashboardHost(redisService, server, HrmProtocol.WebUiPort);
                await webUi.StartAsync(cts.Token);

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[WEB UI READY] Đã tự động mở giao diện quản trị tại: http://localhost:{HrmProtocol.WebUiPort}");
                Console.ResetColor();

                await server.RunAsync(cts);
            }
            else
            {
                Console.Write("Nhập IP của Admin Server (Nhấn Enter để dùng 127.0.0.1): ");
                string serverIp = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(serverIp)) serverIp = "127.0.0.1";

                var client = new HrmTcpClient(serverIp, HrmProtocol.DefaultPort);
                await client.StartClientInteractiveAsync();
            }
        }
    }
}
