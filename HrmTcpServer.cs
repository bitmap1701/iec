using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iec
{
    // Bọc phiên kết nối với SemaphoreSlim để chống xung đột luồng khi vừa gửi PONG vừa gửi BROADCAST
    public class ClientSession
    {
        public TcpClient Client { get; }
        public StreamWriter Writer { get; }
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public ClientSession(TcpClient client, StreamWriter writer)
        {
            Client = client;
            Writer = writer;
        }

        public async Task SafeWriteLineAsync(string line)
        {
            await _writeLock.WaitAsync();
            try
            {
                if (Client.Connected)
                {
                    await Writer.WriteLineAsync(line);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }

    public class HrmTcpServer
    {
        private readonly RedisPresenceService _redisService;
        private readonly int _port;
        private TcpListener _listener;
        private bool _showPingLogs = false;

        private readonly ConcurrentDictionary<string, ClientSession> _activeSessions = new();
        private readonly ConcurrentQueue<TcpLogEntry> _tcpLogs = new();

        public HrmTcpServer(RedisPresenceService redisService, int port = HrmProtocol.DefaultPort)
        {
            _redisService = redisService;
            _port = port;
        }

        public bool IsClientSocketActive(string empId) =>
            _activeSessions.TryGetValue(empId, out var s) && s.Client.Connected;

        public List<TcpLogEntry> GetRecentLogs() => _tcpLogs.ToArray().Reverse().Take(60).ToList();

        public void ClearLogs() => _tcpLogs.Clear();

        public void AddCustomLog(string tag, string message)
        {
            _tcpLogs.Enqueue(new TcpLogEntry
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Tag = tag,
                Message = message
            });
            while (_tcpLogs.Count > 100) _tcpLogs.TryDequeue(out _);
        }

        public bool StartBackgroundListener(CancellationToken token)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
                AddCustomLog("SERVER", $"TcpListener started on 0.0.0.0:{_port} (Ready for Windows & macOS Clients)");
                _ = Task.Run(() => AcceptClientsLoopAsync(token), token);
                return true;
            }
            catch (SocketException)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[LỖI CỔNG MẠNG] Cổng TCP {_port} đang được sử dụng bởi một cửa sổ Server khác!");
                Console.WriteLine("-> Nếu bạn muốn chạy thêm Nhân viên (Client), hãy khởi chạy lại và chọn phím [2].");
                Console.ResetColor();
                return false;
            }
        }

        public async Task RunAsync(CancellationTokenSource cts = null)
        {
            cts ??= new CancellationTokenSource();
            if (_listener == null)
            {
                if (!StartBackgroundListener(cts.Token)) return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[TCP SERVER] Đang lắng nghe tại cổng {_port}...");
            Console.ResetColor();

            await AdminDashboardLoopAsync(cts);
        }

        private async Task AcceptClientsLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(token);
                    _ = Task.Run(() => HandleClientAsync(client), token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddCustomLog("ERROR", $"Listener error: {ex.Message}");
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            string currentEmpId = null;
            string clientEndpoint = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";

            try
            {
                using NetworkStream stream = client.GetStream();
                var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                using StreamReader reader = new(stream, utf8NoBom);
                using StreamWriter writer = new(stream, utf8NoBom) { AutoFlush = true };

                var session = new ClientSession(client, writer);

                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    line = line.Trim('\uFEFF', '\r', '\n');
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string[] parts = line.Split('|');
                    string cmd = parts[0].ToUpperInvariant();

                    if (cmd == HrmProtocol.CmdLogin && parts.Length >= 4)
                    {
                        currentEmpId = HrmProtocol.Sanitize(parts[1], "NV01").ToUpperInvariant();
                        string fullName = HrmProtocol.Sanitize(parts[2], "Nhân viên");
                        string dept = HrmProtocol.Sanitize(parts[3], "IT");

                        _activeSessions[currentEmpId] = session;
                        await _redisService.RegisterLoginAsync(currentEmpId, fullName, dept, clientEndpoint);

                        AddCustomLog("LOGIN", $"RECV: {line} -> HSET staff:profile:{currentEmpId} & SETEX staff:online:{currentEmpId} 15s");
                        await session.SafeWriteLineAsync($"LOGIN_OK|Đăng nhập thành công mã {currentEmpId}. TTL Redis = {HrmProtocol.OnlineTtlSeconds}s");
                    }
                    else if (cmd == HrmProtocol.CmdPing && parts.Length >= 2)
                    {
                        string empId = HrmProtocol.Sanitize(parts[1], "NV01").ToUpperInvariant();
                        currentEmpId ??= empId;
                        _activeSessions[empId] = session;

                        await _redisService.RefreshHeartbeatAsync(empId);
                        AddCustomLog("PING", $"RECV: {line} -> EXPIRE staff:online:{empId} 15s | SEND: PONG");
                        if (_showPingLogs)
                        {
                            LogConsole($"[PING] Nhận nhịp tim từ {empId} -> Gia hạn TTL 15s", ConsoleColor.DarkGray);
                        }
                        await session.SafeWriteLineAsync("PONG");
                    }
                    else if (cmd == HrmProtocol.CmdStatus && parts.Length >= 3)
                    {
                        string empId = HrmProtocol.Sanitize(parts[1], "NV01").ToUpperInvariant();
                        string newStatus = HrmProtocol.Sanitize(parts[2], "Online");
                        currentEmpId ??= empId;
                        _activeSessions[empId] = session;

                        await _redisService.UpdateStatusAsync(empId, newStatus);
                        AddCustomLog("STATUS", $"RECV: {line} -> SETEX staff:online:{empId} 15 \"{newStatus}\"");
                        await session.SafeWriteLineAsync($"STATUS_OK|Đã cập nhật trạng thái trên Redis: {newStatus}");
                    }
                    else if (cmd == HrmProtocol.CmdLogout && parts.Length >= 2)
                    {
                        string empId = HrmProtocol.Sanitize(parts[1], "NV01").ToUpperInvariant();
                        await _redisService.SetOfflineImmediatelyAsync(empId);
                        AddCustomLog("LOGOUT", $"RECV: {line} -> DEL staff:online:{empId} (Offline ngay lập tức)");
                        await session.SafeWriteLineAsync("BYE");
                        break;
                    }
                }
            }
            catch (IOException)
            {
                AddCustomLog("DROP", $"Socket {currentEmpId ?? clientEndpoint} ngắt đột ngột! Chờ Redis TTL 15s tự hủy khóa...");
            }
            catch (Exception ex)
            {
                AddCustomLog("ERROR", $"Client {currentEmpId}: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(currentEmpId))
                {
                    _activeSessions.TryRemove(currentEmpId, out _);
                }
                client.Close();
            }
        }

        public async Task<int> BroadcastMessageAsync(string message)
        {
            string cleanMsg = HrmProtocol.Sanitize(message, "Thông báo");
            int count = 0;
            foreach (var kvp in _activeSessions)
            {
                try
                {
                    await kvp.Value.SafeWriteLineAsync($"BROADCAST|[THÔNG BÁO TỪ ADMIN]: {cleanMsg}");
                    _redisService.RecordBroadcastForStaff(kvp.Key, cleanMsg);
                    count++;
                }
                catch { }
            }
            AddCustomLog("BROADCAST", $"SEND to {count} active sockets: \"BROADCAST|{cleanMsg}\"");
            return count;
        }

        private async Task AdminDashboardLoopAsync(CancellationTokenSource cts)
        {
            while (!cts.IsCancellationRequested)
            {
                Console.WriteLine("\n================ BẢNG ĐIỀU KHIỂN ADMIN (CONSOLE + WEB UI) ================");
                Console.WriteLine($" -> Giao diện Web trực quan đang chạy tại: http://localhost:{HrmProtocol.WebUiPort}");
                Console.WriteLine(" [1] Xem bảng Nhân viên trên Console");
                Console.WriteLine(" [2] Gửi thông báo Broadcast tới toàn bộ Nhân viên đang Online");
                Console.WriteLine($" [3] Bật/Tắt log PING trên Console (Hiện tại: {(_showPingLogs ? "BẬT" : "TẮT")})");
                Console.WriteLine(" [0] Dừng Server và Thoát");
                Console.Write("Chọn thao tác Admin: ");

                string choice = Console.ReadLine()?.Trim();
                if (choice == "1")
                {
                    await PrintStaffTableAsync();
                }
                else if (choice == "2")
                {
                    Console.Write("Nhập nội dung thông báo gửi toàn công ty: ");
                    string msg = Console.ReadLine();
                    int sent = await BroadcastMessageAsync(msg);
                    Console.WriteLine($"-> Đã gửi tới {sent} nhân viên.");
                }
                else if (choice == "3")
                {
                    _showPingLogs = !_showPingLogs;
                }
                else if (choice == "0")
                {
                    cts.Cancel();
                    _listener?.Stop();
                    break;
                }
            }
        }

        private async Task PrintStaffTableAsync()
        {
            var list = await _redisService.GetAllStaffPresenceAsync(IsClientSocketActive);
            Console.WriteLine("\n--------------------------------------------------------------------------------------------------");
            Console.WriteLine($"{"MÃ NV",-8} | {"HỌ VÀ TÊN",-20} | {"PHÒNG BAN",-12} | {"TRẠNG THÁI",-15} | {"TTL REDIS",-10} | {"CẬP NHẬT CUỐI",-19}");
            Console.WriteLine("--------------------------------------------------------------------------------------------------");
            foreach (var s in list)
            {
                Console.ForegroundColor = s.IsOnline ? ConsoleColor.Green : ConsoleColor.DarkGray;
                string ttlText = s.IsOnline ? $"{s.RemainingTtlSeconds}s" : "Hết hạn";
                Console.WriteLine($"{s.EmpId,-8} | {s.FullName,-20} | {s.Department,-12} | {s.CurrentStatus,-15} | {ttlText,-10} | {s.LastSeen,-19}");
                Console.ResetColor();
            }
            Console.WriteLine("--------------------------------------------------------------------------------------------------");
        }

        private void LogConsole(string message, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"\n{DateTime.Now:HH:mm:ss} {message}");
            Console.ResetColor();
        }
    }
}