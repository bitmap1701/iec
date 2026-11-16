# Hướng dẫn chạy thử trên macOS

Nhánh cần dùng: `feat/socket-hardening-redis-pubsub`. Làm lần lượt từ trên xuống, mỗi bài test mất 1–2 phút.

## Project này để làm gì?

Hãy nghĩ tới **chấm xanh "đang online" và khung chat của Zalo/Slack**, nhưng thu nhỏ cho một công ty dùng mạng LAN.

- **Server** là tổng đài. Nhân viên mở chương trình, đăng nhập vào server, từ đó server biết ai đang online và chuyển tin nhắn giữa mọi người.
- **Client** là chương trình của từng nhân viên: đăng nhập, đổi trạng thái (Online/Busy/Away), chat chung, nhắn riêng.
- **Dashboard web** là màn hình của quản trị viên: xem ai online, xem log, và có sẵn các nút để tạo "nhân viên giả" phục vụ demo.
- **Redis** là tấm bảng trắng dùng chung. Server ghi mọi thứ lên đó (ai online, lịch sử chat) thay vì tự nhớ. Nhờ vậy server tắt đi bật lại không mất dữ liệu, và nhiều server đọc chung một bảng được.

Cái khó, và cũng là thứ thầy chấm, nằm ở phần mạng:

1. **Làm sao biết một người đã biến mất?** Nếu họ bấm đăng xuất thì dễ. Nhưng nếu máy họ sập nguồn hay rớt Wi-Fi thì không ai báo cho server cả. Giải pháp: client gửi "tôi còn sống" (`PING`) mỗi 5 giây; server im lặng chờ, quá 15 giây không nghe gì thì coi như người đó đã mất kết nối.
2. **Client tìm server ở đâu?** IP trong mạng Wi-Fi đổi liên tục. Client hét lên cả mạng "server ở đâu?" (UDP broadcast), server trả lời "tôi đây".
3. **Chống phá.** Không cho giả danh người khác, không cho spam làm nghẽn server.
4. **Nhiều server cùng chạy.** Hai server khác nhau vẫn chuyển tin cho nhau được nhờ Redis.

Các bài test bên dưới lần lượt cho bạn thấy từng điểm này bằng mắt.

---

## Chuẩn bị (một lần)

.NET 9 và Redis đã được cài trên máy này bằng Homebrew. Bạn chỉ cần:

```bash
# 1. Cho terminal biết dotnet nằm ở đâu (thêm vào ~/.zshrc rồi mở terminal mới)
echo 'export PATH="/opt/homebrew/opt/dotnet@9/bin:$PATH"' >> ~/.zshrc

# 2. Bật Redis (tự chạy lại mỗi lần mở máy)
brew services start redis

# 3. Kiểm tra
dotnet --version        # phải ra 9.0.x
redis-cli ping          # phải ra PONG
```

Muốn xóa sạch dữ liệu test cũ để bắt đầu lại: `redis-cli FLUSHALL`. Lệnh này xóa **toàn bộ** dữ liệu trong Redis trên máy, chỉ dùng khi bạn không chạy project nào khác trên Redis này.

---

## Phần A — Test trên một máy

### A1. Khởi động server

```bash
cd ~/Desktop/iec
dotnet run
```

Nhập `1`, rồi nhấn Enter hai lần (Redis mặc định, cổng 9000).

**Phải thấy:** dòng `[REDIS OK]`, dòng `[WEB UI READY]`, trình duyệt tự mở `http://localhost:5050` với bảng nhân sự trống. macOS có thể hỏi cho phép nhận kết nối mạng: chọn Allow.

Giữ nguyên terminal này suốt các bài test sau.

### A2. Tạo nhân viên từ dashboard

Ở khung bên phải, mục "Mở kết nối nhân viên mới": nhập `NV01`, tên, phòng rồi bấm **+ Mở TcpClient mới**. Làm lại với `NV02`.

**Phải thấy:** hai dòng trong bảng, nhãn **● Online**, thanh TTL cứ tụt xuống rồi đầy lại mỗi 5 giây. Sau khoảng 10 giây cột RTT hiện một con số (ms) thay cho dấu `-`.

*Ý nghĩa:* thanh TTL đầy lại chính là lúc `PING` tới nơi.

### A3. Client console tự tìm server

Mở terminal thứ hai:

```bash
cd ~/Desktop/iec
dotnet run
```

Nhập `2`, nhấn Enter ở ô IP, rồi nhập `NV03`, tên, phòng.

**Phải thấy:** `[UDP DISCOVERY] Đã tìm thấy Admin Server tại địa chỉ: <IP>:9000`, sau đó `LOGIN_OK`. Trên dashboard có thêm `NV03` và một dòng log `[UDP-9001]`.

### A4. Chat chung và nhắn riêng

Ở terminal client (NV03): chọn `4`, nhấn Enter ở ô người nhận, gõ một câu. Rồi chọn `4` lần nữa, người nhận `NV01`, gõ một câu khác.

**Phải thấy:**
- Tin chung hiện ngay ở terminal NV03 dạng `[CHAT TỪ NV03]: …` và trong khung "Lịch sử chat".
- Tin riêng: terminal báo `DM_OK`. Trên dashboard chọn NV01, mục "Tin nhận cuối" hiện tin đó; chọn NV02 thì không có.
- Log có các dòng `[PUBSUB]`.

### A5. Đóng kết nối đột ngột

Ở terminal client (NV03) nhấn `Ctrl+C`.

**Phải thấy:** NV03 chuyển **Offline gần như ngay lập tức** (trong 1–2 giây), log có dòng `[DROP] … đóng (EOF)`.

### A6. Rớt mạng không báo trước (bài quan trọng nhất)

Trên dashboard chọn NV01, bấm **A. Rớt mạng: im lặng, không FIN**.

**Phải thấy:** NV01 **vẫn Online**, thanh TTL tụt dần không đầy lại, chuyển đỏ. Sau khoảng 15 giây: nhãn thành **Offline** và log có dòng `[HALF-OPEN] NV01 im lặng quá 15s`.

*Ý nghĩa:* đây là tình huống máy sập nguồn. So với A5, lần này server không được báo gì nên phải tự suy ra sau 15 giây.

Bấm **Kết nối lại TCP** để NV01 online trở lại.

### A7. Chống spam

Chọn NV02, bấm **B. Spam 30 dòng lệnh**.

**Phải thấy:** nhãn NV02 thành **BLOCKED** kèm số giây đếm ngược từ 30, ô "Bị khóa spam" tăng lên 1, log có dòng `[SECOPS] [RATE-LIMIT BLOCKED]`. Bấm **Kết nối lại TCP** trong lúc còn bị khóa thì không có tác dụng; hết 30 giây bấm lại mới Online.

### A8. Chống giả danh

Mở terminal thứ ba:

```bash
nc 127.0.0.1 9000
```

Gõ từng dòng rồi Enter (đừng để quá 15 giây giữa hai dòng, server sẽ ngắt):

```text
LOGIN|T1|Test|QA
CHAT|NV01|toi la NV01
CHAT|T1|toi la T1
```

**Phải thấy:** dòng thứ hai bị trả `ERR|ID_MISMATCH`, dòng thứ ba được phát đi. Thoát bằng `Ctrl+C`.

Thử thêm: chạy lại `nc 127.0.0.1 9000` và gõ ngay `CHAT|NV01|x`. Server trả `ERR|NOT_LOGGED_IN` rồi đóng kết nối.

### A9. Hai server, một Redis

Mở terminal mới:

```bash
cd ~/Desktop/iec
dotnet run
```

Nhập `1`, Enter ở ô Redis, nhập cổng `9100`. Dashboard thứ hai mở ở `http://localhost:5150`.

1. Trên dashboard `5150`, tạo nhân viên `NV09`.
2. Trên dashboard `5050`, chọn NV01, gửi một tin chat tới `ALL`.

**Phải thấy:** cả hai dashboard đều liệt kê đủ mọi nhân viên. Trên `5150`, chọn NV09: "Tin nhận cuối" là tin của NV01 dù hai người nối vào hai server khác nhau. Log của `5150` có dòng `[PUBSUB] … đẩy xuống 1 socket của Server :9100`.

### A10. Server tắt đi bật lại

Ở terminal của server `9000`: gõ `0` rồi Enter để dừng. Chạy lại `dotnet run`, chọn `1`, Enter hai lần.

**Phải thấy:** dashboard `5050` vẫn có đủ danh sách nhân viên và lịch sử chat cũ. Các nhân viên từng nối vào server này chuyển Offline sau tối đa 15 giây.

*Ý nghĩa:* dữ liệu nằm trên Redis, không nằm trong server.

### A11. Redis chết giữa chừng

```bash
brew services stop redis
```

Đợi khoảng 10 giây, rồi:

```bash
brew services start redis
```

**Phải thấy:** các terminal server không bị văng. Nhân viên đang nối bị ngắt (log có dòng `[ERROR]`). Sau khi Redis lên lại, bấm **Kết nối lại TCP** thì Online và chat bình thường. Danh sách cũ còn hay mất tùy Redis có kịp ghi xuống đĩa lúc tắt hay không; cả hai đều chấp nhận được.

---

## Phần B — Test hai máy với bạn Hoà (Windows)

Hai máy phải cùng một mạng Wi-Fi. Mạng trường thường chặn các máy thấy nhau; nếu không được thì dùng hotspot điện thoại.

Chỉ máy chạy server cần Redis.

### B1. Mac làm server, Windows làm client

1. Bạn chạy server như bài A1. Đọc địa chỉ ở góc trên dashboard: `Server TCP <IP>:9000`.
2. Bạn Hoà chạy client, nhấn Enter ở ô IP để tự dò.

**Phải thấy:** máy Hoà in `Đã tìm thấy Admin Server tại địa chỉ: <IP của Mac>:9000`; dashboard của bạn có thêm nhân viên của Hoà với IP là IP máy Windows.

Nếu tự dò không ra, bảo Hoà gõ thẳng IP của Mac. Ghi lại là tự dò thất bại.

### B2. Rút mạng thật

Khi Hoà đang Online, bảo Hoà **tắt Wi-Fi** (không tắt chương trình).

**Phải thấy:** trên dashboard của bạn, Hoà vẫn Online, thanh TTL tụt dần, khoảng 15 giây sau chuyển Offline với log `[HALF-OPEN]`.

### B3. Đổi vai

Hoà chạy server trên Windows, bạn chạy client trên Mac và nhấn Enter để tự dò. Lặp lại B1 và B2 với vai ngược lại (bạn tắt Wi-Fi).

---

## Ghi lại kết quả

| Bài | Đạt? | Ghi chú |
| :--- | :---: | :--- |
| A1 Khởi động server | | |
| A2 Nhân viên từ dashboard | | |
| A3 Tự tìm server | | |
| A4 Chat và nhắn riêng | | |
| A5 Đóng đột ngột | | |
| A6 Rớt mạng im lặng | | |
| A7 Chống spam | | |
| A8 Chống giả danh | | |
| A9 Hai server | | |
| A10 Khởi động lại server | | |
| A11 Redis chết | | |
| B1 Mac server, Windows client | | |
| B2 Rút mạng thật | | |
| B3 Đổi vai | | |

Bài nào không đạt: chép lại vài dòng cuối trong khung "Nhật ký gói tin" và nội dung terminal lúc đó.

## Lỗi thường gặp

| Hiện tượng | Cách xử lý |
| :--- | :--- |
| `command not found: dotnet` | Chưa có dòng `export PATH=…` trong `~/.zshrc`, hoặc chưa mở terminal mới |
| `[LỖI REDIS] Không kết nối được Redis` | `brew services start redis` |
| `[LỖI CỔNG MẠNG] Cổng TCP 9000 đang được sử dụng` | Còn một server cũ đang chạy; tắt nó, hoặc nhập cổng khác |
| Dashboard còn nhân viên của lần test trước | Chọn nhân viên rồi bấm **Xóa nhân viên khỏi Redis**. Muốn xóa sạch tất cả: `redis-cli FLUSHALL` rồi tải lại trang |
| Client tự dò không ra server ở máy khác | Kiểm tra cùng Wi-Fi, cho phép tường lửa, hoặc gõ IP bằng tay |
