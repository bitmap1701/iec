using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iec
{
    public class HrmTcpClient
    {
        private readonly string _serverIp;
        private readonly int _serverPort;
        private TcpClient _client;
        private StreamReader _reader;
        private StreamWriter _writer;
        private string _empId;
        private int _pingCount = 0;

        public HrmTcpClient(string serverIp, int serverPort = HrmProtocol.DefaultPort)
        {
            _serverIp = serverIp;
            _serverPort = serverPort;
        }

        public async Task StartClientInteractiveAsync()
        {
            Console.Write("Nháº­p MÃ£ NhÃ¢n ViÃªn (VD: NV01): ");
            _empId = Console.ReadLine()?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(_empId)) _empId = "NV01";

            Console.Write("Nháº­p Há» vÃ  TÃªn (VD: Huynh Nhat Hoa): ");
            string fullName = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(fullName)) fullName = "Nhan Vien Test";

            Console.Write("Nháº­p PhÃ²ng Ban (VD: IT): ");
            string dept = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(dept)) dept = "IT";

            try
            {
                _client = new TcpClient();
                Console.WriteLine($"Äang káº¿t ná»‘i TCP tá»›i {_serverIp}:{_serverPort}...");
                await _client.ConnectAsync(_serverIp, _serverPort);

                NetworkStream stream = _client.GetStream();
                _reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                // Gá»­i gÃ³i tin LOGIN Ä‘áº§u tiÃªn
                await _writer.WriteLineAsync($"{HrmProtocol.CmdLogin}|{_empId}|{fullName}|{dept}");

                using var cts = new CancellationTokenSource();

                // 1. Luá»“ng láº¯ng nghe pháº£n há»“i & thÃ´ng bÃ¡o tá»« Server
                _ = Task.Run(() => ListenServerAsync(cts.Token));

                // 2. Luá»“ng tá»± Ä‘á»™ng báº¯n nhá»‹p tim PING má»—i 5 giÃ¢y
                _ = Task.Run(() => HeartbeatLoopAsync(cts.Token));

                // 3. Menu thao tÃ¡c cá»§a NhÃ¢n viÃªn
                await EmployeeMenuLoopAsync(cts);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Lá»–I Káº¾T Ná»I TCP] KhÃ´ng thá»ƒ káº¿t ná»‘i tá»›i Server ({_serverIp}:{_serverPort}): {ex.Message}");
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
                    if (_client != null && _client.Connected)
                    {
                        await _writer.WriteLineAsync($"{HrmProtocol.CmdPing}|{_empId}");
                    }
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
                    if (line == "PONG")
                    {
                        _pingCount++;
                        continue; // áº¨n PONG Ä‘á»ƒ khÃ´ng lÃ m trÃ´i mÃ n hÃ¬nh menu cá»§a nhÃ¢n viÃªn
                    }

                    if (line.StartsWith("BROADCAST|"))
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"\n{line.Substring("BROADCAST|".Length)}");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"\n[PHáº¢N Há»’I SERVER] {line}");
                        Console.ResetColor();
                    }
                }
            }
            catch
            {
                if (!token.IsCancellationRequested)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("\n[Cáº¢NH BÃO] ÄÃ£ máº¥t káº¿t ná»‘i TCP vá»›i Server!");
                    Console.ResetColor();
                }
            }
        }

        private async Task EmployeeMenuLoopAsync(CancellationTokenSource cts)
        {
            while (!cts.IsCancellationRequested)
            {
                Console.WriteLine($"\n--- Cá»¬A Sá»” NHÃ‚N VIÃŠN: {_empId} ---");
                Console.WriteLine(" [1] Äá»•i tráº¡ng thÃ¡i sang: Online (Äang lÃ m viá»‡c)");
                Console.WriteLine(" [2] Äá»•i tráº¡ng thÃ¡i sang: Busy (Äang báº­n / Há»p)");
                Console.WriteLine(" [3] Äá»•i tráº¡ng thÃ¡i sang: Away (Táº¡m rá»i mÃ¡y)");
                Console.WriteLine(" [4] Xem sá»‘ nhá»‹p tim (Heartbeat PING/PONG) Ä‘Ã£ gá»­i thÃ nh cÃ´ng");
                Console.WriteLine(" [0] ÄÄƒng xuáº¥t chuáº©n (LOGOUT) | (Hoáº·c táº¯t ngang cá»­a sá»• Ä‘á»ƒ test TTL 15s)");
                Console.Write("Chá»n thao tÃ¡c: ");

                string choice = Console.ReadLine()?.Trim();
                if (choice == "1")
                {
                    await _writer.WriteLineAsync($"{HrmProtocol.CmdStatus}|{_empId}|Online");
                }
                else if (choice == "2")
                {
                    await _writer.WriteLineAsync($"{HrmProtocol.CmdStatus}|{_empId}|Busy");
                }
                else if (choice == "3")
                {
                    await _writer.WriteLineAsync($"{HrmProtocol.CmdStatus}|{_empId}|Away");
                }
                else if (choice == "4")
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"-> ÄÃ£ duy trÃ¬ {_pingCount} nhá»‹p tim PING/PONG thÃ nh cÃ´ng vá»›i Server.");
                    Console.ResetColor();
                }
                else if (choice == "0")
                {
                    await _writer.WriteLineAsync($"{HrmProtocol.CmdLogout}|{_empId}");
                    await Task.Delay(300);
                    cts.Cancel();
                    _client.Close();
                    break;
                }
            }
        }
    }
}
