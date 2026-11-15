using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace iec
{
    public class ManagedSimClient
    {
        public string EmpId { get; set; }
        public string FullName { get; set; }
        public string Department { get; set; }
        public TcpClient Client { get; set; }
        public StreamWriter Writer { get; set; }
        public CancellationTokenSource Cts { get; set; }
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        public bool IsConnected => Client != null && Client.Connected;

        public async Task SafeSendAsync(string payload)
        {
            await _writeLock.WaitAsync();
            try
            {
                if (IsConnected && Writer != null)
                {
                    await Writer.WriteLineAsync(payload);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }

    public class WebDashboardHost
    {
        private readonly RedisPresenceService _redisService;
        private readonly HrmTcpServer _tcpServer;
        private readonly int _httpPort;
        private HttpListener _httpListener;

        private readonly ConcurrentDictionary<string, ManagedSimClient> _simClients = new();

        public WebDashboardHost(RedisPresenceService redisService, HrmTcpServer tcpServer, int httpPort = HrmProtocol.WebUiPort)
        {
            _redisService = redisService;
            _tcpServer = tcpServer;
            _httpPort = httpPort;
        }

        public Task StartAsync(CancellationToken token)
        {
            try
            {
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://localhost:{_httpPort}/");
                _httpListener.Start();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[CẢNH BÁO WEB UI] Không thể mở cổng HTTP {_httpPort}: {ex.Message}");
                Console.ResetColor();
                return Task.CompletedTask;
            }

            // Khởi tạo sẵn 3 kết nối TCP Client mẫu thật để bảng Dashboard có dữ liệu ngay khi mở lên
            _ = Task.Run(async () =>
            {
                await Task.Delay(400, token);
                await ConnectRealTcpClientAsync("NV01", "Huỳnh Nhật Hoà", "Core .NET");
                await ConnectRealTcpClientAsync("NV02", "Lương Nhật Khôi", "macOS QA");
                await ConnectRealTcpClientAsync("NV03", "Trần Minh Tuấn", "Network Ops");
                await SendStatusRealTcpAsync("NV03", "Busy");
            }, token);

            OpenBrowser($"http://localhost:{_httpPort}");
            _ = Task.Run(() => HandleHttpRequestsLoopAsync(token), token);
            return Task.CompletedTask;
        }

        public async Task ConnectRealTcpClientAsync(string empId, string fullName, string dept)
        {
            empId = HrmProtocol.Sanitize(empId, "NV01").ToUpperInvariant();
            fullName = HrmProtocol.Sanitize(fullName, "Nhân viên");
            dept = HrmProtocol.Sanitize(dept, "IT");

            await KillSocketWithoutLogoutAsync(empId);

            var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", HrmProtocol.DefaultPort);
            var stream = tcp.GetStream();
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var reader = new StreamReader(stream, utf8NoBom);
            var writer = new StreamWriter(stream, utf8NoBom) { AutoFlush = true };
            var cts = new CancellationTokenSource();

            var sim = new ManagedSimClient
            {
                EmpId = empId,
                FullName = fullName,
                Department = dept,
                Client = tcp,
                Writer = writer,
                Cts = cts
            };
            _simClients[empId] = sim;

            await sim.SafeSendAsync($"{HrmProtocol.CmdLogin}|{empId}|{fullName}|{dept}");

            // Luồng đọc phản hồi PONG & BROADCAST từ NetworkStream để giải phóng bộ đệm nhận TCP
            _ = Task.Run(async () =>
            {
                try
                {
                    string line;
                    while (!cts.Token.IsCancellationRequested && (line = await reader.ReadLineAsync()) != null)
                    {
                        line = line.Trim('\uFEFF', '\r', '\n');
                        if (line.StartsWith("BROADCAST|"))
                        {
                            string msg = line.Substring("BROADCAST|".Length);
                            _redisService.RecordBroadcastForStaff(empId, msg);
                        }
                    }
                }
                catch { }
            }, cts.Token);

            // Vòng lặp bắn gói tin PING thật mỗi 5 giây qua TCP Socket
            _ = Task.Run(async () =>
            {
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(HrmProtocol.HeartbeatIntervalMs));
                    while (await timer.WaitForNextTickAsync(cts.Token))
                    {
                        await sim.SafeSendAsync($"{HrmProtocol.CmdPing}|{empId}");
                    }
                }
                catch { }
            }, cts.Token);
        }

        public async Task SendStatusRealTcpAsync(string empId, string status)
        {
            if (_simClients.TryGetValue(empId, out var sim) && sim.IsConnected)
            {
                await sim.SafeSendAsync($"{HrmProtocol.CmdStatus}|{empId}|{status}");
            }
        }

        public async Task LogoutRealTcpAsync(string empId)
        {
            if (_simClients.TryRemove(empId, out var sim))
            {
                try
                {
                    if (sim.IsConnected)
                    {
                        await sim.SafeSendAsync($"{HrmProtocol.CmdLogout}|{empId}");
                        await Task.Delay(150);
                    }
                    sim.Cts.Cancel();
                    sim.Client.Close();
                }
                catch { }
            }
            else
            {
                await _redisService.SetOfflineImmediatelyAsync(empId);
            }
        }

        public Task KillSocketWithoutLogoutAsync(string empId)
        {
            if (_simClients.TryRemove(empId, out var sim))
            {
                try
                {
                    sim.Cts.Cancel();
                    sim.Client.Close(); // Đóng ngang TCP Socket không gửi LOGOUT để test Redis TTL 15s
                }
                catch { }
            }
            return Task.CompletedTask;
        }

        private async Task HandleHttpRequestsLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _httpListener != null && _httpListener.IsListening)
            {
                try
                {
                    var ctx = await _httpListener.GetContextAsync();
                    _ = Task.Run(() => ProcessRequestAsync(ctx), token);
                }
                catch { }
            }
        }

        private async Task ProcessRequestAsync(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            var resp = ctx.Response;

            try
            {
                if (path == "/api/state")
                {
                    var staff = await _redisService.GetAllStaffPresenceAsync(_tcpServer.IsClientSocketActive);
                    var payload = new
                    {
                        redisMode = _redisService.IsUsingFallbackMemory ? "In-Memory Redis Engine (Fallback)" : "Redis Server 6379 (Live)",
                        staff,
                        logs = _tcpServer.GetRecentLogs()
                    };
                    byte[] buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                    resp.ContentType = "application/json; charset=utf-8";
                    await resp.OutputStream.WriteAsync(buf);
                }
                else if (path == "/api/action" && ctx.Request.HttpMethod == "POST")
                {
                    using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    string body = await sr.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    string action = root.GetProperty("action").GetString();

                    if (action == "connect")
                    {
                        await ConnectRealTcpClientAsync(
                            root.GetProperty("empId").GetString(),
                            root.GetProperty("fullName").GetString(),
                            root.GetProperty("dept").GetString());
                    }
                    else if (action == "status")
                    {
                        await SendStatusRealTcpAsync(
                            root.GetProperty("empId").GetString(),
                            root.GetProperty("status").GetString());
                    }
                    else if (action == "kill")
                    {
                        string id = root.GetProperty("empId").GetString();
                        await KillSocketWithoutLogoutAsync(id);
                        _tcpServer.AddCustomLog("DROP", $"[KILL SOCKET] Đã ngắt ngang TcpClient của {id}. Đang đếm ngược Redis TTL 15s...");
                    }
                    else if (action == "logout")
                    {
                        await LogoutRealTcpAsync(root.GetProperty("empId").GetString());
                    }
                    else if (action == "broadcast")
                    {
                        await _tcpServer.BroadcastMessageAsync(root.GetProperty("message").GetString());
                    }
                    else if (action == "clear_logs")
                    {
                        _tcpServer.ClearLogs();
                    }

                    byte[] ok = Encoding.UTF8.GetBytes("{\"ok\":true}");
                    resp.ContentType = "application/json";
                    await resp.OutputStream.WriteAsync(ok);
                }
                else
                {
                    byte[] htmlBytes = Encoding.UTF8.GetBytes(GetDashboardHtml());
                    resp.ContentType = "text/html; charset=utf-8";
                    await resp.OutputStream.WriteAsync(htmlBytes);
                }
            }
            catch (Exception ex)
            {
                byte[] err = Encoding.UTF8.GetBytes(ex.Message);
                resp.StatusCode = 500;
                await resp.OutputStream.WriteAsync(err);
            }
            finally
            {
                resp.Close();
            }
        }

        private void OpenBrowser(string url)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    Process.Start("open", url);
                else
                    Process.Start("xdg-open", url);
            }
            catch { }
        }

        private string GetDashboardHtml() => @"<!DOCTYPE html>
<html lang='vi' class='dark'>
<head>
  <meta charset='UTF-8'>
  <title>IEC — C# .NET 9 TCP Socket & Redis Cockpit</title>
  <script src='https://cdn.tailwindcss.com'></script>
  <link href='https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500;600&display=swap' rel='stylesheet'>
  <style>
    body { font-family: 'Inter', sans-serif; background-color: #090d16; color: #f1f5f9; }
    .mono { font-family: 'JetBrains Mono', monospace; }
  </style>
</head>
<body class='min-h-screen flex flex-col p-4 gap-4'>
  <header class='bg-[#111827] border border-slate-800 rounded-xl px-5 py-3 flex flex-wrap items-center justify-between gap-4'>
    <div class='flex items-center gap-3'>
      <span class='w-3 h-3 rounded-full bg-emerald-400 animate-ping'></span>
      <div>
        <div class='flex items-center gap-2'>
          <h1 class='font-bold text-base text-white'>IEC Presence Monitor</h1>
          <span class='text-[11px] mono px-2 py-0.5 rounded bg-cyan-500/10 text-cyan-400 border border-cyan-500/30'>.NET 9 LIVE TCP SOCKET</span>
          <span id='redisModeBadge' class='text-[11px] mono px-2 py-0.5 rounded bg-rose-500/10 text-rose-400 border border-rose-500/30'>Redis</span>
        </div>
        <p class='text-xs text-slate-400'>Đồ án Lập trình mạng (IEC_CNTT) • Real C# TcpListener (:9000) &amp; StackExchange.Redis</p>
      </div>
    </div>
    <div class='text-xs mono text-slate-300 bg-slate-900 px-3 py-1.5 rounded-lg border border-slate-800'>
      TCP Server: <span class='text-emerald-400 font-semibold'>0.0.0.0:9000</span> | Heartbeat: <span class='text-cyan-400'>5s</span> | TTL: <span class='text-amber-400'>15s</span>
    </div>
  </header>

  <main class='grid grid-cols-1 lg:grid-cols-12 gap-4 flex-1'>
    <section class='lg:col-span-8 flex flex-col gap-4'>
      <div class='grid grid-cols-4 gap-3'>
        <div class='bg-[#111827] border border-slate-800 rounded-xl p-3.5'>
          <span class='text-xs text-slate-400'>Tổng Nhân sự (SET)</span>
          <div id='kpiTotal' class='text-2xl font-bold mono mt-1'>0</div>
        </div>
        <div class='bg-[#111827] border border-emerald-900/50 rounded-xl p-3.5'>
          <span class='text-xs text-emerald-400'>Đang Online (TTL &gt; 0)</span>
          <div id='kpiOnline' class='text-2xl font-bold mono text-emerald-400 mt-1'>0</div>
        </div>
        <div class='bg-[#111827] border border-amber-900/50 rounded-xl p-3.5'>
          <span class='text-xs text-amber-400'>Busy / Away</span>
          <div id='kpiBusy' class='text-2xl font-bold mono text-amber-400 mt-1'>0</div>
        </div>
        <div class='bg-[#111827] border border-slate-800 rounded-xl p-3.5'>
          <span class='text-xs text-slate-400'>Đã Offline (Hết TTL)</span>
          <div id='kpiOffline' class='text-2xl font-bold mono text-slate-300 mt-1'>0</div>
        </div>
      </div>

      <div class='bg-[#111827] border border-slate-800 rounded-xl p-3 flex gap-2 items-center'>
        <input id='broadcastInput' type='text' placeholder='Nhập thông báo Broadcast gửi qua C# NetworkStream tới mọi nhân viên...' class='flex-1 bg-slate-900 border border-slate-700/80 rounded-lg px-3 py-2 text-xs text-white focus:outline-none focus:border-cyan-500'>
        <button onclick='sendBroadcast()' class='bg-cyan-600 hover:bg-cyan-500 text-white text-xs font-semibold px-4 py-2 rounded-lg transition'>Phát TCP Broadcast</button>
      </div>

      <div class='bg-[#111827] border border-slate-800 rounded-xl overflow-hidden'>
        <div class='px-4 py-3 border-b border-slate-800 flex justify-between items-center'>
          <span class='text-xs font-semibold uppercase tracking-wider text-slate-300'>Giám sát Trạng thái Nhân viên &amp; Bộ đếm ngược Redis TTL</span>
          <span class='text-[11px] mono text-slate-400'>Click vào 1 dòng để điều khiển ở cột phải</span>
        </div>
        <table class='w-full text-left border-collapse'>
          <thead>
            <tr class='border-b border-slate-800 text-[11px] mono text-slate-400 bg-slate-900/60'>
              <th class='py-2.5 px-4'>MÃ NV</th>
              <th class='py-2.5 px-4'>HỌ VÀ TÊN</th>
              <th class='py-2.5 px-4'>PHÒNG BAN</th>
              <th class='py-2.5 px-4'>TRẠNG THÁI</th>
              <th class='py-2.5 px-4 w-44'>REDIS TTL (15s)</th>
              <th class='py-2.5 px-4'>SOCKET IP</th>
              <th class='py-2.5 px-4 text-right'>CẬP NHẬT</th>
            </tr>
          </thead>
          <tbody id='staffBody' class='divide-y divide-slate-800/70 text-xs'></tbody>
        </table>
      </div>

      <div class='bg-[#111827] border border-slate-800 rounded-xl flex-1 flex flex-col overflow-hidden min-h-[210px]'>
        <div class='px-4 py-2 border-b border-slate-800 bg-slate-900/60 flex justify-between items-center'>
          <span class='text-xs font-semibold text-cyan-400 mono'>Gói tin C# TCP Socket &amp; Lệnh Redis Thực Tế</span>
          <button onclick='clearLogs()' class='text-[11px] text-slate-400 hover:text-white'>Xóa Log</button>
        </div>
        <div id='logBox' class='p-3 mono text-[11px] space-y-1.5 overflow-y-auto max-h-52 bg-[#070a12]'></div>
      </div>
    </section>

    <aside class='lg:col-span-4 flex flex-col gap-4'>
      <div class='bg-[#111827] border border-indigo-500/30 rounded-xl p-4 flex flex-col gap-3.5'>
        <div class='border-b border-slate-800 pb-2.5 flex justify-between items-center'>
          <div>
            <span class='text-[10px] mono uppercase px-2 py-0.5 rounded bg-indigo-500/20 text-indigo-300'>Real C# TcpClient Control</span>
            <h2 class='text-sm font-bold text-white mt-1'>Trình Điều Khiển Nhân Viên</h2>
          </div>
          <span id='simSocketBadge' class='text-[11px] mono px-2 py-1 rounded bg-emerald-500/10 text-emerald-400 border border-emerald-500/30'>CONNECTED</span>
        </div>

        <div>
          <label class='text-[11px] text-slate-400 block mb-1'>Chọn Nhân viên đang thao tác:</label>
          <select id='empSelect' onchange='onSelectChange()' class='w-full bg-slate-900 border border-slate-700 rounded-lg px-3 py-2 text-xs text-white mono'></select>
        </div>

        <div class='bg-slate-900/90 border border-slate-800 rounded-lg p-3 space-y-1.5 text-xs mono'>
          <div class='flex justify-between'><span class='text-slate-400'>Redis Key:</span><span id='simKey' class='text-cyan-400'>staff:online:NV01</span></div>
          <div class='flex justify-between'><span class='text-slate-400'>PING đã gửi:</span><span id='simPings' class='text-emerald-400'>0 gói</span></div>
          <div class='flex justify-between'><span class='text-slate-400'>Broadcast cuối:</span><span id='simBroadcast' class='text-amber-300 truncate max-w-[180px]'>Chưa có</span></div>
        </div>

        <div>
          <label class='text-[11px] text-slate-400 block mb-1.5'>Đổi trạng thái qua gói tin STATUS|Mã_NV|...:</label>
          <div class='grid grid-cols-3 gap-2'>
            <button onclick=""sendStatus('Online')"" class='py-2 rounded-lg text-xs font-semibold border border-emerald-500/40 bg-emerald-500/10 text-emerald-300 hover:bg-emerald-500/20'>Online</button>
            <button onclick=""sendStatus('Busy')"" class='py-2 rounded-lg text-xs font-semibold border border-amber-500/40 bg-amber-500/10 text-amber-300 hover:bg-amber-500/20'>Busy</button>
            <button onclick=""sendStatus('Away')"" class='py-2 rounded-lg text-xs font-semibold border border-sky-500/40 bg-sky-500/10 text-sky-300 hover:bg-sky-500/20'>Away</button>
          </div>
        </div>

        <div class='pt-2 border-t border-slate-800 space-y-2'>
          <label class='text-[11px] text-slate-400 block'>Kịch bản Kiểm thử Vấn đáp Môn Lập trình mạng:</label>
          <button onclick='killSocket()' class='w-full py-2.5 px-3 rounded-lg text-xs font-semibold bg-rose-500/15 hover:bg-rose-500/25 text-rose-300 border border-rose-500/30 text-left flex justify-between items-center'>
            <span>1. Giả lập Sập nguồn / Rớt mạng (Đóng Socket)</span>
            <span class='mono text-[10px] bg-rose-950 px-1.5 py-0.5 rounded'>Chờ TTL 15s</span>
          </button>
          <button onclick='logoutGraceful()' class='w-full py-2.5 px-3 rounded-lg text-xs font-semibold bg-slate-800 hover:bg-slate-700 text-slate-200 border border-slate-700 text-left flex justify-between items-center'>
            <span>2. Đăng xuất chuẩn (Gửi LOGOUT|Mã_NV)</span>
            <span class='mono text-[10px] bg-slate-900 px-1.5 py-0.5 rounded'>Xóa Key ngay</span>
          </button>
          <button onclick='reconnectSelected()' class='w-full py-2.5 px-3 rounded-lg text-xs font-semibold bg-emerald-600 hover:bg-emerald-500 text-white text-center'>
            Kết nối lại TCP &amp; Bật Heartbeat 5s
          </button>
        </div>

        <div class='pt-2 border-t border-slate-800 space-y-2'>
          <label class='text-[11px] text-slate-400 block'>Thêm kết nối Nhân viên mới (Mở TcpClient mới):</label>
          <div class='grid grid-cols-3 gap-1.5'>
            <input id='newId' placeholder='NV04' class='bg-slate-900 border border-slate-700 rounded px-2 py-1.5 text-xs mono text-white'>
            <input id='newName' placeholder='Họ tên' class='bg-slate-900 border border-slate-700 rounded px-2 py-1.5 text-xs text-white'>
            <input id='newDept' placeholder='Phòng' class='bg-slate-900 border border-slate-700 rounded px-2 py-1.5 text-xs text-white'>
          </div>
          <button onclick='addNewStaff()' class='w-full py-2 rounded-lg text-xs font-semibold bg-indigo-600 hover:bg-indigo-500 text-white'>+ Mở kết nối TCP Nhân viên mới</button>
        </div>
      </div>
    </aside>
  </main>

  <script>
    let staffList = [];
    let selectedEmpId = 'NV01';

    async function apiAction(payload) {
      await fetch('/api/action', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      await fetchState();
    }

    async function fetchState() {
      try {
        const res = await fetch('/api/state');
        const data = await res.json();
        document.getElementById('redisModeBadge').textContent = data.redisMode;
        staffList = data.staff || [];
        renderDashboard();
        renderLogs(data.logs || []);
      } catch (e) {}
    }

    function renderDashboard() {
      const total = staffList.length;
      const online = staffList.filter(s => s.IsOnline).length;
      const busy = staffList.filter(s => s.IsOnline && (s.CurrentStatus === 'Busy' || s.CurrentStatus === 'Away')).length;
      document.getElementById('kpiTotal').textContent = total;
      document.getElementById('kpiOnline').textContent = online;
      document.getElementById('kpiBusy').textContent = busy;
      document.getElementById('kpiOffline').textContent = total - online;

      const select = document.getElementById('empSelect');
      if (staffList.length > 0 && !staffList.some(s => s.EmpId === selectedEmpId)) {
        selectedEmpId = staffList[0].EmpId;
      }
      select.innerHTML = staffList.map(s =>
        `<option value='${s.EmpId}' ${s.EmpId === selectedEmpId ? 'selected' : ''}>${s.EmpId} — ${s.FullName} (${s.IsOnline ? s.CurrentStatus : 'OFFLINE'})</option>`
      ).join('');

      const tbody = document.getElementById('staffBody');
      tbody.innerHTML = staffList.map(s => {
        const pct = Math.max(0, Math.min(100, (s.RemainingTtlSeconds / 15) * 100));
        let badge = `<span class='px-2 py-0.5 rounded-full text-[11px] bg-slate-800 text-slate-400'>OFFLINE</span>`;
        if (s.IsOnline) {
          if (s.CurrentStatus === 'Online') badge = `<span class='px-2 py-0.5 rounded-full text-[11px] bg-emerald-500/15 text-emerald-400 border border-emerald-500/30'>● Online</span>`;
          else if (s.CurrentStatus === 'Busy') badge = `<span class='px-2 py-0.5 rounded-full text-[11px] bg-amber-500/15 text-amber-400 border border-amber-500/30'>● Busy</span>`;
          else badge = `<span class='px-2 py-0.5 rounded-full text-[11px] bg-sky-500/15 text-sky-400 border border-sky-500/30'>● Away</span>`;
        }
        const barColor = !s.IsOnline ? 'bg-slate-700' : (s.RemainingTtlSeconds <= 6 ? 'bg-rose-500' : 'bg-emerald-500');
        const rowBg = s.EmpId === selectedEmpId ? 'bg-indigo-950/30' : 'hover:bg-slate-800/40';
        return `<tr onclick=""selectRow('${s.EmpId}')"" class='cursor-pointer transition ${rowBg}'>
          <td class='py-2.5 px-4 mono font-semibold text-cyan-400'>${s.EmpId}</td>
          <td class='py-2.5 px-4 font-medium text-white'>${s.FullName}</td>
          <td class='py-2.5 px-4 text-slate-300'>${s.Department}</td>
          <td class='py-2.5 px-4'>${badge}</td>
          <td class='py-2.5 px-4'>
            <div class='flex items-center gap-2'>
              <div class='flex-1 h-1.5 bg-slate-800 rounded-full overflow-hidden'>
                <div class='h-full ${barColor} transition-all duration-300' style='width:${pct}%'></div>
              </div>
              <span class='mono text-[11px] w-10 text-right ${s.RemainingTtlSeconds <= 6 && s.IsOnline ? ""text-rose-400 font-bold"" : ""text-slate-400""}'>${s.IsOnline ? s.RemainingTtlSeconds + 's' : '0s'}</span>
            </div>
          </td>
          <td class='py-2.5 px-4 mono text-[11px] text-slate-400'>${s.ClientIp}</td>
          <td class='py-2.5 px-4 mono text-[11px] text-slate-400 text-right'>${s.LastSeen}</td>
        </tr>`;
      }).join('');

      const cur = staffList.find(s => s.EmpId === selectedEmpId);
      if (cur) {
        document.getElementById('simKey').textContent = `staff:online:${cur.EmpId}`;
        document.getElementById('simPings').textContent = `${cur.PingCount} gói PING`;
        document.getElementById('simBroadcast').textContent = cur.LastBroadcast || 'Chưa có';
        const b = document.getElementById('simSocketBadge');
        if (cur.IsSocketConnected) {
          b.textContent = 'SOCKET ACTIVE';
          b.className = 'text-[11px] mono px-2 py-1 rounded bg-emerald-500/10 text-emerald-400 border border-emerald-500/30';
        } else {
          b.textContent = 'SOCKET CLOSED';
          b.className = 'text-[11px] mono px-2 py-1 rounded bg-rose-500/10 text-rose-400 border border-rose-500/30';
        }
      }
    }

    function renderLogs(logs) {
      const box = document.getElementById('logBox');
      box.innerHTML = logs.map(l => {
        let color = 'text-cyan-400';
        if (l.Tag === 'PING') color = 'text-emerald-400';
        else if (l.Tag === 'DROP' || l.Tag === 'ERROR') color = 'text-rose-400';
        else if (l.Tag === 'BROADCAST' || l.Tag === 'STATUS') color = 'text-amber-400';
        return `<div class='flex gap-2'><span class='text-slate-500'>[${l.Time}]</span><span class='${color} font-semibold w-20'>[${l.Tag}]</span><span class='text-slate-300'>${l.Message}</span></div>`;
      }).join('');
    }

    function selectRow(id) { selectedEmpId = id; renderDashboard(); }
    function onSelectChange() { selectedEmpId = document.getElementById('empSelect').value; renderDashboard(); }
    function sendStatus(st) { apiAction({ action: 'status', empId: selectedEmpId, status: st }); }
    function killSocket() { apiAction({ action: 'kill', empId: selectedEmpId }); }
    function logoutGraceful() { apiAction({ action: 'logout', empId: selectedEmpId }); }
    function reconnectSelected() {
      const cur = staffList.find(s => s.EmpId === selectedEmpId);
      if (cur) apiAction({ action: 'connect', empId: cur.EmpId, fullName: cur.FullName, dept: cur.Department });
    }
    function sendBroadcast() {
      const inp = document.getElementById('broadcastInput');
      if (!inp.value.trim()) return;
      apiAction({ action: 'broadcast', message: inp.value.trim() });
      inp.value = '';
    }
    function addNewStaff() {
      const id = document.getElementById('newId').value.trim() || 'NV04';
      const name = document.getElementById('newName').value.trim() || 'Nhân viên Mới';
      const dept = document.getElementById('newDept').value.trim() || 'IT';
      selectedEmpId = id.toUpperCase();
      apiAction({ action: 'connect', empId: id, fullName: name, dept: dept });
    }
    function clearLogs() { apiAction({ action: 'clear_logs' }); }

    setInterval(fetchState, 1000);
    fetchState();
  </script>
</body>
</html>";
    }
}