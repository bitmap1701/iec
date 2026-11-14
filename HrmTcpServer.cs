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
    public class HrmTcpServer
    {
        private readonly RedisPresenceService _redisService;
        private readonly int _port;
        private TcpListener _listener;
        private bool _showPingLogs = false;

        private readonly ConcurrentDictionary<string, StreamWriter> _activeWriters = new();
        private readonly ConcurrentQueue<TcpLogEntry> _tcpLogs = new();

        public HrmTcpServer(RedisPresenceService redisService, int port = HrmProtocol.DefaultPort)
        {
            _redisService = redisService;
            _port = port;
        }

        public bool IsClientSocketActive(string empId) => _activeWriters.ContainsKey(empId);

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

        public void StartBackgroundListener(CancellationToken token)
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            AddCustomLog("SERVER", $"TcpListener started on 0.0.0.0:{_port} (Ready for Windows & macOS Clients)");
            _ = Task.Run(() => AcceptClientsLoopAsync(token), token);
        }

        public async Task RunAsync(CancellationTokenSource cts = null)
        {
            cts ??= new CancellationTokenSource();
            if (_listener == null)
            {
                StartBackgroundListener(cts.Token);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[TCP SERVER] Äang láº¯ng nghe táº¡i cá»•ng {_port}...");
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
                using StreamReader reader = new(stream, Encoding.UTF8);
                using StreamWriter writer = new(stream, Encoding.UTF8) { AutoFlush = true };

                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    string[] parts = line.Split('|');
                    string cmd = parts[0].ToUpperInvariant();

                    if (cmd == HrmProtocol.CmdLogin && parts.Length >= 4)
                    {
                        currentEmpId = parts[1].Trim().ToUpperInvariant();
                        string fullName = parts[2].Trim();
                        string dept = parts[3].Trim();

                        _activeWriters[currentEmpId] = writer;
                        await _redisService.RegisterLoginAsync(currentEmpId, fullName, dept, clientEndpoint);

                        AddCustomLog("LOGIN", $"RECV: {line} -> HSET staff:profile:{currentEmpId} & SETEX staff:online:{currentEmpId} 15s");
                        await writer.WriteLineAsync($"LOGIN_OK|ÄÄƒng nháº­p thÃ nh cÃ´ng mÃ£ {currentEmpId}. TTL Redis = {HrmProtocol.OnlineTtlSeconds}s");
                    }
                    else if (cmd == HrmProtocol.CmdPing && parts.Length >= 2)
                    {
                        string empId = parts[1].Trim().ToUpperInvariant();
                        await _redisService.RefreshHeartbeatAsync(empId);
                        AddCustomLog("PING", $"RECV: {line} -> EXPIRE staff:online:{empId} 15s | SEND: PONG");
                        if (_showPingLogs)
                        {
                            LogConsole($"[PING] Nháº­n nhá»‹p tim tá»« {empId} -> Gia háº¡n TTL 15s", ConsoleColor.DarkGray);
                        }
                        await writer.WriteLineAsync("PONG");
                    }
                    else if (cmd == HrmProtocol.CmdStatus && parts.Length >= 3)
                    {
                        string empId = parts[1].Trim().ToUpperInvariant();
                        string newStatus = parts[2].Trim();
                        await _redisService.UpdateStatusAsync(empId, newStatus);

                        AddCustomLog("STATUS", $"RECV: {line} -> SETEX staff:online:{empId} 15 \"{newStatus}\"");
                        await writer.WriteLineAsync($"STATUS_OK|ÄÃ£ cáº­p nháº­t tráº¡ng thÃ¡i trÃªn Redis: {newStatus}");
                    }
                    else if (cmd == HrmProtocol.CmdLogout && parts.Length >= 2)
                    {
                        string empId = parts[1].Trim().ToUpperInvariant();
                        await _redisService.SetOfflineImmediatelyAsync(empId);
                        AddCustomLog("LOGOUT", $"RECV: {line} -> DEL staff:online:{empId} (Offline ngay láº­p tá»©c)");
                        await writer.WriteLineAsync("BYE");
                        break;
                    }
                }
            }
            catch (IOException)
            {
                AddCustomLog("DROP", $"Socket {currentEmpId ?? clientEndpoint} ngáº¯t Ä‘á»™t ngá»™t! Chá» Redis TTL tá»± há»§y khÃ³a...");
            }
            catch (Exception ex)
            {
                AddCustomLog("ERROR", $"Client {currentEmpId}: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(currentEmpId))
                {
                    _activeWriters.TryRemove(currentEmpId, out _);
                }
                client.Close();
            }
        }

        public async Task<int> BroadcastMessageAsync(string message)
        {
            int count = 0;
            foreach (var kvp in _activeWriters)
            {
                try
                {
                    await kvp.Value.WriteLineAsync($"BROADCAST|[THÃ”NG BÃO Tá»ª ADMIN]: {message}");
                    _redisService.RecordBroadcastForStaff(kvp.Key, message);
                    count++;
                }
                catch { }
            }
            AddCustomLog("BROADCAST", $"SEND to {count} active sockets: \"BROADCAST|{message}\"");
            return count;
        }

        private async Task AdminDashboardLoopAsync(CancellationTokenSource cts)
        {
            while (!cts.IsCancellationRequested)
            {
                Console.WriteLine("\n================ Báº¢NG ÄIá»€U KHIá»‚N ADMIN (CONSOLE + WEB UI) ================");
                Console.WriteLine($" -> Giao diá»‡n Web trá»±c quan Ä‘ang cháº¡y táº¡i: http://localhost:{HrmProtocol.WebUiPort}");
                Console.WriteLine(" [1] Xem báº£ng NhÃ¢n viÃªn trÃªn Console");
                Console.WriteLine(" [2] Gá»­i thÃ´ng bÃ¡o Broadcast tá»›i toÃ n bá»™ NhÃ¢n viÃªn Ä‘ang Online");
                Console.WriteLine($" [3] Báº­t/Táº¯t log PING trÃªn Console (Hiá»‡n táº¡i: {(_showPingLogs ? "Báº¬T" : "Táº®T")})");
                Console.WriteLine(" [0] Dá»«ng Server vÃ  ThoÃ¡t");
                Console.Write("Chá»n thao tÃ¡c Admin: ");

                string choice = Console.ReadLine()?.Trim();
                if (choice == "1")
                {
                    await PrintStaffTableAsync();
                }
                else if (choice == "2")
                {
                    Console.Write("Nháº­p ná»™i dung thÃ´ng bÃ¡o gá»­i toÃ n cÃ´ng ty: ");
                    string msg = Console.ReadLine();
                    int sent = await BroadcastMessageAsync(msg);
                    Console.WriteLine($"-> ÄÃ£ gá»­i tá»›i {sent} nhÃ¢n viÃªn.");
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
            Console.WriteLine($"{"MÃƒ NV",-8} | {"Há»Œ VÃ€ TÃŠN",-20} | {"PHÃ’NG BAN",-12} | {"TRáº NG THÃI",-15} | {"TTL REDIS",-10} | {"Cáº¬P NHáº¬T CUá»I",-19}");
            Console.WriteLine("--------------------------------------------------------------------------------------------------");
            foreach (var s in list)
            {
                Console.ForegroundColor = s.IsOnline ? ConsoleColor.Green : ConsoleColor.DarkGray;
                string ttlText = s.IsOnline ? $"{s.RemainingTtlSeconds}s" : "Háº¿t háº¡n";
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
