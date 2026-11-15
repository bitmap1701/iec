# IEC — Hệ thống Quản trị & Định vị Trạng thái Nhân sự Thời gian thực (TCP Socket & Redis)

> **Học phần:** Lập trình mạng (Network Programming — `IEC_CNTT`)  
> **Nền tảng:** C# .NET 9.0 Cross-Platform (Chạy đồng nhất trên **Windows** & **macOS**)  
> **Công nghệ cốt lõi:** `System.Net.Sockets` (`TcpListener` / `TcpClient` / `NetworkStream`), `StackExchange.Redis`, `HttpListener`  
> **Kiến trúc:** Multi-threaded Asynchronous TCP Socket Server kết hợp Redis In-Memory State (Set, Hash, String TTL)

---

## 1. Tổng quan Bài toán & Mục tiêu Đồ án

Trong các hệ thống quản lý nhân sự nội bộ (LAN), bài toán khó nhất của tầng giao vận mạng là phát hiện chính xác trạng thái **Online / Offline thực tế** khi xảy ra sự cố vật lý:
- Nếu nhân viên bấm nút **Đăng xuất (`LOGOUT`)**, ứng dụng gửi gói tin báo cho Server biết để ngắt kết nối ngay lập tức.
- Nhưng nếu máy tính nhân viên **sập nguồn đột ngột, đứt cáp LAN, rớt Wi-Fi hoặc bị tắt ngang tiến trình (`Kill Process`)**, hệ điều hành phía Client không kịp gửi gói tin `TCP FIN/RST`. Khi đó, Socket phía Server rơi vào trạng thái **Half-Open Connection (Kết nối treo một nửa)**, dẫn đến hiện tượng **"Online ảo" (Ghost Online)**.

Dự án **IEC (`iec`)** giải quyết triệt để vấn đề này bằng sự kết hợp giữa **Lập trình Socket (Heartbeat 5s)** và **Cơ chế Khóa tự hủy (TTL 15s) của Redis**:
1. Mỗi `Employee Client` duy trì kết nối TCP tới Server ở cổng `9000` và định kỳ bắn gói tin nhịp tim `PING|<Mã_NV>` mỗi **5 giây**.
2. Mỗi lần nhận được `PING`, `Admin Server` gọi lệnh `EXPIRE` gia hạn khóa `staff:online:<Mã_NV>` trên Redis lên **15 giây**.
3. Nếu Client mất kết nối đột ngột, luồng `PING` dừng lại -> Đúng **15 giây** sau, khóa trên Redis tự động bốc hơi khỏi RAM -> Nhân viên tự động chuyển về **`OFFLINE`** mà không làm treo Server.

---

## 2. Phân tích Chi tiết Cấu trúc Thư mục: Chức năng, Đóng góp & Tác động (Impact)

```text
iec/
├── iec.csproj                # [Cấu hình] Khai báo SDK .NET 9.0 & thư viện StackExchange.Redis
├── StaffMember.cs            # [Data Model & Protocol] Định nghĩa thực thể & từ điển giao thức TCP
├── RedisPresenceService.cs   # [Data & State Layer] Quản lý Redis (Set, Hash, TTL) & In-Memory Fallback
├── HrmTcpServer.cs           # [Core Network Server] Lõi TcpListener đa luồng (Cổng 9000)
├── HrmTcpClient.cs           # [Core Network Client] Lõi TcpClient & Bộ phát nhịp tim (Heartbeat 5s)
├── WebDashboardHost.cs       # [Web Cockpit & Bridge] Giao diện giám sát (Cổng 5050) & Cầu nối TCP thật
├── Program.cs                # [Entry Point] Bộ điều phối khởi chạy đa nền tảng Windows / macOS
├── .gitignore                # [Git Config] Chặn đẩy thư mục biên dịch tạm (bin/, obj/) lên GitHub
└── README.md                 # [Documentation] Tài liệu kiến trúc, giao thức và kịch bản kiểm thử
```

### Bảng Phân tích Chuyên sâu Từng Thành phần trong Dự án

#### 2.1. `iec.csproj` — Cấu hình Môi trường & Phụ thuộc
- **Làm gì (Chức năng):** Định nghĩa Target Framework là `net9.0`, kiểu đầu ra là ứng dụng thực thi (`Exe`) và khai báo gói thư viện `StackExchange.Redis`.
- **Đóng góp gì cho bài:** Đảm bảo mã nguồn không phụ thuộc vào thư viện giao diện riêng của Windows (như WinForms hay WPF), giúp cùng một bộ code biên dịch được trên cả Windows và macOS.
- **Impact (Tác động tới dự án):** Là nền móng tương thích đa nền tảng. Nhờ thiết kế file `.csproj` tối giản, thành viên dùng macOS hoặc Windows chỉ cần gõ `dotnet run` là NuGet tự động tải đúng gói thư viện tương ứng với kiến trúc chip (Intel x64 hoặc Apple Silicon M-series).

#### 2.2. `StaffMember.cs` — Hợp đồng Dữ liệu & Chuẩn hóa Giao thức (`HrmProtocol`)
- **Làm gì (Chức năng):** Chứa 3 thành phần:
  1. Class `StaffMember`: Lưu thông tin nhân sự (`EmpId`, `FullName`, `Department`, `ClientIp`, `CurrentStatus`, `RemainingTtlSeconds`, `PingCount`).
  2. Class `TcpLogEntry`: Định dạng dòng nhật ký gói tin (`Time`, `Tag`, `Message`).
  3. Static class `HrmProtocol`: Cố định các hằng số mạng (`DefaultPort = 9000`, `WebUiPort = 5050`, `HeartbeatIntervalMs = 5000`, `OnlineTtlSeconds = 15`) và các từ khóa lệnh (`LOGIN`, `PING`, `STATUS`, `LOGOUT`).
- **Đóng góp gì cho bài:** Đóng vai trò là **"Hợp đồng giao thức" (Protocol Contract)** dùng chung cho cả Server và Client, loại bỏ hoàn toàn lỗi gõ sai chuỗi lệnh (*Magic Strings*) hoặc lệch cổng kết nối.
- **Impact (Tác động tới dự án):** Nếu muốn thay đổi chu kỳ nhịp tim (ví dụ từ 5s xuống 3s) hoặc đổi thời gian hết hạn TTL (từ 15s sang 30s) khi demo cho giảng viên, nhóm chỉ cần sửa duy nhất 1 con số tại file này là cả Server, Client và Redis đều tự động đồng bộ theo.

#### 2.3. `RedisPresenceService.cs` — Tầng Quản trị Trạng thái Thời gian thực & Tự phục hồi (Fallback)
- **Làm gì (Chức năng):** Đóng gói toàn bộ lệnh đọc/ghi tới Redis thông qua 3 cấu trúc dữ liệu:
  - `SET (staff:ids)`: Lưu danh sách mã nhân viên duy nhất.
  - `HASH (staff:profile:{empId})`: Lưu hồ sơ tĩnh (`FullName`, `Department`, `ClientIp`, `LastSeen`).
  - `STRING + TTL (staff:online:{empId})`: Lưu trạng thái động (`Online`/`Busy`/`Away`) kèm bộ đếm ngược tự hủy 15 giây.
  - Đặc biệt: Tích hợp bộ máy **`In-Memory Redis Engine Fallback`** bằng `ConcurrentDictionary` có tính toán thời gian hết hạn `ExpireAt`.
- **Đóng góp gì cho bài:** Tách biệt hoàn toàn logic lưu trữ dữ liệu ra khỏi logic xử lý mạng Socket (Separation of Concerns).
- **Impact (Tác động tới dự án):**
  1. **Về hiệu năng:** Truy xuất trạng thái Online/Offline với độ phức tạp $O(1)$ trên RAM, không cần quét `KEYS *` gây nghẽn Redis.
  2. **Về tính sẵn sàng khi chấm bài:** Nhờ cơ chế *Fallback*, nếu máy của thành viên nhóm hoặc máy phòng Lab chưa cài đặt dịch vụ Redis Server ở cổng `6379`, chương trình **không bị văng lỗi kết nối** mà tự động chuyển sang chế độ giả lập Redis trên RAM với hành vi đếm ngược TTL 15s giống hệt Redis thật.

#### 2.4. `HrmTcpServer.cs` — Trái tim của Đồ án Môn Lập trình Mạng (`TcpListener`)
- **Làm gì (Chức năng):**
  - Mở cổng TCP `9000` qua `TcpListener(IPAddress.Any, 9000)` để lắng nghe kết nối trên mọi card mạng (`127.0.0.1`, Wi-Fi LAN, Hotspot).
  - Sử dụng vòng lặp bất đồng bộ `AcceptTcpClientAsync()` kết hợp `Task.Run(() => HandleClientAsync(client))` để phục vụ hàng chục nhân viên kết nối đồng thời mà không chặn luồng chính (Non-blocking Multi-client).
  - Đọc luồng byte từ `NetworkStream` qua `StreamReader`, bóc tách gói tin theo ký tự phân tách `|` (`LOGIN`, `PING`, `STATUS`, `LOGOUT`), gọi `RedisPresenceService` cập nhật trạng thái và phản hồi lại Client qua `StreamWriter`.
  - Lưu danh sách các kết nối đang sống vào `ConcurrentDictionary<string, StreamWriter>` để phát thông báo `BROADCAST` từ Admin xuống toàn bộ nhân viên.
- **Đóng góp gì cho bài:** Thể hiện trực tiếp kiến thức cốt lõi của môn **Lập trình mạng**: quản lý vòng đời Socket, lập trình bất đồng bộ `async/await`, xử lý đa luồng an toàn (`Thread-safe`) và bắt ngoại lệ rớt mạng (`catch IOException`).
- **Impact (Tác động tới dự án):** Đây là thành phần quyết định điểm số kỹ thuật của đồ án. Nếu một Client bị tắt ngang, khối `catch (IOException)` và `finally` trong file này sẽ cô lập lỗi của riêng Client đó, thu hồi tài nguyên Socket sạch sẽ mà không làm ảnh hưởng đến các nhân viên khác đang kết nối.

#### 2.5. `HrmTcpClient.cs` — Ứng dụng Đầu cuối Nhân viên & Bộ phát Nhịp tim (`TcpClient`)
- **Làm gì (Chức năng):**
  - Khởi tạo `TcpClient`, kết nối tới IP và cổng `9000` của Server, đồng bộ luồng gửi bằng `SemaphoreSlim(1, 1)`.
  - Sau khi gửi bản tin `LOGIN`, kích hoạt đồng thời 3 luồng tác vụ chạy song song:
    1. `HeartbeatLoopAsync`: Dùng `PeriodicTimer` của .NET để tự động bắn gói tin `PING|<Mã_NV>` mỗi 5 giây.
    2. `ListenServerAsync`: Lắng nghe liên tục phản hồi `PONG`, `STATUS_OK` và tin nhắn `BROADCAST` từ Admin đẩy xuống.
    3. `EmployeeMenuLoopAsync`: Giao diện dòng lệnh cho phép nhân viên chủ động đổi trạng thái (`Online`/`Busy`/`Away`) hoặc gửi lệnh `LOGOUT`.
- **Đóng góp gì cho bài:** Chứng minh khả năng giao tiếp **Song công toàn phần (Full-Duplex)** trên một kết nối TCP duy nhất (vừa gửi `PING`/`STATUS` định kỳ, vừa nhận `BROADCAST` bất kỳ lúc nào từ Server).
- **Impact (Tác động tới dự án):** Cho phép chạy độc lập trên một máy tính thứ hai (ví dụ máy macOS kết nối qua mạng LAN vào máy Windows đang chạy Server) để chứng minh tính thực tế của mô hình mạng phân tán.

#### 2.6. `WebDashboardHost.cs` — Giao diện Giám sát Trực quan & Trình Điều khiển Socket Thật
- **Làm gì (Chức năng):**
  - Khởi chạy một Web Server nội bộ nhẹ bằng `HttpListener` tại `http://localhost:5050/`.
  - Cung cấp giao diện **Dark-Mode Cockpit** hiển thị trực tiếp thanh tiến trình đếm ngược TTL 15 giây của từng nhân viên và cửa sổ nhật ký gói tin TCP thời gian thực.
  - Quản lý các đối tượng `ManagedSimClient` (bên trong là các `TcpClient` thật kết nối thẳng vào cổng `9000` của `HrmTcpServer`).
- **Đóng góp gì cho bài:** Giải quyết bài toán giao diện đồ họa (GUI) đa nền tảng mà không cần dùng WinForms (vốn không hỗ trợ trên macOS).
- **Impact (Tác động tới dự án):** Giúp buổi báo cáo đồ án trở nên trực quan. Khi bấm nút *"Giả lập Sập nguồn (Đóng Socket)"* trên trình duyệt, C# đóng thật kết nối `TcpClient` ngầm và cả nhóm nhìn thấy ngay thanh TTL đếm ngược từng giây từ `15s` về `0s` trước khi chuyển sang `OFFLINE`.

#### 2.7. `Program.cs` — Điểm Khởi chạy Hợp nhất (Unified Entry Point)
- **Làm gì (Chức năng):** Cấu hình bảng mã `UTF-8` cho Console, hiển thị menu cho phép người dùng chọn chạy chế độ **[1] Admin Server + Web UI** hoặc **[2] Employee Client** ngay trong cùng một project.
- **Đóng góp gì cho bài:** Gom cả Server và Client vào chung một Solution/Project gọn nhẹ, giúp việc chia sẻ code và chạy kiểm thử nhiều cửa sổ Terminal cực kỳ nhanh chóng.
- **Impact (Tác động tới dự án):** Giảm thiểu rủi ro lệch phiên bản code giữa project Server và project Client khi làm việc nhóm trên GitHub.

---

## 3. Đặc tả Giao thức Truyền thông (Application-Layer TCP Protocol)

Các bản tin được truyền dưới dạng văn bản `UTF-8`, kết thúc bằng ký tự xuống dòng (`\n` - Line-delimited framing) và phân tách trường bằng dấu `|`:

| Lệnh | Chiều truyền | Cú pháp Gói tin | Hành động tại TCP Server & Redis | Phản hồi TCP |
| :--- | :---: | :--- | :--- | :--- |
| **`LOGIN`** | Client -> Server | `LOGIN\|<EmpId>\|<FullName>\|<Dept>` | `SADD staff:ids`, `HSET staff:profile:<Id>`, `SETEX staff:online:<Id> 15 "Online"` | `LOGIN_OK\|...` |
| **`PING`** | Client -> Server | `PING\|<EmpId>` | `EXPIRE staff:online:<Id> 15`, cập nhật `LastSeen` trong Hash | `PONG` |
| **`STATUS`** | Client -> Server | `STATUS\|<EmpId>\|<Online/Busy/Away>` | `SETEX staff:online:<Id> 15 "<Status>"`, cập nhật `LastSeen` | `STATUS_OK\|...` |
| **`LOGOUT`** | Client -> Server | `LOGOUT\|<EmpId>` | `DEL staff:online:<Id>` (Chuyển sang `OFFLINE` tức thì), đóng Socket | `BYE` |
| **`BROADCAST`** | Server -> Clients | `BROADCAST\|[THÔNG BÁO]: <Nội dung>` | Server duyệt qua `ConcurrentDictionary` các Socket đang mở và đẩy tin nhắn xuống | *(Hiển thị ngay ở Client)* |

---

## 4. Hướng dẫn Cài đặt & Chạy Kiểm thử (Windows & macOS)

### Bước 1: Tải dự án & Khởi chạy Admin Server + Giao diện Web Cockpit
```bash
git clone [https://github.com/bitmap1701/iec.git](https://github.com/bitmap1701/iec.git)
cd iec
dotnet run
```
- Nhập **`1`** -> Nhấn **Enter** để chọn vai trò `ADMIN SERVER`.
- Nhấn **Enter** ở dòng hỏi địa chỉ Redis (mặc định `localhost:6379`; nếu máy chưa bật Redis, hệ thống tự động bật `In-Memory Redis Engine`).
- Trình duyệt sẽ tự động mở trang quản trị tại **`http://localhost:5050`**.

### Bước 2: Mở Terminal 2 — Khởi chạy thêm Employee Client trên dòng lệnh
Giữ nguyên cửa sổ Terminal 1 đang chạy Server, mở một tab Terminal mới:
```bash
cd iec
dotnet run
```
- Nhập **`2`** -> Nhấn **Enter** để chọn vai trò `EMPLOYEE CLIENT`.
- Nhập IP Server: Nhấn **Enter** nếu chạy trên cùng máy (`127.0.0.1`), hoặc nhập IP LAN của máy chạy Server nếu test giữa 2 máy tính.
- Nhập Mã NV (VD: `NV04`), Họ tên và Phòng ban -> Quan sát `NV04` xuất hiện ngay trên bảng `http://localhost:5050`.
