# PulseHRM Mini â€” Há»‡ thá»‘ng Quáº£n trá»‹ & Äá»‹nh vá»‹ Tráº¡ng thÃ¡i NhÃ¢n sá»± Thá»i gian thá»±c (TCP Socket & Redis)

> **Há»c pháº§n:** Láº­p trÃ¬nh máº¡ng (Network Programming â€” `IEC_CNTT`)  
> **Ná»n táº£ng:** C# .NET 9.0 Cross-Platform (Cháº¡y Ä‘á»“ng nháº¥t trÃªn **Windows** & **macOS**)  
> **CÃ´ng nghá»‡ cá»‘t lÃµi:** `System.Net.Sockets` (`TcpListener` / `TcpClient` / `NetworkStream`), `StackExchange.Redis`, `HttpListener`  
> **Kiáº¿n trÃºc:** Multi-threaded Asynchronous TCP Socket Server káº¿t há»£p Redis In-Memory State (Set, Hash, String TTL)

---

## 1. Tá»•ng quan BÃ i toÃ¡n & Má»¥c tiÃªu Äá»“ Ã¡n

Trong cÃ¡c há»‡ thá»‘ng quáº£n lÃ½ nhÃ¢n sá»± ná»™i bá»™ (LAN), bÃ i toÃ¡n khÃ³ nháº¥t cá»§a táº§ng giao váº­n máº¡ng lÃ  phÃ¡t hiá»‡n chÃ­nh xÃ¡c tráº¡ng thÃ¡i **Online / Offline thá»±c táº¿** khi xáº£y ra sá»± cá»‘ váº­t lÃ½:
- Náº¿u nhÃ¢n viÃªn báº¥m nÃºt **ÄÄƒng xuáº¥t (`LOGOUT`)**, á»©ng dá»¥ng gá»­i gÃ³i tin bÃ¡o cho Server biáº¿t Ä‘á»ƒ ngáº¯t káº¿t ná»‘i ngay láº­p tá»©c.
- NhÆ°ng náº¿u mÃ¡y tÃ­nh nhÃ¢n viÃªn **sáº­p nguá»“n Ä‘á»™t ngá»™t, Ä‘á»©t cÃ¡p LAN, rá»›t Wi-Fi hoáº·c bá»‹ táº¯t ngang tiáº¿n trÃ¬nh (`Kill Process`)**, há»‡ Ä‘iá»u hÃ nh phÃ­a Client khÃ´ng ká»‹p gá»­i gÃ³i tin `TCP FIN/RST`. Khi Ä‘Ã³, Socket phÃ­a Server rÆ¡i vÃ o tráº¡ng thÃ¡i **Half-Open Connection (Káº¿t ná»‘i treo má»™t ná»­a)**, dáº«n Ä‘áº¿n hiá»‡n tÆ°á»£ng **"Online áº£o" (Ghost Online)**.

**PulseHRM Mini** giáº£i quyáº¿t triá»‡t Ä‘á»ƒ váº¥n Ä‘á» nÃ y báº±ng sá»± káº¿t há»£p giá»¯a **Láº­p trÃ¬nh Socket (Heartbeat 5s)** vÃ  **CÆ¡ cháº¿ KhÃ³a tá»± há»§y (TTL 15s) cá»§a Redis**:
1. Má»—i `Employee Client` duy trÃ¬ káº¿t ná»‘i TCP tá»›i Server á»Ÿ cá»•ng `9000` vÃ  Ä‘á»‹nh ká»³ báº¯n gÃ³i tin nhá»‹p tim `PING|<MÃ£_NV>` má»—i **5 giÃ¢y**.
2. Má»—i láº§n nháº­n Ä‘Æ°á»£c `PING`, `Admin Server` gá»i lá»‡nh `EXPIRE` gia háº¡n khÃ³a `staff:online:<MÃ£_NV>` trÃªn Redis lÃªn **15 giÃ¢y**.
3. Náº¿u Client máº¥t káº¿t ná»‘i Ä‘á»™t ngá»™t, luá»“ng `PING` dá»«ng láº¡i -> ÄÃºng **15 giÃ¢y** sau, khÃ³a trÃªn Redis tá»± Ä‘á»™ng bá»‘c hÆ¡i khá»i RAM -> NhÃ¢n viÃªn tá»± Ä‘á»™ng chuyá»ƒn vá» **`OFFLINE`** mÃ  khÃ´ng lÃ m treo Server.

---

## 2. PhÃ¢n tÃ­ch Chi tiáº¿t Cáº¥u trÃºc ThÆ° má»¥c: Chá»©c nÄƒng, ÄÃ³ng gÃ³p & TÃ¡c Ä‘á»™ng (Impact)

```text
iec/
â”œâ”€â”€ iec.csproj       # [Cáº¥u hÃ¬nh] Khai bÃ¡o SDK .NET 9.0 & thÆ° viá»‡n StackExchange.Redis
â”œâ”€â”€ StaffMember.cs            # [Data Model & Protocol] Äá»‹nh nghÄ©a thá»±c thá»ƒ & tá»« Ä‘iá»ƒn giao thá»©c TCP
â”œâ”€â”€ RedisPresenceService.cs   # [Data & State Layer] Quáº£n lÃ½ Redis (Set, Hash, TTL) & In-Memory Fallback
â”œâ”€â”€ HrmTcpServer.cs           # [Core Network Server] LÃµi TcpListener Ä‘a luá»“ng (Cá»•ng 9000)
â”œâ”€â”€ HrmTcpClient.cs           # [Core Network Client] LÃµi TcpClient & Bá»™ phÃ¡t nhá»‹p tim (Heartbeat 5s)
â”œâ”€â”€ WebDashboardHost.cs       # [Web Cockpit & Bridge] Giao diá»‡n giÃ¡m sÃ¡t (Cá»•ng 5050) & Cáº§u ná»‘i TCP tháº­t
â”œâ”€â”€ Program.cs                # [Entry Point] Bá»™ Ä‘iá»u phá»‘i khá»Ÿi cháº¡y Ä‘a ná»n táº£ng Windows / macOS
â”œâ”€â”€ .gitignore                # [Git Config] Cháº·n Ä‘áº©y thÆ° má»¥c biÃªn dá»‹ch táº¡m (bin/, obj/) lÃªn GitHub
â””â”€â”€ README.md                 # [Documentation] TÃ i liá»‡u kiáº¿n trÃºc, giao thá»©c vÃ  ká»‹ch báº£n kiá»ƒm thá»­
