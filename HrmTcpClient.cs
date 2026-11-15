using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iec
{
    public class HrmTcpClient
    {
        private string _serverIp;
        private readonly int _serverPort;
        private TcpClient _client;
        private StreamReader _reader;
        private StreamWriter _writer;
        private string _empId;
        private int _pingCount = 0;
        private double _lastRttMs = 1.2;

        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public HrmTcpClient(string serverIp, int serverPort = HrmProtocol.DefaultPort)
        {
            _serverIp = serverIp;
            _serverPort = serverPort;
        }

        // Dùng giao thức UDP Broadcast (Port 9001) để tự động tìm IP của Admin Server trong mạng LAN
        public static async Task<string> DiscoverServerViaUdpAsync(int timeoutMs = 1800)
        {
            try
            {
                using var udp = new UdpClient();
                udp.EnableBroadcast = true;
                byte[] req = Encoding.UTF8.GetBytes(HrmProtocol.UdpDiscoverRequest);
                await udp.SendAsync(req, req.Length, new IPEndPoint(IPAddress.Broadcast, HrmProtocol.UdpDiscoveryPort));

                using var cts = new CancellationTokenSource(timeoutMs);
                var res = await udp.ReceiveAsync(cts.Token);
                string text = Encoding.UTF8.GetString(res.Buffer).Trim();
                if (text.StartsWith(HrmProtocol.UdpDiscoverResponse))
                {
                    string[] parts = text.Split('|');
                    if (parts.Length >= 2) return parts[1];
                }
            }
            catch { }
            return "127.0.0.1";
        }

        private async Task SafeSendLineAsync(string payload)
        {
            await _writeLock.WaitAsync();
            try
            {
                if (_client != null && _client.Connected && _writer != null)
                {
                    await _writer.WriteLineAsync(payload);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task StartClientInteractiveAsync()
        {
            if (string.IsNullOrWhiteSpace(_serverIp) || _serverIp.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[UDP DISCOVERY] Đang phát gói tin UDP Broadcast tới cổng {HrmProtocol.UdpDiscoveryPort} để tìm Server...");
                _serverIp = await DiscoverServerViaUdpAsync();
                Console.WriteLine($"[UDP DISCOVERY] Đã tìm thấy Admin Server tại địa chỉ: {_serverIp}:{_serverPort}");
                Console.ResetColor();
            }

            Console.Write("Nhập Mã Nhân Viên (VD: NV04): ");
            _empId = HrmProtocol.Sanitize(Console.ReadLine(), "NV04").ToUpperInvariant();

            Console.Write("Nhập Họ và Tên (VD: Huynh Nhat Hoa): ");
            string fullName = HrmProtocol.Sanitize(Console.ReadLine(), "Nhan Vien Test");

            Console.Write("Nhập Phòng Ban (VD: IT): ");
            string dept = HrmProtocol.Sanitize(Console.ReadLine(), "IT");

            Console.Write("Nhập Công việc đang làm (VD: Task HRM-4 Thiết kế ERD): ");
            string task = HrmProtocol.Sanitize(Console.ReadLine(), "Task HRM-4 Thiết kế ERD");

            try
            {
                _client = new TcpClient();
                Console.WriteLine($"Đang kết nối TCP tới {_serverIp}:{_serverPort}...");
                await _client.ConnectAsync(_serverIp, _serverPort);

                NetworkStream stream = _client.GetStream();
                var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                _reader = new StreamReader(stream, utf8NoBom);
                _writer = new StreamWriter(stream, utf8NoBom) { AutoFlush = true };

                await SafeSendLineAsync($"{HrmProtocol.CmdLogin}|{_empId}|{fullName}|{dept}|{task}");

                using var cts = new CancellationTokenSource();
                _ = Task.Run(() => ListenServerAsync(cts.Token));
                _ = Task.Run(() => HeartbeatLoopAsync(cts.Token));

                await EmployeeMenuLoopAsync(cts);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[LỖI KẾT NỐI TCP] Không thể kết nối tới Server ({_serverIp}:{_serverPort}): {ex.Message}");
                Console.ResetColor();
            }
        }

        private async Task HeartbeatLoopAsync(CancellationToken token)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(HrmProtocol.HeartbeatIntervalMs));
                while (await timer.WaitForNextTickAsync(token))
                {
                    long nowTick = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await SafeSendLineAsync($"{HrmProtocol.CmdPing}|{_empId}|{nowTick}|{_lastRttMs:0.0}");
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private async Task ListenServerAsync(CancellationToken token)
        {
            try
            {
                string line;
                while (!token.IsCancellationRequested && (line = await _reader.ReadLineAsync()) != null)
                {
                    line = line.Trim('\uFEFF', '\r', '\n');
                    if (line.StartsWith("PONG"))
                    {
                        _pingCount++;
                        string[] p = line.Split('|');
                        if (p.Length >= 2 && long.TryParse(p[1], out long sentTick))
                        {
                            long diff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sentTick;
                            _lastRttMs = Math.Max(0.5, diff);
                        }
                        continue;
                    }

                    if (line.StartsWith("BROADCAST|"))
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"\n{line.Substring("BROADCAST|".Length)}");
                        Console.ResetColor();
                    }
                    else if (line.StartsWith("REPLAY|"))
                    {
                        Console.ForegroundColor = ConsoleColor.DarkCyan;
                        Console.WriteLine($"\n[REDIS LIST REPLAY] {line.Substring("REPLAY|".Length)}");
                        Console.ResetColor();
                    }
                    else if (line.StartsWith("ERR|"))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\n[BẢO MẬT REDIS SECOPS] {line}");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"\n[PHẢN HỒI SERVER] {line}");
                        Console.ResetColor();
                    }
                }
            }
            catch
            {
                if (!token.IsCancellationRequested)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("\n[CẢNH BÁO] Đã mất kết nối TCP với Server!");
                    Console.ResetColor();
                }
            }
        }

        private async Task EmployeeMenuLoopAsync(CancellationTokenSource cts)
        {
            while (!cts.IsCancellationRequested)
            {
                Console.WriteLine($"\n--- CỬA SỔ NHÂN VIÊN: {_empId} (RTT: {_lastRttMs:0.0}ms) ---");
                Console.WriteLine(" [1] Đổi trạng thái: Online  | [2] Busy  | [3] Away");
                Console.WriteLine(" [4] Cập nhật Công việc đang làm (Lưu vào Redis HASH)");
                Console.WriteLine(" [5] Gửi tin nhắn Chat chung (CHAT) hoặc Nhắn riêng (DM) qua Redis LIST");
                Console.WriteLine(" [6] Xem thống kê Nhịp tim PING/PONG & Độ trễ RTT (ms)");
                Console.WriteLine(" [7] Giả lập Tấn công Spam Flood 30 gói (Test Redis Rate-Limiter khóa 30s)");
                Console.WriteLine(" [0] Đăng xuất chuẩn (LOGOUT)");
                Console.Write("Chọn thao tác: ");

                string choice = Console.ReadLine()?.Trim();
                if (choice == "1") await SafeSendLineAsync($"{HrmProtocol.CmdStatus}|{_empId}|Online");
                else if (choice == "2") await SafeSendLineAsync($"{HrmProtocol.CmdStatus}|{_empId}|Busy");
                else if (choice == "3") await SafeSendLineAsync($"{HrmProtocol.CmdStatus}|{_empId}|Away");
                else if (choice == "4")
                {
                    Console.Write("Nhập tên Task & tiến độ (VD: HRM-4 Thiết kế ERD 90%): ");
                    string t = Console.ReadLine();
                    await SafeSendLineAsync($"{HrmProtocol.CmdTask}|{_empId}|{t}");
                }
                else if (choice == "5")
                {
                    Console.Write("Nhập Mã NV nhận (Nhấn Enter để gửi ALL toàn công ty, hoặc gõ NV01): ");
                    string target = Console.ReadLine()?.Trim().ToUpperInvariant();
                    Console.Write("Nhập nội dung tin nhắn: ");
                    string msg = Console.ReadLine();
                    if (string.IsNullOrEmpty(target) || target == "ALL")
                        await SafeSendLineAsync($"{HrmProtocol.CmdChat}|{_empId}|{msg}");
                    else
                        await SafeSendLineAsync($"{HrmProtocol.CmdDm}|{_empId}|{target}|{msg}");
                }
                else if (choice == "6")
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"-> Đã gửi {_pingCount} nhịp tim PING/PONG | Độ trễ khứ hồi RTT = {_lastRttMs:0.0} ms.");
                    Console.ResetColor();
                }
                else if (choice == "7")
                {
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.WriteLine("-> Đang bắn liên thanh 30 gói tin TCP để kiểm thử tường lửa Redis Rate-Limiter...");
                    Console.ResetColor();
                    for (int i = 0; i < 30; i++)
                    {
                        await SafeSendLineAsync($"{HrmProtocol.CmdPing}|{_empId}|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}|{_lastRttMs:0.0}");
                    }
                }
                else if (choice == "0")
                {
                    await SafeSendLineAsync($"{HrmProtocol.CmdLogout}|{_empId}");
                    await Task.Delay(300);
                    cts.Cancel();
                    _client.Close();
                    break;
                }
            }
        }
    }
}