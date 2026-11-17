# IEC — Theo dõi trạng thái & chat nhân sự thời gian thực (TCP/UDP Socket + Redis)

> **Học phần:** Lập trình mạng (Network Programming — `IEC_CNTT`)
> **Nền tảng:** C# .NET 9, chạy trên Windows và macOS
> **Mạng:** `TcpListener`/`TcpClient` (cổng `9000`), `UdpClient` broadcast (cổng `9001`), `HttpListener` cho dashboard (cổng `5050`)
> **Trạng thái dùng chung:** Redis qua `StackExchange.Redis` — bắt buộc phải có Redis đang chạy

---

## 1. Bài toán và cách giải

Hệ thống cho nhân viên trong mạng LAN đăng nhập, báo trạng thái (Online/Busy/Away) và chat với nhau. Ba vấn đề mạng mà dự án tập trung giải quyết:

1. **Kết nối "nửa mở" (half-open).** Khi máy client sập nguồn hoặc rớt Wi-Fi, hệ điều hành không kịp gửi `FIN`/`RST`. Phía server, socket vẫn "mở" và lệnh đọc treo mãi, nên nhân viên hiện Online dù đã mất kết nối.
   - Client gửi `PING` mỗi 5 giây. Server đặt **hạn đọc 15 giây** trên mỗi socket: quá 15 giây không nhận được dòng nào thì server chủ động đóng và đặt Offline.
   - Khi client đóng kết nối bình thường (EOF) hoặc bị reset, server đặt Offline **ngay lập tức**.
   - Khóa `staff:online:<Id>` trên Redis có TTL 15 giây làm lưới an toàn: nếu chính **server** chết, không ai xóa khóa thì Redis tự xóa.
2. **Tìm server trong LAN khi IP thay đổi.** Client phát UDP broadcast tới cổng `9001`; server trả lời kèm cổng TCP của mình. Client lấy IP từ địa chỉ nguồn của gói trả lời rồi mới mở kết nối TCP.
3. **Nhiều server, một trạng thái.** Toàn bộ trạng thái nằm trên Redis chứ không nằm trong RAM của server. Nhờ đó có thể chạy nhiều server IEC cùng trỏ về một Redis: nhân viên nối vào server nào cũng thấy nhau, và tin chat đi qua **Redis Pub/Sub** để tới socket đang nằm ở server khác. Khởi động lại một server không làm mất dữ liệu.

```text
 Client A ──TCP──▶ Server IEC :9000 ─┐
                                     ├──▶ Redis :6379  (SET, HASH, STRING+TTL, ZSET, LIST, Pub/Sub)
 Client B ──TCP──▶ Server IEC :9100 ─┘
```

---

## 2. Cấu trúc mã nguồn

```text
iec/
├── iec.csproj                # .NET 9 + gói NuGet StackExchange.Redis
├── Program.cs                # Chọn vai trò: [1] Server + dashboard, [2] Client console
├── StaffMember.cs            # Lớp dữ liệu + HrmProtocol (cổng, tên lệnh, hằng số, Sanitize)
├── HrmTcpServer.cs           # Server TCP, trả lời UDP discovery, nhận tin từ Redis Pub/Sub
├── HrmTcpClient.cs           # Client console: dò UDP, nhịp tim, đo RTT, menu thao tác
├── RedisPresenceService.cs   # Mọi lệnh Redis của dự án nằm ở đây
└── WebDashboardHost.cs       # Dashboard web; mỗi nút bấm điều khiển một TcpClient thật
```

| File | Điểm đáng chú ý về lập trình mạng |
| :--- | :--- |
| `HrmTcpServer.cs` | Mỗi kết nối một `Task`. Đọc từng dòng có hạn 15s và giới hạn 4096 ký tự. Ghi vào socket qua `SemaphoreSlim` để `PONG` và tin chat không chen nhau. Lỗi ghi tới một người nhận không làm rớt người gửi. |
| `HrmTcpClient.cs` | Ba việc chạy song song trên một kết nối TCP: gửi nhịp tim, nghe server, menu người dùng. Khi server đóng kết nối, client dừng nhịp tim và thoát menu. |
| `RedisPresenceService.cs` | Server IEC là **client** của Redis qua TCP cổng 6379. Bộ đếm rate-limit chạy bằng một script Lua để `INCR` và `EXPIRE` không bị tách rời. |
| `WebDashboardHost.cs` | Không tải gì từ Internet nên demo được trong LAN không có mạng ngoài. Mọi chữ client gửi lên đều được escape trước khi hiển thị. |

---

## 3. Dữ liệu trên Redis

| Cấu trúc | Khóa | Lệnh | Dùng để làm gì |
| :--- | :--- | :--- | :--- |
| `SET` | `staff:ids` | `SADD`, `SMEMBERS` | Danh sách mã nhân viên đã từng đăng nhập |
| `HASH` | `staff:profile:{Id}` | `HSET`, `HGETALL` | Hồ sơ: `FullName`, `Department`, `ClientIp`, `LastSeen`, `RttMs` |
| `STRING` + TTL | `staff:online:{Id}` | `SET … EX 15`, `EXPIRE`, `DEL` | Trạng thái Online/Busy/Away; tự hết hạn sau 15s nếu không có `PING` |
| `ZSET` | `staff:leaderboard:uptime` | `ZINCRBY`, `ZSCORE` | Cộng 5 giây mỗi `PING`, dùng để xếp hạng trên dashboard |
| `LIST` | `staff:chat:history` | `LPUSH`, `LTRIM 0 49`, `LRANGE` | 50 tin gần nhất, phát lại khi đăng nhập |
| Pub/Sub | `staff:chat:channel` | `PUBLISH`, `SUBSCRIBE` | Đưa tin chat tới mọi server IEC để đẩy xuống socket của từng server |
| Bộ đếm + TTL | `security:ratelimit:{Id}` | `INCR` + `EXPIRE 10` (Lua) | Đếm số dòng lệnh trong cửa sổ 10 giây |
| `STRING` + TTL | `security:blocked:{Id}` | `SET … EX 30`, `TTL` | Đánh dấu mã NV bị khóa 30 giây vì gửi quá 25 dòng/10s |

---

## 4. Giao thức tầng ứng dụng

Mỗi bản tin là một dòng văn bản UTF-8 kết thúc bằng `\n`, các trường cách nhau bằng `|`, tối đa 4096 ký tự.

### UDP `:9001` — dò tìm server

| Chiều | Bản tin |
| :--- | :--- |
| Client → broadcast | `DISCOVER_IEC_SERVER` |
| Server → client | `IEC_SERVER_HERE\|<IP>\|<Cổng TCP>` |

Client dùng địa chỉ nguồn của gói trả lời làm IP server và lấy cổng TCP từ nội dung. Nếu sau 1,8 giây không có trả lời, client hỏi người dùng nhập IP bằng tay.

### TCP `:9000` — lệnh từ client

| Lệnh | Cú pháp | Server làm gì | Trả lời |
| :--- | :--- | :--- | :--- |
| `LOGIN` | `LOGIN\|<Id>\|<Tên>\|<Phòng>` | Gắn `<Id>` với kết nối này, `SADD`, `HSET`, `SET … EX 15` | `LOGIN_OK\|…` rồi tối đa 3 dòng `REPLAY\|…` |
| `PING` | `PING\|<Id>\|<Mốc>[\|<RttMs>]` | Trả `PONG` trước, sau đó `EXPIRE 15`, `HSET`, `ZINCRBY 5` | `PONG\|<Mốc>` |
| `STATUS` | `STATUS\|<Id>\|<Online/Busy/Away>` | `SET staff:online:<Id> … EX 15` | `STATUS_OK\|…` |
| `CHAT` | `CHAT\|<Id>\|<Nội dung>` | `LPUSH` + `PUBLISH`, mọi server đẩy xuống socket của mình | `BROADCAST\|[CHAT TỪ <Id>]: …` tới mọi người |
| `DM` | `DM\|<Từ>\|<Tới>\|<Nội dung>` | `LPUSH` + `PUBLISH`, chỉ socket của `<Tới>` nhận | `DM_OK\|…` nếu người nhận đang online, `DM_QUEUED\|…` nếu đang offline |
| `LOGOUT` | `LOGOUT\|<Id>` | `DEL staff:online:<Id>`, đóng socket | `BYE` |

Ghi chú:

- `<Mốc>` trong `PING` là giá trị riêng của client; server gửi trả nguyên văn để client tự tính RTT. Client báo RTT đo được ở lần `PING` kế tiếp.
- `REPLAY` chỉ gồm tin chung và tin nhắn riêng mà chính người đó gửi hoặc nhận.
- Mã `ADMIN` và `ALL` được dành riêng, không đăng nhập được.

### Lỗi server trả về

| Dòng lỗi | Khi nào | Sau đó |
| :--- | :--- | :--- |
| `ERR\|NOT_LOGGED_IN\|…` | Gửi lệnh khác `LOGIN` khi chưa đăng nhập, hoặc `LOGIN` sai cú pháp | Đóng kết nối |
| `ERR\|ID_MISMATCH\|…` | Sau khi đăng nhập, gửi lệnh với `<Id>` khác mã của kết nối | Giữ kết nối, bỏ qua lệnh |
| `ERR\|RATE_LIMIT_BLOCKED\|…` | Quá 25 dòng lệnh trong 10 giây | Đóng kết nối, khóa mã NV 30 giây |
| `ERR\|UNKNOWN_USER\|…` | `DM` tới mã nhân viên chưa từng đăng nhập | Giữ kết nối, không lưu tin |
| `ERR\|BAD_COMMAND\|…` | Lệnh không tồn tại hoặc thiếu tham số | Giữ kết nối |

Server cũng đóng kết nối khi một dòng dài quá 4096 ký tự hoặc khi 15 giây không nhận được dòng nào.

---

## 5. Cài đặt và chạy

### Yêu cầu

- **.NET 9 SDK**
- **Redis** đang chạy (mặc định `localhost:6379`). Không có Redis thì server báo lỗi và thoát.

```bash
# macOS
brew install dotnet@9 redis
export PATH="/opt/homebrew/opt/dotnet@9/bin:$PATH"   # thêm dòng này vào ~/.zshrc
brew services start redis

# Windows / máy đã có Docker
docker run -d -p 6379:6379 redis
```

### Chạy server

```bash
git clone https://github.com/bitmap1701/iec.git
cd iec
dotnet run
```

1. Nhập `1` để chọn vai trò server.
2. Địa chỉ Redis: nhấn Enter để dùng `localhost:6379`.
3. Cổng TCP: nhấn Enter để dùng `9000`.

Trình duyệt tự mở dashboard tại `http://localhost:5050`. Dashboard ban đầu trống; thêm nhân viên bằng ô "Mở kết nối nhân viên mới" hoặc chạy client console.

### Chạy client console

Mở một terminal khác (hoặc một máy khác trong cùng LAN):

```bash
cd iec
dotnet run
```

1. Nhập `2` để chọn vai trò client.
2. IP server: nhấn Enter để tự dò bằng UDP broadcast, hoặc gõ `IP` hay `IP:cổng`.
3. Nhập mã nhân viên, họ tên, phòng ban.

### Chạy server thứ hai dùng chung Redis

Chạy `dotnet run` lần nữa, chọn `1`, và nhập cổng TCP khác, ví dụ `9100`. Dashboard của server này nằm ở `http://localhost:5150` (cổng web = 5050 + chênh lệch cổng TCP). Client nối vào bằng cách gõ `127.0.0.1:9100`.

Trên cùng một máy, chỉ server khởi động trước giữ được cổng UDP `9001`, nên tự dò UDP sẽ luôn trả về server đó.

---

## 6. Kịch bản demo

Tra nhanh quy trình, lệnh trên hai hệ điều hành và các con số chuẩn: [Bảng sử dụng](docs/BANG_SU_DUNG.md).

Hướng dẫn chạy thử từng bước kèm kết quả mong đợi: [macOS](docs/HUONG_DAN_TEST_MACOS.md) · [Windows](docs/HUONG_DAN_TEST_WINDOWS.md).

1. **Dò server bằng UDP.** Chạy client, nhấn Enter ở ô IP. Dashboard hiện dòng log `[UDP-9001]`, client in địa chỉ tìm được rồi kết nối TCP.
2. **Rớt mạng không có FIN (half-open).** Chọn một nhân viên trên dashboard, bấm **A. Rớt mạng**. Client ngừng gửi `PING` nhưng không đóng socket. Thanh TTL đếm về 0, sau 15 giây log hiện `[HALF-OPEN]` và nhân viên chuyển Offline. Với hai máy thật: tắt Wi-Fi của máy client.
3. **Đóng kết nối đột ngột.** Nhấn `Ctrl+C` ở terminal client. Server nhận EOF và đặt Offline ngay, không chờ TTL.
4. **Chống spam.** Bấm **B. Spam 30 dòng lệnh** (hoặc menu `6` ở client console). Sau dòng thứ 25, server trả `ERR|RATE_LIMIT_BLOCKED`, đóng socket, nhãn chuyển `BLOCKED` và đếm ngược 30 giây.
5. **Giả mạo danh tính.** Dùng `nc 127.0.0.1 9000`, gõ `LOGIN|T1|Test|QA` rồi `CHAT|NV01|xin chào`. Server trả `ERR|ID_MISMATCH`.
6. **Chat, nhắn riêng và phát lại.** Gửi tin chung và tin riêng giữa hai nhân viên. Đăng nhập một nhân viên thứ ba: dòng `REPLAY` chỉ có tin chung, không có tin riêng của hai người kia.
7. **Hai server, một Redis.** Chạy server ở `9000` và `9100`. Nhân viên nối vào server này chat chung, nhân viên ở server kia nhận được; log hiện `[PUBSUB]`.
8. **Khởi động lại server.** Tắt server (`0` ở menu admin) rồi chạy lại. Danh sách nhân viên và lịch sử chat vẫn còn vì nằm trên Redis.
9. **Redis chết giữa chừng.** Dừng Redis khoảng 10 giây rồi bật lại. Server không sập; client đang nối bị ngắt; sau khi Redis lên lại thì đăng nhập và chat bình thường.

---

## 7. Giới hạn đã biết

- **Không có xác thực.** Ai cũng đăng nhập được bằng bất kỳ mã nhân viên nào; đăng nhập trùng mã sẽ chiếm phiên của người đang dùng mã đó.
- **Trùng mã trên hai server.** Cùng một mã đăng nhập ở hai server khác nhau thì phiên cũ không bị ngắt. Khi phiên cũ kết thúc, người đó hiện Offline cho tới nhịp `PING` kế tiếp.
- **Uptime tính theo số `PING`.** Mỗi `PING` cộng cứng 5 giây, nên client gửi `PING` dày hơn sẽ có uptime cao hơn thực tế.
- **Rate-limit dạng cửa sổ cố định.** Một đợt gửi dồn ở cuối cửa sổ này và đầu cửa sổ kế tiếp có thể vượt 25 dòng trong 10 giây.
- **Dữ liệu không mã hóa.** Mọi bản tin đi dạng văn bản thuần trên TCP.
- **Phạm vi đã kiểm thử.** Đã chạy trên một máy macOS với nhiều tiến trình. Chưa kiểm thử trên Windows và trên hai máy vật lý với bản hiện tại.

---

## 8. Thành viên thực hiện

- **Huỳnh Nhật Hoà** ([@bitmap1701](https://github.com/bitmap1701))
- **Lương Nhật Khôi** ([@FirstKhoi](https://github.com/FirstKhoi))
