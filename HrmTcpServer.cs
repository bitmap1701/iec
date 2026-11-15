using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iec
{
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
        private UdpClient _udpDiscoveryServer;
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

        public List<TcpLogEntry> GetRecentLogs() => _tcpLogs.ToArray().Reverse().Take(70).ToList();

        public void ClearLogs() => _tcpLogs.Clear();

        public void AddCustomLog(string tag, string message)
        {
            _tcpLogs.Enqueue(new TcpLogEntry
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Tag = tag,
                Message = message
            });
            while (_tcpLogs.Count > 120) _tcpLogs.TryDequeue(out _);
        }

        public bool StartBackgroundListener(CancellationToken token)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
                string lanIp = GetLocalLanIp();
                AddCustomLog("SERVER", $"TcpListener started on {lanIp}:{_port} | UDP Discovery on :{HrmProtocol.UdpDiscoveryPort}");

                _ = Task.Run(() => AcceptClientsLoopAsync(token), token);
                _ = Task.Run(() => StartUdpDiscoveryResponderAsync(token), token);
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

        // Mở cổng UDP 9001 để tự động trả lời IP của Server khi máy Client quét mạng LAN
        private async Task StartUdpDiscoveryResponderAsync(CancellationToken token)
        {
            try
            {
                _udpDiscoveryServer = new UdpClient(HrmProtocol.UdpDiscoveryPort);
                while (!token.IsCancellationRequested)
                {
                    var result = await _udpDiscoveryServer.ReceiveAsync(token);
                    string req = Encoding.UTF8.GetString(result.Buffer).Trim();
                    if (req.StartsWith(HrmProtocol.UdpDiscoverRequest))
                    {
                        string serverIp = GetLocalLanIp();
                        string reply = $"{HrmProtocol.UdpDiscoverResponse}|{serverIp}|{_port}";
                        byte[] bytes = Encoding.UTF8.GetBytes(reply);
                        await _udpDiscoveryServer.SendAsync(bytes, bytes.Length, result.RemoteEndPoint);
                        AddCustomLog("UDP-9001", $"RECV UDP Broadcast from {result.RemoteEndPoint.Address} -> REPLY: {reply}");
                    }
                }
            }
            catch { }
        }

        public static string GetLocalLanIp()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.Description.Contains("Radmin", StringComparison.OrdinalIgnoreCase) ||
                        ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var props = ni.GetIPProperties();
                    if (props.GatewayAddresses.Count == 0) continue;

                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            return ua.Address.ToString();
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        public async Task RunAsync(CancellationTokenSource cts = null)
        {
            cts ??= new CancellationTokenSource();
            if (_listener == null)
            {
                if (!StartBackgroundListener(cts.Token)) return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[TCP SERVER] Đang lắng nghe tại {GetLocalLanIp()}:{_port} (UDP Auto-Discovery :{HrmProtocol.UdpDiscoveryPort})...");
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
                    string candidateId = parts.Length >= 2 ? HrmProtocol.Sanitize(parts[1], "NV01").ToUpperInvariant() : (currentEmpId ?? "UNKNOWN");

                    // Kiểm tra tường lửa chống Spam gói tin (Redis Sliding/Fixed Window Rate-Limiter)
                    var rateCheck = await _redisService.CheckAndIncrementRateLimitAsync(candidateId);
                    if (!rateCheck.Allowed)
                    {
                        AddCustomLog("SECOPS", $"[RATE-LIMIT BLOCKED] {candidateId} vượt quá {HrmProtocol.MaxPacketsPerWindow} gói/10s! Khóa Socket {rateCheck.BlockedSecondsLeft}s.");
                        await session.SafeWriteLineAsync($"ERR|RATE_LIMIT_BLOCKED|Tài khoản {candidateId} bị khóa {rateCheck.BlockedSecondsLeft}s do gửi spam gói tin TCP!");
                        break;
                    }

                    if (cmd == HrmProtocol.CmdLogin && parts.Length >= 4)
                    {
                        currentEmpId = candidateId;
                        string fullName = HrmProtocol.Sanitize(parts[2], "Nhân viên");
                        string dept = HrmProtocol.Sanitize(parts[3], "IT");
                        string task = parts.Length >= 5 ? HrmProtocol.Sanitize(parts[4], "Đang trực ca") : "Đang trực ca";

                        _activeSessions[currentEmpId] = session;
                        await _redisService.RegisterLoginAsync(currentEmpId, fullName, dept, clientEndpoint, task);

                        AddCustomLog("LOGIN", $"RECV: {line} -> SADD, HSET & SETEX staff:online:{currentEmpId} 15s");
                        await session.SafeWriteLineAsync($"LOGIN_OK|Đăng nhập thành công mã {currentEmpId}. TTL Redis = {HrmProtocol.OnlineTtlSeconds}s");

                        // Phát lại 3 tin nhắn gần nhất từ Redis LIST (Offline Message Replay)
                        var recentChats = await _redisService.GetRecentChatHistoryAsync(3);
                        recentChats.Reverse();
                        foreach (var c in recentChats)
                        {
                            await session.SafeWriteLineAsync($"REPLAY|[{c.Time}] {c.Sender} -> {c.Target}: {c.Content}");
                        }
                    }
                    else if (cmd == HrmProtocol.CmdPing && parts.Length >= 2)
                    {
                        string empId = candidateId;
                        currentEmpId ??= empId;
                        _activeSessions[empId] = session;

                        long sentTick = parts.Length >= 3 && long.TryParse(parts[2], out long t) ? t : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        double clientMeasuredRtt = parts.Length >= 4 && double.TryParse(parts[3], out double r) ? r : 1.1;

                        await _redisService.RefreshHeartbeatAsync(empId, clientEndpoint, clientMeasuredRtt);
                        AddCustomLog("PING", $"RECV: PING|{empId} (RTT {clientMeasuredRtt:0.0}ms) -> EXPIRE 15s & ZINCRBY +5s");
                        if (_showPingLogs)
                        {
                            LogConsole($"[PING] {empId} (RTT {clientMeasuredRtt:0.0}ms) -> Gia hạn TTL 15s", ConsoleColor.DarkGray);
                        }
                        await session.SafeWriteLineAsync($"PONG|{sentTick}");
                    }
                    else if (cmd == HrmProtocol.CmdStatus && parts.Length >= 3)
                    {
                        string empId = candidateId;
                        string newStatus = HrmProtocol.Sanitize(parts[2], "Online");
                        currentEmpId ??= empId;
                        _activeSessions[empId] = session;

                        await _redisService.UpdateStatusAsync(empId, newStatus);
                        AddCustomLog("STATUS", $"RECV: {line} -> SETEX staff:online:{empId} 15 \"{newStatus}\"");
                        await session.SafeWriteLineAsync($"STATUS_OK|Đã cập nhật trạng thái trên Redis: {newStatus}");
                    }
                    else if (cmd == HrmProtocol.CmdTask && parts.Length >= 3)
                    {
                        string empId = candidateId;
                        string taskInfo = HrmProtocol.Sanitize(parts[2], "Đang trực ca");
                        await _redisService.UpdateTaskAsync(empId, taskInfo);
                        await _redisService.AppendChatMessageAsync(empId, "TASK-BOARD", $"Cập nhật công việc: {taskInfo}");
                        AddCustomLog("TASK", $"RECV: {line} -> HSET staff:profile:{empId} CurrentTask=\"{taskInfo}\"");
                        await session.SafeWriteLineAsync($"TASK_OK|Đã lưu tiến độ công việc vào Redis Hash: {taskInfo}");
                    }
                    else if (cmd == HrmProtocol.CmdChat && parts.Length >= 3)
                    {
                        string empId = candidateId;
                        string msg = HrmProtocol.Sanitize(parts[2], "");
                        await _redisService.AppendChatMessageAsync(empId, "ALL", msg);
                        AddCustomLog("CHAT", $"LPUSH staff:chat:history \"{empId}: {msg}\" & Broadcast to {_activeSessions.Count} sockets");

                        foreach (var kvp in _activeSessions)
                        {
                            await kvp.Value.SafeWriteLineAsync($"BROADCAST|[CHAT TỪ {empId}]: {msg}");
                            _redisService.RecordBroadcastForStaff(kvp.Key, $"{empId}: {msg}");
                        }
                    }
                    else if (cmd == HrmProtocol.CmdDm && parts.Length >= 4)
                    {
                        string fromId = candidateId;
                        string toId = HrmProtocol.Sanitize(parts[2], "NV01").ToUpperInvariant();
                        string msg = HrmProtocol.Sanitize(parts[3], "");

                        await _redisService.AppendChatMessageAsync(fromId, toId, $"[DM] {msg}");
                        AddCustomLog("DM", $"UNICAST {fromId} -> {toId}: \"{msg}\" (Saved to Redis LIST)");

                        if (_activeSessions.TryGetValue(toId, out var targetSession))
                        {
                            await targetSession.SafeWriteLineAsync($"BROADCAST|[TIN NHẮN RIÊNG TỪ {fromId}]: {msg}");
                            _redisService.RecordBroadcastForStaff(toId, $"[DM từ {fromId}]: {msg}");
                            await session.SafeWriteLineAsync($"DM_OK|Đã chuyển phát tin nhắn riêng tới Socket của {toId}.");
                        }
                        else
                        {
                            await session.SafeWriteLineAsync($"DM_QUEUED|{toId} hiện Offline. Tin nhắn đã lưu vào Redis LIST để phát lại khi {toId} đăng nhập.");
                        }
                    }
                    else if (cmd == HrmProtocol.CmdLogout && parts.Length >= 2)
                    {
                        string empId = candidateId;
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
            await _redisService.AppendChatMessageAsync("ADMIN", "ALL", cleanMsg);
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
            AddCustomLog("BROADCAST", $"SEND to {count} sockets & LPUSH Redis LIST: \"{cleanMsg}\"");
            return count;
        }

        private async Task AdminDashboardLoopAsync(CancellationTokenSource cts)
        {
            while (!cts.IsCancellationRequested)
            {
                Console.WriteLine("\n================ BẢNG ĐIỀU KHIỂN ADMIN (CONSOLE + WEB UI) ================");
                Console.WriteLine($" -> Giao diện Web Cockpit: http://localhost:{HrmProtocol.WebUiPort} | IP LAN: {GetLocalLanIp()}:{_port}");
                Console.WriteLine(" [1] Xem bảng Nhân viên & Xếp hạng Uptime (Redis ZSET) trên Console");
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
                    Console.WriteLine($"-> Đã gửi tới {sent} nhân viên và lưu vào Redis LIST.");
                }
                else if (choice == "3")
                {
                    _showPingLogs = !_showPingLogs;
                }
                else if (choice == "0")
                {
                    cts.Cancel();
                    _listener?.Stop();
                    _udpDiscoveryServer?.Close();
                    break;
                }
            }
        }

        private async Task PrintStaffTableAsync()
        {
            var list = await _redisService.GetAllStaffPresenceAsync(IsClientSocketActive);
            Console.WriteLine("\n----------------------------------------------------------------------------------------------------------------------------");
            Console.WriteLine($"{"MÃ NV",-7} | {"HỌ VÀ TÊN",-18} | {"TRẠNG THÁI",-10} | {"TTL",-6} | {"RTT",-7} | {"UPTIME (ZSET)",-13} | {"TASK ĐANG LÀM (REDIS HASH)",-26}");
            Console.WriteLine("----------------------------------------------------------------------------------------------------------------------------");
            foreach (var s in list)
            {
                Console.ForegroundColor = s.IsRateLimited ? ConsoleColor.Red : (s.IsOnline ? ConsoleColor.Green : ConsoleColor.DarkGray);
                string ttlText = s.IsOnline ? $"{s.RemainingTtlSeconds}s" : "0s";
                Console.WriteLine($"{s.EmpId,-7} | {s.FullName,-18} | {s.CurrentStatus,-10} | {ttlText,-6} | {s.RttMs + "ms",-7} | {s.UptimeSeconds + "s",-13} | {s.CurrentTask,-26}");
                Console.ResetColor();
            }
            Console.WriteLine("----------------------------------------------------------------------------------------------------------------------------");
        }

        private void LogConsole(string message, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"\n{DateTime.Now:HH:mm:ss} {message}");
            Console.ResetColor();
        }
    }
}