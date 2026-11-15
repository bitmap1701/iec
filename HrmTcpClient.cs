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

        // Khóa đồng bộ đảm bảo luồng Heartbeat 5s và luồng bấm nút Menu không ghi đè nhau trên NetworkStream
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public HrmTcpClient(string serverIp, int serverPort = HrmProtocol.DefaultPort)
        {
            _serverIp = serverIp;
            _serverPort = serverPort;
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
                    await SafeSendLineAsync($"{HrmProtocol.CmdPing}|{_empId}");
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
                    if (line == "PONG")
                    {
                        _pingCount++;
                        continue;
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
                Console.WriteLine($"\n--- CỬA SỔ NHÂN VIÊN: {_empId} ---");
                Console.WriteLine(" [1] Đổi trạng thái sang: Online (Đang làm việc)");
                Console.WriteLine(" [2] Đổi trạng thái sang: Busy (Đang bận / Họp)");
                Console.WriteLine(" [3] Đổi trạng thái sang: Away (Tạm rời máy)");
                Console.WriteLine(" [4] Xem số nhịp tim (Heartbeat PING/PONG) đã gửi thành công");
                Console.WriteLine(" [0] Đăng xuất chuẩn (LOGOUT) | (Hoặc tắt ngang cửa sổ để test TTL 15s)");
                Console.Write("Chọn thao tác: ");

                string choice = Console.ReadLine()?.Trim();
                if (choice == "1")
                {
                    await SafeSendLineAsync($"{HrmProtocol.CmdStatus}|{_empId}|Online");
                }
                else if (choice == "2")
                {
                    await SafeSendLineAsync($"{HrmProtocol.CmdStatus}|{_empId}|Busy");
                }
                else if (choice == "3")
                {
                    await SafeSendLineAsync($"{HrmProtocol.CmdStatus}|{_empId}|Away");
                }
                else if (choice == "4")
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"-> Đã duy trì {_pingCount} nhịp tim PING/PONG thành công với Server.");
                    Console.ResetColor();
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