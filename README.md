# IEC — Hệ thống Quản trị, Định vị Trạng thái & Giao tiếp Nhân sự Thời gian thực (TCP/UDP Socket & Redis)

> **Học phần:** Lập trình mạng (Network Programming — `IEC_CNTT`)  
> **Nền tảng:** C# .NET 9.0 Cross-Platform (Tương thích đồng nhất trên **Windows** & **macOS**)  
> **Giao thức mạng:** `TcpListener` / `TcpClient` (Cổng `9000`), `UdpClient` Auto-Discovery (Cổng `9001`), `HttpListener` Web Cockpit (Cổng `5050`)  
> **Kiến trúc dữ liệu:** `StackExchange.Redis` (Khai thác 5 cấu trúc: `SET`, `HASH`, `STRING + TTL`, `ZSET`, `LIST` & `Atomic Counter Rate-Limiter`) kèm cơ chế **In-Memory Fallback** tự động.

---

## 1. Tổng quan Bài toán & Giải pháp Kiến trúc

Trong các hệ thống quản trị nhân sự qua mạng nội bộ (LAN), thách thức lớn nhất của tầng giao vận mạng là **xác định chính xác trạng thái kết nối thực tế** và **đồng bộ dữ liệu thời gian thực** giữa nhiều thiết bị:
1. **Bài toán "Online ảo" (Half-Open TCP Connection / Ghost Online):** Khi nhân viên bấm đăng xuất (`LOGOUT`), Client gửi gói tin báo Server đóng kết nối ngay. Nhưng nếu máy tính nhân viên **sập nguồn đột ngột, rớt Wi-Fi, rút cáp LAN hoặc bị tắt ngang tiến trình (`Kill Process`)**, hệ điều hành Client không thể gửi cờ `TCP FIN/RST`. Socket phía Server bị treo nửa vời và báo nhân viên Online ảo suốt cả ngày.
2. **Bài toán Tìm kiếm Máy chủ trong mạng LAN động (DHCP IP):** Khi triển khai giữa nhiều laptop trên giảng đường, địa chỉ IPv4 của Server thay đổi liên tục theo mạng Wi-Fi/Hotspot.
3. **Bài toán Đa luồng & Bảo mật Gói tin (Concurrency & TCP Flood):** Khi hàng chục Client cùng gửi nhịp tim `PING`, đổi trạng thái, chat nội bộ hoặc khi một Client bị lỗi vòng lặp bắn hàng loạt gói tin gây nghẽn mạng.

**Giải pháp của hệ thống IEC (`iec`):**
- **Tự động dò tìm Server bằng UDP Broadcast (`Port 9001`):** Client phát gói tin quảng bá UDP tìm Server trong mạng LAN trước khi thiết lập kết nối TCP tin cậy ở cổng `9000`.
- **Cơ chế Nhịp tim TCP (`Heartbeat 5s`) + Khóa tự hủy Redis (`TTL 15s`):** Client gửi gói tin `PING` kèm mốc thời gian mỗi 5 giây để đo độ trễ khứ hồi (`RTT ms`) và gia hạn khóa `staff:online:<Mã_NV>` trên Redis thêm 15 giây. Nếu mất mạng đột ngột, sau đúng 15 giây khóa Redis tự hủy và nhân viên chuyển về **`OFFLINE`**.
- **Tích hợp 5 Cấu trúc Dữ liệu Redis & Tường lửa Chống Spam (`Rate-Limiter`):** Quản lý đồng bộ danh sách nhân sự (`SET`), hồ sơ & công việc (`HASH`), bảng xếp hạng thời gian trực tuyến (`ZSET`), lịch sử tin nhắn phát lại (`LIST`) và bộ đếm chặn tấn công ngập lụt gói tin (`INCR + EXPIRE`).

---

## 2. Phân tích Cấu trúc Thư mục: Chức năng, Đóng góp & Tác động (Impact)

```text
iec/
├── iec.csproj                # [Project Config] Cấu hình SDK .NET 9.0 & gói NuGet StackExchange.Redis
├── StaffMember.cs            # [Data Model & Protocol] Thực thể nhân sự, từ điển lệnh & bộ lọc Sanitize()
├── RedisPresenceService.cs   # [Redis State Layer] Tầng xử lý 5 cấu trúc Redis, Rate-Limit & In-Memory Fallback
├── HrmTcpServer.cs           # [Core Network Server] Máy chủ TCP (:9000) + UDP (:9001) đa luồng bất đồng bộ
├── HrmTcpClient.cs           # [Core Network Client] Máy khách TCP/UDP, bộ phát nhịp tim 5s & đo độ trễ RTT
├── WebDashboardHost.cs       # [Web Cockpit & Bridge] Giao diện giám sát (:5050) & Cầu nối điều khiển Socket thật
├── Program.cs                # [Unified Entry Point] Bộ điều phối khởi chạy hợp nhất trên Windows & macOS
├── .gitignore                # [Git Hygiene] Loại bỏ thư mục biên dịch tạm (bin/, obj/) khỏi kho lưu trữ
└── README.md                 # [Documentation] Tài liệu kiến trúc mạng, thiết kế Redis và kịch bản nghiệm thu
```

### Bảng Phân tích Chuyên sâu Từng Thành phần trong Dự án

#### 2.1. `iec.csproj` — Cấu hình Môi trường Đa Nền Tảng
- **Làm gì:** Khai báo Target Framework `net9.0`, kiểu đầu ra `Exe` và thư viện `StackExchange.Redis`.
- **Đóng góp cho bài:** Loại bỏ sự phụ thuộc vào các thư viện giao diện chỉ chạy trên Windows (như WinForms/WPF).
- **Impact (Tác động):** Giúp mã nguồn biên dịch và chạy đồng nhất 100% trên cả **Windows** và **macOS** chỉ với lệnh `dotnet run`.

#### 2.2. `StaffMember.cs` — Hợp đồng Giao thức (`HrmProtocol`) & An toàn Khung Gói tin
- **Làm gì:** Định nghĩa các lớp dữ liệu (`StaffMember`, `TcpLogEntry`, `ChatMessageEntry`), các hằng số giao thức (`DefaultPort = 9000`, `UdpDiscoveryPort = 9001`, `HeartbeatIntervalMs = 5000`, `OnlineTtlSeconds = 15`) và hàm lọc `HrmProtocol.Sanitize()`.
- **Đóng góp cho bài:** Đóng vai trò là "Hợp đồng giao thức" dùng chung giữa Server và Client, khử ký tự ẩn UTF-8 BOM (`\uFEFF`) và lọc ký tự phân tách `|`.
- **Impact (Tác động):** Ngăn chặn triệt để lỗi **Delimiter Injection** (người dùng nhập ký tự `|` hoặc xuống dòng làm vỡ cấu trúc `Split('|')` trên luồng TCP) và giúp thay đổi cấu hình mạng tại một nơi duy nhất.

#### 2.3. `RedisPresenceService.cs` — Tầng Quản trị Trạng thái Redis & Cơ chế Tự phục hồi (`Fallback`)
- **Làm gì:** Đóng gói toàn bộ lệnh giao tiếp với Redis Server qua 5 cấu trúc dữ liệu (`SET`, `HASH`, `STRING + TTL`, `ZSET`, `LIST`), bộ đếm chống spam (`Rate-Limiter`) và bộ máy **`In-Memory 5-Struct Redis Engine Fallback`**.
- **Đóng góp cho bài:** Tách biệt hoàn toàn logic lưu trữ trạng thái trên RAM ra khỏi logic truyền nhận Socket (Separation of Concerns).
- **Impact (Tác động):**
  1. Đảm bảo tốc độ đọc/ghi trạng thái khi hàng chục Client bắn `PING` liên tục đạt dưới 1 mili-giây mà không gây nghẽn ổ cứng (Disk I/O).
  2. Nhờ cơ chế **Fallback tự động**, nếu chạy trên máy phòng Lab hoặc máy macOS chưa bật dịch vụ Redis cổng `6379`, ứng dụng tự động chuyển sang bộ nhớ giả lập trên RAM hỗ trợ đầy đủ cả 5 cấu trúc và bộ đếm ngược TTL 15s mà không bị dừng chương trình.

#### 2.4. `HrmTcpServer.cs` — Máy chủ Lập trình Mạng Đa luồng (`TcpListener` & `UdpClient`)
- **Làm gì:**
  - Mở cổng TCP `9000` (`TcpListener`) chấp nhận nhiều kết nối đồng thời qua `AcceptTcpClientAsync()` + `Task.Run()`.
  - Mở cổng UDP `9001` (`UdpClient`) lắng nghe gói tin `DISCOVER_IEC_SERVER` và tự động phản hồi địa chỉ IPv4 LAN của Server.
  - Bọc mỗi kết nối vào lớp **`ClientSession`** có khóa `SemaphoreSlim(1, 1)` để đồng bộ hóa luồng ghi `StreamWriter`.
- **Đóng góp cho bài:** Thể hiện trực tiếp các kỹ thuật cốt lõi của môn Lập trình mạng: kết hợp UDP (Connectionless) và TCP (Connection-oriented), xử lý bất đồng bộ `async/await`, quản lý phiên an toàn luồng (`Thread-safe`) và định tuyến bản tin (Broadcast & Unicast DM).
- **Impact (Tác động):** Khóa `SemaphoreSlim` trong `ClientSession` loại bỏ hoàn toàn lỗi xung đột luồng (`InvalidOperationException: stream in use`) khi Server vừa trả lời `PONG` vừa đẩy tin nhắn `BROADCAST` xuống cùng một Client.

#### 2.5. `HrmTcpClient.cs` — Ứng dụng Đầu cuối Nhân viên (`TcpClient` & `PeriodicTimer`)
- **Làm gì:**
  - Phát gói tin UDP Broadcast cổng `9001` để tự động tìm IP Server trong mạng LAN, sau đó kết nối TCP vào cổng `9000`.
  - Vận hành song song 3 luồng tác vụ: `HeartbeatLoopAsync` (bắn `PING` mỗi 5s kèm Timestamp), `ListenServerAsync` (đo độ trễ `RTT ms` khi nhận `PONG`, nhận `BROADCAST` và lịch sử `REPLAY`), và `EmployeeMenuLoopAsync` (đổi trạng thái, cập nhật Task, gửi Chat/DM, kiểm thử Spam Flood).
- **Đóng góp cho bài:** Chứng minh khả năng truyền thông **Song công toàn phần (Full-Duplex)** trên một kết nối TCP duy nhất.
- **Impact (Tác động):** Cho phép chạy độc lập trên nhiều máy tính vật lý khác nhau (Windows PC, Laptop phụ, MacBook) cùng kết nối về Server trung tâm.

#### 2.6. `WebDashboardHost.cs` — Giao diện Giám sát Trực quan & Cầu nối Socket Thật
- **Làm gì:** Khởi chạy máy chủ HTTP nhẹ (`HttpListener` cổng `5050`) cung cấp giao diện **Dark-Mode Cockpit** và quản lý các đối tượng `ManagedSimClient` (kết nối `TcpClient` thật vào cổng `9000`).
- **Đóng góp cho bài:** Mang lại giao diện đồ họa trực quan trên mọi hệ điều hành mà mọi thao tác bấm nút đều đi qua đường ống `NetworkStream` thật 100%.
- **Impact (Tác động):** Giúp quan sát trực tiếp thanh đếm ngược `REDIS TTL (15s)`, độ trễ `RTT (ms)`, bảng xếp hạng `UPTIME (ZSET)` và cảnh báo khóa `SECOPS BLOCKED` theo từng giây khi bảo vệ đồ án.

#### 2.7. `Program.cs` — Điểm Khởi chạy Hợp nhất (Unified Entry Point)
- **Làm gì:** Thiết lập bảng mã `UTF-8` và điều phối menu chọn vai trò **[1] Admin Server + Web Cockpit** hoặc **[2] Employee Client**.
- **Đóng góp & Impact:** Gom cả Server và Client vào chung một dự án gọn nhẹ, đồng bộ 100% phiên bản giao thức khi làm việc nhóm qua GitHub.

---

## 3. Kiến trúc 6 Cơ chế Redis trong Dự án & Mối liên hệ với Lập trình Mạng

| Cơ chế / Cấu trúc Redis | Khóa (Key Pattern) | Lệnh Redis Tương ứng | Giải quyết Bài toán gì cho Lập trình Mạng? |
| :--- | :--- | :--- | :--- |
| **1. `Redis SET`** | `staff:ids` | `SADD`, `SMEMBERS` | Lưu danh sách mã nhân viên duy nhất (`NV01`, `NV02`...), giúp Server duyệt quân số với độ phức tạp $O(N)$ mà không dùng lệnh cấm `KEYS *`. |
| **2. `Redis HASH`** | `staff:profile:{Id}` | `HSET`, `HGETALL` | Lưu hồ sơ nhân viên (`FullName`, `Department`, `ClientIp`, `LastSeen`, `CurrentTask`, `RttMs`). Giữ nguyên thông tin ngay cả khi Socket đã ngắt. |
| **3. `Redis STRING + TTL`** | `staff:online:{Id}` | `SETEX ... 15`, `EXPIRE ... 15`, `DEL` | **Trái tim phát hiện rớt mạng:** Lưu trạng thái (`Online`/`Busy`/`Away`) kèm bộ đếm ngược 15s. Tự động xóa khóa khi mất nhịp tim `PING` do đứt mạng. |
| **4. `Redis ZSET`** | `staff:leaderboard:uptime` | `ZINCRBY ... 5`, `ZSCORE` | Cộng lũy kế 5 giây mỗi nhịp tim `PING` thành công, tự động xếp hạng nhân viên chuyên cần theo thời gian thực với độ phức tạp $O(\log N)$. |
| **5. `Redis LIST`** | `staff:chat:history` | `LPUSH`, `LTRIM 0 49`, `LRANGE` | Lưu 50 tin nhắn Chat/DM/Task mới nhất trên RAM để **phát lại (Offline Message Replay)** cho các Socket vừa kết nối vào sau. |
| **6. `Atomic Counter`** | `security:ratelimit:{Id}` | `INCR`, `EXPIRE ... 10` | **Tường lửa tầng Ứng dụng:** Đếm số gói tin TCP gửi lên trong mỗi cửa sổ 10 giây. Nếu vượt quá 25 gói/10s, tự động khóa tài khoản 30s và ngắt Socket. |

---

## 4. Đặc tả Giao thức Tầng Ứng dụng (TCP Port `9000` & UDP Port `9001`)

| Giao thức | Lệnh | Cú pháp Bản tin | Xử lý tại Server & Tác động lên Redis | Phản hồi Mạng |
| :---: | :--- | :--- | :--- | :--- |
| **UDP :9001** | **`DISCOVER`** | `DISCOVER_IEC_SERVER` | Server lắng nghe UDP Broadcast và lấy IPv4 LAN thực tế để phản hồi | `IEC_SERVER_HERE\|<IP>\|9000` |
| **TCP :9000** | **`LOGIN`** | `LOGIN\|<Id>\|<Name>\|<Dept>\|<Task>` | `SADD`, `HSET` hồ sơ, `SETEX` TTL 15s, đọc `LRANGE` gửi lại 3 tin nhắn gần nhất | `LOGIN_OK\|...` + `REPLAY\|...` |
| **TCP :9000** | **`PING`** | `PING\|<Id>\|<TickMs>\|<RttMs>` | `EXPIRE` gia hạn TTL lên 15s, `HSET` lưu `RttMs`, `ZINCRBY` cộng 5s Uptime | `PONG\|<TickMs>` |
| **TCP :9000** | **`STATUS`** | `STATUS\|<Id>\|<Online/Busy/Away>` | `SETEX staff:online:<Id> 15 "<Status>"`, cập nhật `LastSeen` | `STATUS_OK\|...` |
| **TCP :9000** | **`TASK`** | `TASK\|<Id>\|<Mô_tả_công_việc>` | `HSET staff:profile:<Id> CurrentTask "<Task>"`, lưu nhật ký vào `LIST` | `TASK_OK\|...` |
| **TCP :9000** | **`CHAT` / `DM`** | `CHAT\|<Id>\|<Msg>` hoặc `DM\|<From>\|<To>\|<Msg>` | `LPUSH` vào Redis `LIST`, định tuyến Broadcast (All) hoặc Unicast (riêng người nhận) | `BROADCAST\|...` / `DM_OK` |
| **TCP :9000** | **`LOGOUT`** | `LOGOUT\|<Id>` | `DEL staff:online:<Id>` (chuyển sang `OFFLINE` tức thì không chờ 15s), đóng Socket | `BYE` |

---

## 5. Hướng dẫn Cài đặt & Chạy Kiểm thử (Windows & macOS)

### Bước 1: Mở Terminal 1 — Khởi chạy Admin Server, UDP Responder & Web Cockpit
```bash
git clone [https://github.com/bitmap1701/iec.git](https://github.com/bitmap1701/iec.git)
cd iec
dotnet run
```
- Nhập **`1`** -> Nhấn **Enter** để chọn vai trò `ADMIN SERVER`.
- Nhấn **Enter** ở dòng hỏi địa chỉ Redis (mặc định `localhost:6379`; tự động kích hoạt `In-Memory 5-Struct Redis Engine` nếu máy chưa cài Redis).
- Trình duyệt tự động mở trang quản trị tại **`http://localhost:5050`**.

### Bước 2: Mở Terminal 2 — Khởi chạy thêm Employee Client (Hỗ trợ UDP Auto-Discovery)
Giữ nguyên cửa sổ Terminal 1 đang chạy Server, mở một tab Terminal mới (hoặc chạy trên một laptop thứ 2 trong cùng mạng LAN/Hotspot):
```bash
cd iec
dotnet run
```
- Nhập **`2`** -> Nhấn **Enter** để chọn vai trò `EMPLOYEE CLIENT`.
- Ở dòng hỏi IP Server: Nhấn **Enter** để Client tự động bắn gói tin **UDP Broadcast cổng `9001`** tìm IP của Server và kết nối TCP vào cổng `9000`.
- Nhập Mã NV (`NV04`), Họ tên, Phòng ban và Task đang làm -> Quan sát `NV04` xuất hiện ngay trên bảng `http://localhost:5050`.

---

## 6. Năm Kịch bản Kiểm thử Nghiệm thu Đồ án

1. **Kịch bản 1 — Tự động dò tìm Server bằng UDP (`UDP Broadcast -> TCP Handshake`):** Chạy Client `[2]` và nhấn Enter ở ô nhập IP -> Quan sát log `[UDP-9001]` trên Server trả lời địa chỉ IP LAN và Client tự động bắt tay TCP.
2. **Kịch bản 2 — Rớt mạng / Sập nguồn đột ngột (`Half-Open Socket & Redis TTL 15s`):** Chọn một nhân viên trên Web Cockpit, bấm nút **"A. Giả lập Sập nguồn / Rớt mạng (Đóng Socket)"** -> Quan sát thanh `REDIS TTL (15s)` đếm ngược từ `15s` về `0s` rồi tự động chuyển sang **`OFFLINE`**.
3. **Kịch bản 3 — Chống tấn công Spam gói tin (`TCP Flood & Redis Rate-Limiter`):** Bấm nút **"B. Giả lập Tấn công Spam 30 gói (Rate-Limit)"** -> Bộ đếm Redis phát hiện vượt ngưỡng 25 gói/10s, lập tức ngắt Socket và đổi nhãn sang **`BLOCKED (30s)`**.
4. **Kịch bản 4 — Báo cáo Task (`Redis HASH`) & Xếp hạng Chuyên cần (`Redis ZSET`):** Cập nhật tiến độ công việc tại mục 2 bên phải và quan sát bảng tự động sắp xếp huy chương 🥇🥈🥉 theo điểm `UPTIME (ZSET)` tăng thêm 5 giây mỗi nhịp `PING`.
5. **Kịch bản 5 — Chat nội bộ, Nhắn riêng (`Unicast DM`) & Phát lại lịch sử (`Redis LIST Replay`):** Gửi tin nhắn chung (`ALL`) hoặc nhắn riêng (`NV02`), sau đó mở một Client mới đăng nhập vào sẽ nhận được ngay 3 tin nhắn gần nhất nhờ cơ chế `REPLAY` từ Redis `LIST`.

---

## 7. Thành viên Thực hiện (Contributors)

- **Huỳnh Nhật Hoà** ([@bitmap1701](https://github.com/bitmap1701)) — Kiến trúc sư hệ thống TCP/UDP Socket, tích hợp 5 cấu trúc Redis, Rate-Limiter & giao diện Web Cockpit.
- **Lương Nhất Khôi** ([@FirstKhoi](https://github.com/FirstKhoi)) — Kiểm thử đa nền tảng (macOS Client <-> Windows Server), chuẩn hóa tài liệu kỹ thuật & kịch bản nghiệm thu.