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

        // Trả false thay vì ném lỗi: socket hỏng của người nhận không được làm sập handler của người gửi
        public async Task<bool> SafeWriteLineAsync(string line)
        {
            await _writeLock.WaitAsync();
            try
            {
                if (!Client.Connected) return false;
                await Writer.WriteLineAsync(line);
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return false;
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

        public int Port => _port;

        public bool IsClientSocketActive(string empId) =>
            _activeSessions.TryGetValue(empId, out var s) && s.Client.Connected;

        // Đóng socket của một nhân viên đang nối vào Server này (dùng khi Admin xóa nhân viên)
        public void KickSession(string empId)
        {
            if (_activeSessions.TryGetValue(empId, out var s)) s.Client.Close();
        }

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

                _redisService.SubscribeChat(DeliverChatToLocalSocketsAsync);
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
            catch (SocketException ex)
            {
                // Thường gặp khi chạy Server thứ hai trên cùng máy: cổng UDP đã thuộc về Server đầu tiên
                AddCustomLog("UDP-9001", $"Không mở được cổng UDP Discovery: {ex.Message}");
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

        // StreamReader.ReadLineAsync không giới hạn độ dài: một client gửi mãi không xuống dòng sẽ ăn hết RAM server.
        // Trả null khi EOF.
        private static async Task<string> ReadLineLimitedAsync(StreamReader reader, CancellationToken token)
        {
            var sb = new StringBuilder();
            var buf = new char[1];
            while (true)
            {
                int n = await reader.ReadAsync(buf.AsMemory(), token);
                if (n == 0) return sb.Length > 0 ? sb.ToString() : null;
                if (buf[0] == '\n') return sb.ToString();
                if (sb.Length >= HrmProtocol.MaxLineLength)
                    throw new InvalidDataException($"Dòng vượt quá {HrmProtocol.MaxLineLength} ký tự");
                sb.Append(buf[0]);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            string currentEmpId = null;
            ClientSession session = null;
            string clientEndpoint = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";

            try
            {
                using NetworkStream stream = client.GetStream();
                var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                using StreamReader reader = new(stream, utf8NoBom);
                using StreamWriter writer = new(stream, utf8NoBom) { AutoFlush = true };

                session = new ClientSession(client, writer);

                while (true)
                {
                    // Client mất mạng/sập nguồn không gửi được FIN: socket vẫn "mở" nhưng im lặng mãi (half-open).
                    // Hạn đọc = TTL: quá 3 nhịp tim không có dòng nào thì server tự đóng.
                    using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(HrmProtocol.OnlineTtlSeconds));
                    string line = await ReadLineLimitedAsync(reader, readCts.Token);
                    if (line == null)
                    {
                        AddCustomLog("DROP", $"Socket {currentEmpId ?? clientEndpoint} đóng (EOF) không qua LOGOUT -> Offline ngay");
                        break;
                    }

                    line = line.Trim('﻿', '\r', '\n');
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string[] parts = line.Split('|');
                    string cmd = parts[0].ToUpperInvariant();
                    string claimedId = parts.Length >= 2 ? HrmProtocol.Sanitize(parts[1], "").ToUpperInvariant() : "";

                    if (currentEmpId == null && (cmd != HrmProtocol.CmdLogin || parts.Length < 4 || claimedId is "" or "ALL" or "ADMIN"))
                    {
                        AddCustomLog("SECOPS", $"{clientEndpoint} gửi {cmd} khi chưa LOGIN -> đóng Socket");
                        await session.SafeWriteLineAsync("ERR|NOT_LOGGED_IN|Phải gửi LOGIN|<Id>|<Name>|<Dept> trước khi dùng lệnh khác.");
                        break;
                    }

                    // Mã NV gắn với kết nối từ lúc LOGIN; trước đó chỉ có mã client tự khai trong dòng LOGIN
                    string empId = currentEmpId ?? claimedId;

                    // Kiểm tra tường lửa chống Spam gói tin (Redis Fixed Window Rate-Limiter)
                    var rateCheck = await _redisService.CheckAndIncrementRateLimitAsync(empId);
                    if (!rateCheck.Allowed)
                    {
                        AddCustomLog("SECOPS", $"[RATE-LIMIT BLOCKED] {empId} vượt quá {HrmProtocol.MaxPacketsPerWindow} gói/10s! Khóa Socket {rateCheck.BlockedSecondsLeft}s.");
                        await session.SafeWriteLineAsync($"ERR|RATE_LIMIT_BLOCKED|Tài khoản {empId} bị khóa {rateCheck.BlockedSecondsLeft}s do gửi spam gói tin TCP!");
                        break;
                    }

                    // Không tin mã NV client tự khai ở các dòng sau LOGIN: chặn gửi thay người khác
                    if (currentEmpId != null && (cmd == HrmProtocol.CmdLogin || claimedId != currentEmpId))
                    {
                        AddCustomLog("SECOPS", $"{currentEmpId} gửi \"{cmd}|{claimedId}\" sai mã phiên -> từ chối");
                        await session.SafeWriteLineAsync($"ERR|ID_MISMATCH|Kết nối này đã đăng nhập là {currentEmpId}.");
                        continue;
                    }

                    if (cmd == HrmProtocol.CmdLogin)
                    {
                        currentEmpId = empId;
                        string fullName = HrmProtocol.Sanitize(parts[2], "Nhân viên");
                        string dept = HrmProtocol.Sanitize(parts[3], "IT");

                        // Đăng nhập lại cùng mã: đóng socket phiên cũ để nó không còn gửi được dưới mã này
                        _activeSessions.AddOrUpdate(empId, session, (_, old) => { old.Client.Close(); return session; });
                        await _redisService.RegisterLoginAsync(empId, fullName, dept, clientEndpoint);

                        AddCustomLog("LOGIN", $"RECV: {line} -> SADD, HSET & SETEX staff:online:{empId} 15s");
                        await session.SafeWriteLineAsync($"LOGIN_OK|Đăng nhập thành công mã {empId}. TTL Redis = {HrmProtocol.OnlineTtlSeconds}s");

                        // Phát lại 3 tin gần nhất mà người này được phép thấy: tin chung, hoặc DM gửi/nhận của chính họ
                        var recentChats = (await _redisService.GetRecentChatHistoryAsync(50))
                            .Where(c => c.Target == "ALL" || c.Target == empId || c.Sender == empId)
                            .Take(3).Reverse();
                        foreach (var c in recentChats)
                        {
                            await session.SafeWriteLineAsync($"REPLAY|[{c.Time}] {c.Sender} -> {c.Target}: {c.Content}");
                        }
                    }
                    else if (cmd == HrmProtocol.CmdPing)
                    {
                        // Trường 3 là dấu thời gian riêng của client, Server chỉ gửi trả nguyên văn để client tự tính RTT
                        string echo = parts.Length >= 3 ? HrmProtocol.Sanitize(parts[2], "0") : "0";
                        double? clientRtt = parts.Length >= 4 && double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double r) ? r : null;
                        string rttText = clientRtt.HasValue ? $"{clientRtt:0.00}ms" : "chưa đo";

                        // Trả PONG trước khi gọi Redis để RTT client đo được là độ trễ mạng, không lẫn thời gian xử lý
                        await session.SafeWriteLineAsync($"PONG|{echo}");
                        await _redisService.RefreshHeartbeatAsync(empId, clientRtt);
                        AddCustomLog("PING", $"RECV: PING|{empId} (RTT {rttText}) -> EXPIRE 15s & ZINCRBY +5s");
                        if (_showPingLogs)
                        {
                            LogConsole($"[PING] {empId} (RTT {rttText}) -> Gia hạn TTL 15s", ConsoleColor.DarkGray);
                        }
                    }
                    else if (cmd == HrmProtocol.CmdStatus && parts.Length >= 3)
                    {
                        string newStatus = HrmProtocol.Sanitize(parts[2], "Online");
                        await _redisService.UpdateStatusAsync(empId, newStatus);
                        AddCustomLog("STATUS", $"RECV: {line} -> SETEX staff:online:{empId} 15 \"{newStatus}\"");
                        await session.SafeWriteLineAsync($"STATUS_OK|Đã cập nhật trạng thái trên Redis: {newStatus}");
                    }
                    else if (cmd == HrmProtocol.CmdChat && parts.Length >= 3)
                    {
                        string msg = HrmProtocol.Sanitize(parts[2], "");
                        await _redisService.PublishChatAsync(empId, "ALL", msg);
                        AddCustomLog("CHAT", $"LPUSH + PUBLISH \"{empId}: {msg}\"");
                    }
                    else if (cmd == HrmProtocol.CmdDm && parts.Length >= 4)
                    {
                        string toId = HrmProtocol.Sanitize(parts[2], "NV01").ToUpperInvariant();
                        string msg = HrmProtocol.Sanitize(parts[3], "");

                        // Chỉ xếp hàng tin cho người có thật: mã lạ thì báo lỗi ngay, không lưu vào lịch sử
                        if (!await _redisService.IsKnownStaffAsync(toId))
                        {
                            AddCustomLog("DM", $"{empId} nhắn riêng tới mã không tồn tại \"{toId}\" -> từ chối");
                            await session.SafeWriteLineAsync($"ERR|UNKNOWN_USER|Không có nhân viên nào mang mã {toId}.");
                            continue;
                        }

                        await _redisService.PublishChatAsync(empId, toId, msg);
                        AddCustomLog("DM", $"LPUSH + PUBLISH {empId} -> {toId}: \"{msg}\"");

                        // Người nhận có thể đang nối vào một Server IEC khác, nên hỏi Redis chứ không tra bảng socket cục bộ
                        if (await _redisService.IsOnlineAsync(toId))
                            await session.SafeWriteLineAsync($"DM_OK|Đã chuyển tin nhắn riêng tới {toId}.");
                        else
                            await session.SafeWriteLineAsync($"DM_QUEUED|{toId} hiện Offline. Tin nhắn đã lưu vào Redis LIST để phát lại khi {toId} đăng nhập.");
                    }
                    else if (cmd == HrmProtocol.CmdLogout)
                    {
                        AddCustomLog("LOGOUT", $"RECV: {line} -> DEL staff:online:{empId} (Offline ngay lập tức)");
                        await session.SafeWriteLineAsync("BYE");
                        break;
                    }
                    else
                    {
                        await session.SafeWriteLineAsync($"ERR|BAD_COMMAND|Lệnh không hợp lệ hoặc thiếu tham số: {cmd}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                AddCustomLog("DROP", $"[HALF-OPEN] {currentEmpId ?? clientEndpoint} im lặng quá {HrmProtocol.OnlineTtlSeconds}s (không FIN/RST) -> Server chủ động đóng Socket");
            }
            catch (InvalidDataException ex)
            {
                AddCustomLog("SECOPS", $"{currentEmpId ?? clientEndpoint}: {ex.Message} -> đóng Socket");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                AddCustomLog("DROP", $"Socket {currentEmpId ?? clientEndpoint} ngắt đột ngột (RST, hoặc bị phiên đăng nhập mới cùng mã thay thế)");
            }
            catch (Exception ex)
            {
                AddCustomLog("ERROR", $"Client {currentEmpId}: {ex.Message}");
            }
            finally
            {
                // Chỉ dọn khi bảng phiên vẫn trỏ tới chính kết nối này; nếu cùng mã NV đã đăng nhập lại
                // trên socket mới thì không được xóa phiên mới hay đặt nó Offline.
                if (currentEmpId != null &&
                    _activeSessions.TryRemove(new KeyValuePair<string, ClientSession>(currentEmpId, session)))
                {
                    try { await _redisService.SetOfflineImmediatelyAsync(currentEmpId); } catch { }
                }
                client.Close();
            }
        }

        public async Task BroadcastMessageAsync(string message)
        {
            string cleanMsg = HrmProtocol.Sanitize(message, "Thông báo");
            await _redisService.PublishChatAsync("ADMIN", "ALL", cleanMsg);
            AddCustomLog("BROADCAST", $"LPUSH + PUBLISH thông báo Admin: \"{cleanMsg}\"");
        }

        // Chạy mỗi khi Redis đẩy một tin từ kênh Pub/Sub về (kể cả tin do chính Server này PUBLISH):
        // chuyển tiếp xuống các socket đang nối vào Server này.
        private async Task DeliverChatToLocalSocketsAsync(ChatMessageEntry m)
        {
            string text = m.Sender == "ADMIN" ? $"[THÔNG BÁO TỪ ADMIN]: {m.Content}"
                        : m.Target == "ALL" ? $"[CHAT TỪ {m.Sender}]: {m.Content}"
                        : $"[TIN NHẮN RIÊNG TỪ {m.Sender}]: {m.Content}";

            int sent = 0;
            foreach (var kvp in _activeSessions)
            {
                if (m.Target != "ALL" && kvp.Key != m.Target) continue;
                if (await kvp.Value.SafeWriteLineAsync($"BROADCAST|{text}"))
                {
                    _redisService.RecordBroadcastForStaff(kvp.Key, text);
                    sent++;
                }
            }
            AddCustomLog("PUBSUB", $"RECV từ Redis: {m.Sender} -> {m.Target} | đẩy xuống {sent} socket của Server :{_port}");
        }

        private async Task AdminDashboardLoopAsync(CancellationTokenSource cts)
        {
            while (!cts.IsCancellationRequested)
            {
                Console.WriteLine("\n================ BẢNG ĐIỀU KHIỂN ADMIN (CONSOLE + WEB UI) ================");
                Console.WriteLine($" -> Giao diện Web Cockpit: http://localhost:{HrmProtocol.WebPortFor(_port)} | IP LAN: {GetLocalLanIp()}:{_port}");
                Console.WriteLine(" [1] Gửi thông báo Broadcast tới toàn bộ Nhân viên đang Online");
                Console.WriteLine($" [2] Bật/Tắt log PING trên Console (Hiện tại: {(_showPingLogs ? "BẬT" : "TẮT")})");
                Console.WriteLine(" [0] Dừng Server và Thoát");
                Console.Write("Chọn thao tác Admin: ");

                string choice = Console.ReadLine()?.Trim();
                if (choice == "1")
                {
                    Console.Write("Nhập nội dung thông báo gửi toàn công ty: ");
                    await BroadcastMessageAsync(Console.ReadLine());
                    Console.WriteLine("-> Đã PUBLISH lên Redis và lưu vào Redis LIST.");
                }
                else if (choice == "2")
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

        private void LogConsole(string message, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"\n{DateTime.Now:HH:mm:ss} {message}");
            Console.ResetColor();
        }
    }
}