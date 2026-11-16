using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
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
    // Một TcpClient thật do Web Cockpit điều khiển: mọi nút bấm trên web đều đi qua NetworkStream tới cổng TCP của Server
    public class ManagedSimClient
    {
        public TcpClient Client { get; set; }
        public StreamWriter Writer { get; set; }
        public CancellationTokenSource Cts { get; set; }
        public double? LastRttMs { get; set; }
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        // Cts bị hủy = client đã "chết" (bị đóng hoặc giả lập rớt mạng): không được gửi thêm gì nữa
        public bool IsConnected => Client != null && Client.Connected && Cts?.IsCancellationRequested != true;

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

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[WEB UI READY] Đã mở giao diện quản trị tại: http://localhost:{_httpPort}");
            Console.ResetColor();

            OpenBrowser($"http://localhost:{_httpPort}");
            _ = Task.Run(() => HandleHttpRequestsLoopAsync(token), token);
            return Task.CompletedTask;
        }

        public async Task ConnectRealTcpClientAsync(string empId, string fullName, string dept)
        {
            empId = HrmProtocol.Sanitize(empId, "NV01").ToUpperInvariant();
            fullName = HrmProtocol.Sanitize(fullName, "Nhân viên");
            dept = HrmProtocol.Sanitize(dept, "IT");

            CloseSimClient(empId);

            var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", _tcpServer.Port);
            var stream = tcp.GetStream();
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var reader = new StreamReader(stream, utf8NoBom);
            var writer = new StreamWriter(stream, utf8NoBom) { AutoFlush = true };
            var cts = new CancellationTokenSource();

            var sim = new ManagedSimClient { Client = tcp, Writer = writer, Cts = cts };
            _simClients[empId] = sim;

            await sim.SafeSendAsync($"{HrmProtocol.CmdLogin}|{empId}|{fullName}|{dept}");

            _ = Task.Run(async () =>
            {
                try
                {
                    string line;
                    while (!cts.Token.IsCancellationRequested && (line = await reader.ReadLineAsync()) != null)
                    {
                        line = line.Trim('﻿', '\r', '\n');
                        if (line.StartsWith("PONG|"))
                        {
                            string[] p = line.Split('|');
                            if (p.Length >= 2 && long.TryParse(p[1], out long sentTick))
                            {
                                sim.LastRttMs = Stopwatch.GetElapsedTime(sentTick).TotalMilliseconds;
                            }
                        }
                        else if (line.StartsWith("ERR|RATE_LIMIT_BLOCKED"))
                        {
                            sim.Cts.Cancel();
                            sim.Client.Close();
                            break;
                        }
                    }
                }
                catch { }
            }, cts.Token);

            _ = Task.Run(async () =>
            {
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(HrmProtocol.HeartbeatIntervalMs));
                    while (await timer.WaitForNextTickAsync(cts.Token))
                    {
                        string rtt = sim.LastRttMs.HasValue ? "|" + sim.LastRttMs.Value.ToString("0.00", CultureInfo.InvariantCulture) : "";
                        await sim.SafeSendAsync($"{HrmProtocol.CmdPing}|{empId}|{Stopwatch.GetTimestamp()}{rtt}");
                    }
                }
                catch { }
            }, cts.Token);
        }

        private async Task SendIfConnectedAsync(string empId, string payload)
        {
            if (_simClients.TryGetValue(empId, out var sim) && sim.IsConnected)
            {
                await sim.SafeSendAsync(payload);
            }
        }

        public Task SendChatOrDmRealTcpAsync(string fromId, string targetId, string message)
        {
            message = HrmProtocol.Sanitize(message, "");
            targetId = HrmProtocol.Sanitize(targetId, "ALL").ToUpperInvariant();
            if (message == "") return Task.CompletedTask;
            return SendIfConnectedAsync(fromId, targetId == "ALL"
                ? $"{HrmProtocol.CmdChat}|{fromId}|{message}"
                : $"{HrmProtocol.CmdDm}|{fromId}|{targetId}|{message}");
        }

        // Bắn 30 dòng lệnh liên tiếp trên Socket thật để kích hoạt Redis Rate-Limiter.
        // Dùng STATUS thay vì PING để đợt spam không cộng điểm uptime.
        public async Task TriggerFloodAttackRealTcpAsync(string empId)
        {
            for (int i = 0; i < 30; i++)
            {
                await SendIfConnectedAsync(empId, $"{HrmProtocol.CmdStatus}|{empId}|Online");
            }
        }

        public async Task LogoutRealTcpAsync(string empId)
        {
            await SendIfConnectedAsync(empId, $"{HrmProtocol.CmdLogout}|{empId}");
            await Task.Delay(150);
            CloseSimClient(empId);
        }

        private void CloseSimClient(string empId)
        {
            if (_simClients.TryRemove(empId, out var sim))
            {
                try
                {
                    sim.Cts.Cancel();
                    sim.Client.Close();
                }
                catch { }
            }
        }

        // Giả lập mất mạng/sập nguồn thật: ngừng gửi PING nhưng KHÔNG đóng socket (không có FIN/RST),
        // nên Server chỉ phát hiện được nhờ hạn đọc 15s.
        public void FreezeSocketWithoutClose(string empId)
        {
            if (_simClients.TryGetValue(empId, out var sim)) sim.Cts.Cancel();
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
                    var chats = await _redisService.GetRecentChatHistoryAsync(25);
                    var payload = new
                    {
                        redis = _redisService.Endpoint,
                        lanIp = $"{HrmTcpServer.GetLocalLanIp()}:{_tcpServer.Port}",
                        staff,
                        chats,
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
                    string empId = root.TryGetProperty("empId", out var idProp) ? idProp.GetString() : null;

                    if (action == "connect")
                    {
                        await ConnectRealTcpClientAsync(empId,
                            root.GetProperty("fullName").GetString(),
                            root.GetProperty("dept").GetString());
                    }
                    else if (action == "status")
                    {
                        await SendIfConnectedAsync(empId, $"{HrmProtocol.CmdStatus}|{empId}|{HrmProtocol.Sanitize(root.GetProperty("status").GetString(), "Online")}");
                    }
                    else if (action == "chat")
                    {
                        await SendChatOrDmRealTcpAsync(empId,
                            root.GetProperty("target").GetString(),
                            root.GetProperty("message").GetString());
                    }
                    else if (action == "flood")
                    {
                        await TriggerFloodAttackRealTcpAsync(empId);
                    }
                    else if (action == "kill")
                    {
                        FreezeSocketWithoutClose(empId);
                        _tcpServer.AddCustomLog("DROP", $"[HALF-OPEN] {empId} ngừng gửi PING nhưng socket không đóng. Chờ hạn đọc {HrmProtocol.OnlineTtlSeconds}s của Server...");
                    }
                    else if (action == "logout")
                    {
                        await LogoutRealTcpAsync(empId);
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
                    byte[] htmlBytes = Encoding.UTF8.GetBytes(DashboardHtml);
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

        // Không tải gì từ Internet (CSS/font tự viết) để demo được trong mạng LAN không có Internet.
        // Mọi chuỗi do client gửi lên đều đi qua esc() trước khi chèn vào trang.
        private const string DashboardHtml = """
<!DOCTYPE html>
<html lang="vi">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>IEC Cockpit</title>
<style>
  :root { color-scheme:dark; --bg:#0b1018; --panel:#131b27; --line:#243044; --text:#e6edf5; --dim:#8a99ad;
          --ok:#34d399; --warn:#fbbf24; --info:#38bdf8; --bad:#fb7185; --accent:#818cf8; }
  * { box-sizing:border-box; }
  body { margin:0; padding:16px; background:var(--bg); color:var(--text);
         font:13px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
  .mono, code, td.id, .log, .kpi b { font-family:ui-monospace, "SF Mono", Consolas, monospace; }
  h1 { font-size:16px; margin:0; } h2 { font-size:12px; margin:0; color:var(--dim); text-transform:uppercase; letter-spacing:.05em; }
  header, .panel { background:var(--panel); border:1px solid var(--line); border-radius:10px; }
  header { padding:12px 16px; display:flex; flex-wrap:wrap; gap:8px 24px; justify-content:space-between; align-items:center; }
  header .dim { color:var(--dim); font-size:12px; } header code { color:var(--ok); }
  main { display:grid; grid-template-columns:minmax(0,2fr) minmax(280px,1fr); gap:16px; margin-top:16px; }
  @media (max-width:900px) { main { grid-template-columns:1fr; } }
  .col { display:flex; flex-direction:column; gap:16px; min-width:0; }
  .panel { overflow:hidden; } .panel > h2 { padding:10px 14px; border-bottom:1px solid var(--line); display:flex; justify-content:space-between; }
  .pad { padding:12px 14px; display:flex; flex-direction:column; gap:10px; }
  .kpis { display:grid; grid-template-columns:repeat(4,1fr); gap:12px; }
  .kpi { background:var(--panel); border:1px solid var(--line); border-radius:10px; padding:10px 12px; color:var(--dim); font-size:12px; }
  .kpi b { display:block; font-size:22px; color:var(--text); }
  table { width:100%; border-collapse:collapse; } .scroll { overflow-x:auto; }
  th { text-align:left; font-size:11px; color:var(--dim); font-weight:600; padding:8px 12px; border-bottom:1px solid var(--line); white-space:nowrap; }
  td { padding:8px 12px; border-bottom:1px solid var(--line); vertical-align:middle; }
  tbody tr { cursor:pointer; } tbody tr:hover { background:#1a2433; } tbody tr.sel { background:#1d2540; }
  td.id { color:var(--info); font-weight:600; } td .sub { color:var(--dim); font-size:11px; } td.num { text-align:right; }
  .badge { display:inline-block; padding:1px 8px; border-radius:99px; font-size:11px; border:1px solid currentColor; white-space:nowrap; }
  .on { color:var(--ok); } .busy { color:var(--warn); } .away { color:var(--info); } .off { color:var(--dim); } .blocked { color:var(--bad); font-weight:700; }
  .ttl { display:flex; align-items:center; gap:8px; min-width:110px; }
  .bar { flex:1; height:6px; background:var(--line); border-radius:3px; overflow:hidden; }
  .bar i { display:block; height:100%; background:var(--ok); transition:width .3s; } .bar i.low { background:var(--bad); }
  .log { height:220px; overflow-y:auto; padding:10px 14px; font-size:11px; background:#080c13; }
  .log div { margin-bottom:4px; overflow-wrap:anywhere; } .log .t { color:var(--dim); } .log .tag { font-weight:600; color:var(--info); }
  .tag.PING { color:var(--ok); } .tag.DROP, .tag.ERROR, .tag.SECOPS { color:var(--bad); }
  .tag.BROADCAST, .tag.STATUS { color:var(--warn); } .tag.PUBSUB, .tag.CHAT, .tag.DM, .tag.UDP-9001 { color:var(--accent); }
  .two { display:grid; grid-template-columns:1fr 1fr; gap:16px; } @media (max-width:900px) { .two { grid-template-columns:1fr; } }
  input, select, button { font:inherit; color:var(--text); background:#0b1018; border:1px solid var(--line); border-radius:6px; padding:6px 10px; min-width:0; }
  input:focus, select:focus, button:focus-visible { outline:2px solid var(--accent); outline-offset:1px; }
  button { cursor:pointer; background:#1b2637; font-weight:600; } button:hover { background:#243247; }
  button.primary { background:#4f46e5; border-color:#4f46e5; } button.primary:hover { background:#6366f1; }
  button.danger { color:var(--bad); } button.block { width:100%; text-align:left; display:flex; justify-content:space-between; gap:8px; }
  button.block small { color:var(--dim); font-weight:400; }
  .row { display:flex; gap:8px; } .row > input { flex:1; } .grid3 { display:grid; grid-template-columns:repeat(3,1fr); gap:8px; }
  label { color:var(--dim); font-size:11px; display:block; margin-bottom:4px; }
  dl { margin:0; display:grid; grid-template-columns:auto 1fr; gap:4px 12px; font-size:12px; } dt { color:var(--dim); } dd { margin:0; text-align:right; overflow-wrap:anywhere; }
  .empty { color:var(--dim); padding:16px; text-align:center; }
</style>
</head>
<body>
<header>
  <div>
    <h1>IEC Presence Cockpit</h1>
    <div class="dim">Lập trình mạng — TCP Socket, UDP Discovery và Redis (TTL, Pub/Sub, Rate-Limiter)</div>
  </div>
  <div class="dim">Server TCP <code id="lanIp">-</code> · Redis <code id="redis">-</code> · Giới hạn 25 dòng lệnh/10s</div>
</header>

<main>
  <div class="col">
    <div class="kpis">
      <div class="kpi">Nhân sự (SET)<b id="kpiTotal">0</b></div>
      <div class="kpi">Online (TTL 15s)<b id="kpiOnline" class="on">0</b></div>
      <div class="kpi">Bị khóa spam<b id="kpiBlocked" class="blocked">0</b></div>
      <div class="kpi">Tin đã lưu (LIST)<b id="kpiChats">0</b></div>
    </div>

    <div class="panel"><div class="pad"><div class="row">
      <input id="broadcastInput" placeholder="Thông báo Admin gửi tới mọi nhân viên (PUBLISH lên Redis)…" aria-label="Nội dung thông báo Admin">
      <button class="primary" onclick="sendBroadcast()">Phát thông báo</button>
    </div></div></div>

    <div class="panel">
      <h2><span>Nhân sự</span><span>Xếp theo uptime (ZSET)</span></h2>
      <div class="scroll"><table>
        <thead><tr><th>Mã NV</th><th>Họ tên · Phòng · IP</th><th>Trạng thái</th><th>TTL khóa online</th><th>RTT</th><th style="text-align:right">Uptime</th></tr></thead>
        <tbody id="staffBody"></tbody>
      </table></div>
      <div id="staffEmpty" class="empty">Chưa có nhân viên nào. Thêm một kết nối ở khung bên phải, hoặc chạy client console (chọn [2]).</div>
    </div>

    <div class="two">
      <div class="panel">
        <h2><span>Nhật ký gói tin</span><button onclick="apiAction({action:'clear_logs'})">Xóa</button></h2>
        <div id="logBox" class="log"></div>
      </div>
      <div class="panel">
        <h2><span>Lịch sử chat (Redis LIST)</span><span>50 tin gần nhất</span></h2>
        <div id="chatBox" class="log"></div>
      </div>
    </div>
  </div>

  <div class="col">
    <div class="panel">
      <h2><span>Điều khiển TcpClient</span><span id="sockBadge" class="badge off">-</span></h2>
      <div class="pad">
        <div><label for="empSelect">Nhân viên đang chọn</label><select id="empSelect" style="width:100%" onchange="selectedId=this.value; render()"></select></div>
        <dl class="mono">
          <dt>Khóa Redis</dt><dd id="simKey">-</dd>
          <dt>Số PING · RTT</dt><dd id="simPings">-</dd>
          <dt>Tin nhận cuối</dt><dd id="simLast">-</dd>
        </dl>

        <div><label>Đổi trạng thái (STATUS)</label><div class="grid3">
          <button class="on" onclick="act('status',{status:'Online'})">Online</button>
          <button class="busy" onclick="act('status',{status:'Busy'})">Busy</button>
          <button class="away" onclick="act('status',{status:'Away'})">Away</button>
        </div></div>

        <div><label for="chatMessage">Chat chung hoặc nhắn riêng (CHAT / DM)</label><div class="row">
          <input id="chatTarget" placeholder="ALL / NV02" style="flex:0 0 96px" aria-label="Người nhận">
          <input id="chatMessage" placeholder="Nội dung…">
          <button class="primary" onclick="sendChat()">Gửi</button>
        </div></div>

        <div><label>Kịch bản kiểm thử</label>
          <div style="display:flex;flex-direction:column;gap:8px">
            <button class="block danger" onclick="act('kill')"><span>A. Rớt mạng: im lặng, không FIN</span><small>Server ngắt sau 15s</small></button>
            <button class="block danger" onclick="act('flood')"><span>B. Spam 30 dòng lệnh</span><small>Khóa 30s</small></button>
            <button class="block" onclick="act('logout')"><span>C. Đăng xuất chuẩn (LOGOUT)</span><small>Offline ngay</small></button>
            <button class="block" onclick="reconnect()"><span>Kết nối lại TCP</span><small>LOGIN</small></button>
          </div>
        </div>

        <div><label>Mở kết nối nhân viên mới</label>
          <div class="grid3" style="margin-bottom:8px">
            <input id="newId" placeholder="NV01" aria-label="Mã nhân viên">
            <input id="newName" placeholder="Họ tên" aria-label="Họ tên">
            <input id="newDept" placeholder="Phòng" aria-label="Phòng ban">
          </div>
          <button style="width:100%" onclick="addStaff()">+ Mở TcpClient mới</button>
        </div>
      </div>
    </div>
  </div>
</main>

<script>
  const $ = id => document.getElementById(id);
  // Chữ do client gửi lên (tên, tin nhắn, log) là dữ liệu không tin cậy: luôn escape trước khi chèn vào HTML
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  let staff = [], chats = [], selectedId = '', lastOptions = '';

  async function apiAction(payload) {
    await fetch('/api/action', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(payload) });
    await refresh();
  }
  function act(action, extra) { if (selectedId) apiAction({ action, empId:selectedId, ...extra }); }

  async function refresh() {
    try {
      const d = await (await fetch('/api/state')).json();
      $('redis').textContent = d.redis; $('lanIp').textContent = d.lanIp;
      staff = d.staff || []; chats = d.chats || [];
      render();
      $('logBox').innerHTML = (d.logs || []).map(l =>
        `<div><span class="t">${esc(l.Time)}</span> <span class="tag ${esc(l.Tag)}">[${esc(l.Tag)}]</span> ${esc(l.Message)}</div>`).join('');
      $('chatBox').innerHTML = chats.map(c =>
        `<div><span class="t">${esc(c.Time)}</span> <span class="tag">${esc(c.Sender)} → ${esc(c.Target)}:</span> ${esc(c.Content)}</div>`).join('');
    } catch (e) {}
  }

  function badge(s) {
    if (s.IsRateLimited) return `<span class="badge blocked">BLOCKED ${s.BlockedRemainingSeconds}s</span>`;
    if (!s.IsOnline) return `<span class="badge off">Offline</span>`;
    const cls = s.CurrentStatus === 'Online' ? 'on' : s.CurrentStatus === 'Busy' ? 'busy' : 'away';
    return `<span class="badge ${cls}">● ${esc(s.CurrentStatus)}</span>`;
  }
  const rtt = s => s.IsOnline && s.RttMs != null ? s.RttMs.toFixed(2) + ' ms' : '-';

  function render() {
    $('kpiTotal').textContent = staff.length;
    $('kpiOnline').textContent = staff.filter(s => s.IsOnline).length;
    $('kpiBlocked').textContent = staff.filter(s => s.IsRateLimited).length;
    $('kpiChats').textContent = chats.length;
    if (!staff.some(s => s.EmpId === selectedId)) selectedId = staff.length ? staff[0].EmpId : '';

    // Chỉ dựng lại <select> khi danh sách đổi, để không đóng menu đang mở mỗi giây
    const options = staff.map(s => `<option value="${esc(s.EmpId)}">${esc(s.EmpId)} — ${esc(s.FullName)}</option>`).join('');
    if (options !== lastOptions) { $('empSelect').innerHTML = options; lastOptions = options; }
    $('empSelect').value = selectedId;

    $('staffEmpty').hidden = staff.length > 0;
    $('staffBody').innerHTML = staff.map(s => {
      const pct = Math.max(0, Math.min(100, s.RemainingTtlSeconds / 15 * 100));
      return `<tr data-id="${esc(s.EmpId)}" class="${s.EmpId === selectedId ? 'sel' : ''}">
        <td class="id">${esc(s.EmpId)}</td>
        <td>${esc(s.FullName)}<div class="sub">${esc(s.Department)} · ${esc(s.ClientIp)}</div></td>
        <td>${badge(s)}</td>
        <td><div class="ttl"><div class="bar"><i class="${s.RemainingTtlSeconds <= 6 ? 'low' : ''}" style="width:${pct}%"></i></div><span class="mono">${s.IsOnline ? s.RemainingTtlSeconds : 0}s</span></div></td>
        <td class="mono">${rtt(s)}</td>
        <td class="mono num">${s.UptimeSeconds}s</td>
      </tr>`;
    }).join('');

    const cur = staff.find(s => s.EmpId === selectedId);
    $('simKey').textContent = cur ? `staff:online:${cur.EmpId}` : '-';
    $('simPings').textContent = cur ? `${cur.PingCount} · ${rtt(cur)}` : '-';
    $('simLast').textContent = cur && cur.LastBroadcast || '-';
    const b = $('sockBadge');
    b.textContent = !cur ? '-' : cur.IsRateLimited ? 'Bị khóa' : cur.IsSocketConnected ? 'Socket mở' : 'Socket đóng';
    b.className = 'badge ' + (!cur ? 'off' : cur.IsRateLimited ? 'blocked' : cur.IsSocketConnected ? 'on' : 'off');
  }

  $('staffBody').addEventListener('click', e => {
    const tr = e.target.closest('tr[data-id]');
    if (tr) { selectedId = tr.dataset.id; render(); }
  });

  function sendChat() {
    const msg = $('chatMessage').value.trim();
    if (!msg) return;
    act('chat', { target: ($('chatTarget').value.trim() || 'ALL').toUpperCase(), message: msg });
    $('chatMessage').value = '';
  }
  function sendBroadcast() {
    const msg = $('broadcastInput').value.trim();
    if (!msg) return;
    apiAction({ action:'broadcast', message: msg });
    $('broadcastInput').value = '';
  }
  function reconnect() {
    const cur = staff.find(s => s.EmpId === selectedId);
    if (cur) apiAction({ action:'connect', empId:cur.EmpId, fullName:cur.FullName, dept:cur.Department });
  }
  function addStaff() {
    const id = $('newId').value.trim().toUpperCase();
    if (!id) { $('newId').focus(); return; }
    selectedId = id;
    apiAction({ action:'connect', empId:id, fullName:$('newName').value.trim() || id, dept:$('newDept').value.trim() || 'IT' });
    $('newId').value = $('newName').value = $('newDept').value = '';
  }
  $('chatMessage').addEventListener('keydown', e => { if (e.key === 'Enter') sendChat(); });
  $('broadcastInput').addEventListener('keydown', e => { if (e.key === 'Enter') sendBroadcast(); });

  setInterval(refresh, 1000);
  refresh();
</script>
</body>
</html>
""";
    }
}
