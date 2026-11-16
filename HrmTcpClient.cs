using System;
using System.Diagnostics;
using System.Globalization;
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
        private double? _lastRttMs; // null cho tới khi nhận PONG đầu tiên

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
                // Lấy IP từ địa chỉ nguồn của gói trả lời (do tầng mạng ghi), không tin IP server tự khai trong payload
                if (text.StartsWith(HrmProtocol.UdpDiscoverResponse))
                    return res.RemoteEndPoint.Address.ToString();
            }
            catch (OperationCanceledException) { }
            catch (SocketException ex)
            {
                Console.WriteLine($"[UDP DISCOVERY] Lỗi gửi/nhận UDP: {ex.Message}");
            }
            return null; // không có Server nào trả lời
        }

        private string RttText => _lastRttMs.HasValue ? $"{_lastRttMs:0.00} ms" : "chưa đo";

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
                if (_serverIp != null)
                {
                    Console.WriteLine($"[UDP DISCOVERY] Đã tìm thấy Admin Server tại địa chỉ: {_serverIp}:{_serverPort}");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("[UDP DISCOVERY] Không có Server nào trả lời trong mạng LAN.");
                    Console.ResetColor();
                    Console.Write("Nhập IP Server bằng tay (Enter = 127.0.0.1): ");
                    _serverIp = Console.ReadLine()?.Trim();
                    if (string.IsNullOrEmpty(_serverIp)) _serverIp = "127.0.0.1";
                }
            }

            Console.Write("Nhập Mã Nhân Viên (VD: NV04): ");
            _empId = HrmProtocol.Sanitize(Console.ReadLine(), "NV04").ToUpperInvariant();

            Console.Write("Nhập Họ và Tên (VD: Huynh Nhat Hoa): ");
            string fullName = HrmProtocol.Sanitize(Console.ReadLine(), "Nhan Vien Test");

            Console.Write("Nhập Phòng Ban (VD: IT): ");
            string dept = HrmProtocol.Sanitize(Console.ReadLine(), "IT");

            try
            {
                _client = new TcpClient();
                Console.WriteLine($"Đang kết nối TCP tới {_serverIp}:{_serverPort}...");
                await _client.ConnectAsync(_serverIp, _serverPort);

                NetworkStream stream = _client.GetStream();
                var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                _reader = new StreamReader(stream, utf8NoBom);
                _writer = new StreamWriter(stream, utf8NoBom) { AutoFlush = true };

                await SafeSendLineAsync($"{HrmProtocol.CmdLogin}|{_empId}|{fullName}|{dept}");

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
                    // Gửi kèm mốc Stopwatch (độ phân giải dưới 1ms); Server trả nguyên văn trong PONG để tính RTT
                    string rtt = _lastRttMs.HasValue ? "|" + _lastRttMs.Value.ToString("0.00", CultureInfo.InvariantCulture) : "";
                    await SafeSendLineAsync($"{HrmProtocol.CmdPing}|{_empId}|{Stopwatch.GetTimestamp()}{rtt}");
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
                    if (line == "BYE") return; // Server x\u00E1c nh\u1EADn LOGOUT, EOF sau \u0111\u00F3 l\u00E0 b\u00ECnh th\u01B0\u1EDDng
                    if (line.StartsWith("PONG"))
                    {
                        _pingCount++;
                        string[] p = line.Split('|');
                        if (p.Length >= 2 && long.TryParse(p[1], out long sentTick))
                        {
                            _lastRttMs = Stopwatch.GetElapsedTime(sentTick).TotalMilliseconds;
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
                        Console.WriteLine($"\n[LỖI TỪ SERVER] {line}");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"\n[PHẢN HỒI SERVER] {line}");
                        Console.ResetColor();
                    }
                }
                // ReadLineAsync trả null = Server đã đóng kết nối (EOF)
                if (!token.IsCancellationRequested) throw new IOException("EOF");
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
                Console.WriteLine($"\n--- CỬA SỔ NHÂN VIÊN: {_empId} (RTT: {RttText}) ---");
                Console.WriteLine(" [1] Đổi trạng thái: Online  | [2] Busy  | [3] Away");
                Console.WriteLine(" [4] Gửi tin nhắn Chat chung (CHAT) hoặc Nhắn riêng (DM) qua Redis Pub/Sub");
                Console.WriteLine(" [5] Xem thống kê Nhịp tim PING/PONG & Độ trễ RTT (ms)");
                Console.WriteLine(" [6] Giả lập Tấn công Spam Flood 30 gói (Test Redis Rate-Limiter khóa 30s)");
                Console.WriteLine(" [0] Đăng xuất chuẩn (LOGOUT)");
                Console.Write("Chọn thao tác: ");

                string choice = Console.ReadLine()?.Trim();
                if (choice == "1") await SafeSendLineAsync($"{HrmProtocol.CmdStatus}|{_empId}|Online");
                else if (choice == "2") await SafeSendLineAsync($"{HrmProtocol.CmdStatus}|{_empId}|Busy");
                else if (choice == "3") await SafeSendLineAsync($"{HrmProtocol.CmdStatus}|{_empId}|Away");
                else if (choice == "4")
                {
                    Console.Write("Nhập Mã NV nhận (Nhấn Enter để gửi ALL toàn công ty, hoặc gõ NV01): ");
                    string target = Console.ReadLine()?.Trim().ToUpperInvariant();
                    Console.Write("Nhập nội dung tin nhắn: ");
                    string msg = HrmProtocol.Sanitize(Console.ReadLine(), ""); // ký tự | trong tin sẽ làm vỡ khung lệnh
                    if (msg == "") continue;
                    if (string.IsNullOrEmpty(target) || target == "ALL")
                        await SafeSendLineAsync($"{HrmProtocol.CmdChat}|{_empId}|{msg}");
                    else
                        await SafeSendLineAsync($"{HrmProtocol.CmdDm}|{_empId}|{target}|{msg}");
                }
                else if (choice == "5")
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"-> Đã nhận {_pingCount} nhịp PONG | Độ trễ khứ hồi RTT = {RttText}.");
                    Console.ResetColor();
                }
                else if (choice == "6")
                {
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.WriteLine("-> Đang bắn liên thanh 30 gói tin TCP để kiểm thử tường lửa Redis Rate-Limiter...");
                    Console.ResetColor();
                    for (int i = 0; i < 30; i++)
                    {
                        // Dùng STATUS thay vì PING để đợt spam không cộng điểm uptime
                        await SafeSendLineAsync($"{HrmProtocol.CmdStatus}|{_empId}|Online");
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