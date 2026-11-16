# Hướng dẫn chạy thử trên Windows

Gửi Hoà: nhánh `feat/socket-hardening-redis-pubsub` thay đổi khá nhiều so với `main`. Bản này **chưa được chạy trên Windows lần nào**, nên nhờ bạn làm lần lượt các bài dưới đây và ghi lại bài nào không đạt.

## Thay đổi cần biết trước

- **Redis là bắt buộc.** Bản giả lập trên RAM đã bỏ. Không có Redis thì server báo `[LỖI REDIS]` rồi thoát.
- **Bỏ lệnh TASK** và 3 nhân viên tạo sẵn. Dashboard ban đầu trống, phải tự thêm nhân viên.
- **Chat đi qua Redis Pub/Sub**, nên chạy được nhiều server dùng chung một Redis.
- **Server tự ngắt kết nối im lặng quá 15 giây** và đặt Offline ngay khi client đóng kết nối, không chờ TTL nữa.
- **Mã nhân viên gắn với kết nối từ lúc LOGIN.** Gửi lệnh với mã khác sẽ bị từ chối.
- Khi khởi động, server hỏi thêm **cổng TCP** (Enter là 9000).

Chi tiết giao thức xem ở `README.md`.

---

## Chuẩn bị (một lần)

Dùng **Windows Terminal** hoặc PowerShell; `cmd` cũ có thể hiển thị sai tiếng Việt.

### 1. .NET 9 SDK

```powershell
winget install Microsoft.DotNet.SDK.9
```

Mở terminal mới rồi kiểm tra: `dotnet --version` phải ra `9.0.x`.

### 2. Redis

Redis không có bản chính thức cho Windows. Cách đơn giản nhất là Docker Desktop:

```powershell
docker run -d --name iec-redis -p 6379:6379 redis
docker exec iec-redis redis-cli ping      # phải ra PONG
```

Những lần sau chỉ cần `docker start iec-redis`.

Nếu không dùng Docker: cài Redis trong WSL (`sudo apt install redis-server`, rồi `sudo service redis-server start`).

Xóa sạch dữ liệu test cũ: `docker exec iec-redis redis-cli FLUSHALL`.

### 3. Lấy code

```powershell
git clone https://github.com/bitmap1701/iec.git    # bỏ qua nếu đã có
cd iec
git fetch
git checkout feat/socket-hardening-redis-pubsub
dotnet build
```

`dotnet build` phải báo `0 Error(s)`.

---

## Phần A — Test trên một máy

### A1. Khởi động server

```powershell
dotnet run
```

Nhập `1`, rồi nhấn Enter hai lần (Redis mặc định, cổng 9000).

**Phải thấy:** dòng `[REDIS OK]`, dòng `[WEB UI READY]`, trình duyệt tự mở `http://localhost:5050` với bảng nhân sự trống.

**Tường lửa Windows** sẽ hỏi cho phép ứng dụng: tích **Private networks** rồi Allow. Bỏ qua bước này thì máy khác không nối vào được (Phần B).

Giữ nguyên terminal này suốt các bài sau.

### A2. Tạo nhân viên từ dashboard

Ở khung bên phải, mục "Mở kết nối nhân viên mới": nhập `NV01`, tên, phòng rồi bấm **+ Mở TcpClient mới**. Làm lại với `NV02`.

**Phải thấy:** hai dòng trong bảng, nhãn **● Online**, thanh TTL tụt xuống rồi đầy lại mỗi 5 giây. Sau khoảng 10 giây cột RTT hiện một con số (ms) thay cho dấu `-`.

### A3. Client console tự tìm server

Mở terminal thứ hai trong thư mục `iec`:

```powershell
dotnet run
```

Nhập `2`, nhấn Enter ở ô IP, rồi nhập `NV03`, tên, phòng.

**Phải thấy:** `[UDP DISCOVERY] Đã tìm thấy Admin Server tại địa chỉ: <IP>:9000`, sau đó `LOGIN_OK`. Dashboard có thêm `NV03` và một dòng log `[UDP-9001]`.

Nếu thấy `Không có Server nào trả lời trong mạng LAN`: nhấn Enter để dùng `127.0.0.1` và **ghi lại là tự dò thất bại**. Đây là điểm mình muốn biết nhất trên Windows.

### A4. Chat chung và nhắn riêng

Ở terminal client (NV03): chọn `4`, nhấn Enter ở ô người nhận, gõ một câu có dấu tiếng Việt. Rồi chọn `4` lần nữa, người nhận `NV01`, gõ một câu khác.

**Phải thấy:**
- Tin chung hiện ở terminal dạng `[CHAT TỪ NV03]: …` và trong khung "Lịch sử chat", **dấu tiếng Việt không bị vỡ**.
- Tin riêng: terminal báo `DM_OK`. Trên dashboard chọn NV01, mục "Tin nhận cuối" hiện tin đó; chọn NV02 thì không có.
- Log có các dòng `[PUBSUB]`.

### A5. Đóng kết nối đột ngột

Ở terminal client (NV03) nhấn `Ctrl+C`, hoặc đóng hẳn cửa sổ.

**Phải thấy:** NV03 chuyển **Offline trong 1–2 giây**, log có dòng `[DROP]`.

### A6. Rớt mạng không báo trước

Trên dashboard chọn NV01, bấm **A. Rớt mạng: im lặng, không FIN**.

**Phải thấy:** NV01 vẫn Online, thanh TTL tụt dần không đầy lại. Sau khoảng 15 giây: nhãn thành **Offline** và log có dòng `[HALF-OPEN] NV01 im lặng quá 15s`.

Bấm **Kết nối lại TCP** để NV01 online trở lại.

### A7. Chống spam

Chọn NV02, bấm **B. Spam 30 dòng lệnh**.

**Phải thấy:** nhãn NV02 thành **BLOCKED** kèm số giây đếm ngược từ 30, log có dòng `[SECOPS] [RATE-LIMIT BLOCKED]`. Hết 30 giây bấm **Kết nối lại TCP** thì Online lại.

Thử thêm ở client console: đăng nhập `NV04`, chọn `6`. Terminal phải in `ERR|RATE_LIMIT_BLOCKED`, rồi `Đã mất kết nối TCP với Server! Nhấn Enter để thoát.` và chương trình kết thúc sau khi nhấn Enter.

### A8. Hai server, một Redis

Mở terminal mới trong thư mục `iec`:

```powershell
dotnet run
```

Nhập `1`, Enter ở ô Redis, nhập cổng `9100`. Dashboard thứ hai mở ở `http://localhost:5150`.

1. Trên dashboard `5150`, tạo nhân viên `NV09`.
2. Trên dashboard `5050`, chọn NV01, gửi một tin chat tới `ALL`.

**Phải thấy:** cả hai dashboard liệt kê đủ mọi nhân viên. Trên `5150`, chọn NV09: "Tin nhận cuối" là tin của NV01. Log của `5150` có dòng `[PUBSUB] … đẩy xuống 1 socket của Server :9100`.

Log của server thứ hai có thể có dòng `[UDP-9001] Không mở được cổng UDP Discovery`. Đó là bình thường: cổng UDP 9001 đã thuộc về server đầu tiên.

### A9. Server tắt đi bật lại

Ở terminal của server `9000`: gõ `0` rồi Enter. Chạy lại `dotnet run`, chọn `1`, Enter hai lần.

**Phải thấy:** dashboard `5050` vẫn còn danh sách nhân viên và lịch sử chat cũ.

### A10. Redis chết giữa chừng

```powershell
docker stop iec-redis
```

Đợi khoảng 10 giây, rồi `docker start iec-redis`.

**Phải thấy:** các terminal server không bị văng. Nhân viên đang nối bị ngắt (log có dòng `[ERROR]`). Sau khi Redis lên lại, bấm **Kết nối lại TCP** (hoặc tạo nhân viên mới) thì Online và chat bình thường. Danh sách cũ còn hay mất tùy Redis có kịp ghi xuống đĩa lúc tắt hay không; cả hai đều chấp nhận được.

---

## Phần B — Test hai máy với Khôi (macOS)

Hai máy phải cùng một mạng Wi-Fi. Mạng trường thường chặn các máy thấy nhau; nếu không được thì dùng hotspot điện thoại. Chỉ máy chạy server cần Redis.

### B1. Windows làm server, Mac làm client

1. Hoà chạy server như bài A1 (nhớ đã cho phép tường lửa). Đọc địa chỉ ở góc trên dashboard: `Server TCP <IP>:9000`.
2. Khôi chạy client trên Mac, nhấn Enter ở ô IP để tự dò.

**Phải thấy:** máy Khôi in `Đã tìm thấy Admin Server tại địa chỉ: <IP của Windows>:9000`; dashboard của Hoà có thêm nhân viên của Khôi với IP là IP máy Mac.

Nếu tự dò không ra, Khôi gõ thẳng IP của máy Windows. Nếu gõ IP vẫn không nối được: tường lửa Windows đang chặn cổng TCP 9000.

### B2. Rút mạng thật

Khi Khôi đang Online, Khôi **tắt Wi-Fi** (không tắt chương trình).

**Phải thấy:** trên dashboard của Hoà, Khôi vẫn Online, thanh TTL tụt dần, khoảng 15 giây sau chuyển Offline với log `[HALF-OPEN]`.

### B3. Đổi vai

Khôi chạy server trên Mac, Hoà chạy client trên Windows và nhấn Enter để tự dò. Lặp lại B1 và B2 với vai ngược lại (Hoà tắt Wi-Fi).

---

## Ghi lại kết quả

| Bài | Đạt? | Ghi chú |
| :--- | :---: | :--- |
| Build `0 Error(s)` | | |
| A1 Khởi động server | | |
| A2 Nhân viên từ dashboard | | |
| A3 Tự tìm server | | |
| A4 Chat, nhắn riêng, tiếng Việt | | |
| A5 Đóng đột ngột | | |
| A6 Rớt mạng im lặng | | |
| A7 Chống spam | | |
| A8 Hai server | | |
| A9 Khởi động lại server | | |
| A10 Redis chết | | |
| B1 Windows server, Mac client | | |
| B2 Rút mạng thật | | |
| B3 Đổi vai | | |

Bài nào không đạt: chép lại vài dòng cuối trong khung "Nhật ký gói tin" và nội dung terminal lúc đó, gửi cho Khôi.

## Lỗi thường gặp

| Hiện tượng | Cách xử lý |
| :--- | :--- |
| `[LỖI REDIS] Không kết nối được Redis` | `docker start iec-redis`, rồi `docker ps` xem có cổng `6379` chưa |
| `[LỖI CỔNG MẠNG] Cổng TCP 9000 đang được sử dụng` | Còn một server cũ đang chạy; tắt nó, hoặc nhập cổng khác |
| Chữ tiếng Việt thành ký tự lạ trong terminal | Dùng Windows Terminal thay cho `cmd` |
| Trình duyệt không tự mở | Tự mở `http://localhost:5050` |
| Máy khác không nối vào được | Windows Security → Firewall → Allow an app → cho phép `iec` trên Private networks |
| Dashboard còn nhân viên của lần test trước | `docker exec iec-redis redis-cli FLUSHALL` rồi tải lại trang |
