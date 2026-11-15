using System;

namespace iec
{
    public class StaffMember
    {
        public string EmpId { get; set; }
        public string FullName { get; set; }
        public string Department { get; set; }
        public string ClientIp { get; set; }
        public string LastSeen { get; set; }
        public bool IsOnline { get; set; }
        public string CurrentStatus { get; set; }
        public double RemainingTtlSeconds { get; set; }
        public int PingCount { get; set; }
        public double RttMs { get; set; }
        public double UptimeSeconds { get; set; }
        public string CurrentTask { get; set; }
        public string LastBroadcast { get; set; }
        public bool IsSocketConnected { get; set; }
        public bool IsRateLimited { get; set; }
        public double BlockedRemainingSeconds { get; set; }
    }

    public class TcpLogEntry
    {
        public string Time { get; set; }
        public string Tag { get; set; }
        public string Message { get; set; }
    }

    public class ChatMessageEntry
    {
        public string Time { get; set; }
        public string Sender { get; set; }
        public string Target { get; set; }
        public string Content { get; set; }
    }

    public static class HrmProtocol
    {
        public const int DefaultPort = 9000;          // Cổng TCP chính
        public const int UdpDiscoveryPort = 9001;     // Cổng UDP Broadcast tự tìm Server trong LAN
        public const int WebUiPort = 5050;            // Cổng giao diện Web Cockpit
        public const int HeartbeatIntervalMs = 5000;  // Chu kỳ nhịp tim 5 giây
        public const int OnlineTtlSeconds = 15;       // Khóa Redis tự hủy sau 15 giây

        // Cấu hình Redis Sliding/Fixed-Window Rate Limiter chống Spam gói tin TCP
        public const int MaxPacketsPerWindow = 25;    // Tối đa 25 gói tin / 10 giây
        public const int RateWindowSeconds = 10;
        public const int BlockDurationSeconds = 30;   // Khóa 30 giây nếu vi phạm

        public const string CmdLogin = "LOGIN";
        public const string CmdPing = "PING";
        public const string CmdStatus = "STATUS";
        public const string CmdTask = "TASK";
        public const string CmdChat = "CHAT";
        public const string CmdDm = "DM";
        public const string CmdLogout = "LOGOUT";
        public const string UdpDiscoverRequest = "DISCOVER_IEC_SERVER";
        public const string UdpDiscoverResponse = "IEC_SERVER_HERE";

        public static string Sanitize(string input, string fallback = "N/A")
        {
            if (string.IsNullOrWhiteSpace(input)) return fallback;
            return input.Trim().Trim('\uFEFF')
                        .Replace("|", "/")
                        .Replace("\r", " ")
                        .Replace("\n", " ");
        }
    }
}