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
        public string LastBroadcast { get; set; }
        public bool IsSocketConnected { get; set; }
    }

    public class TcpLogEntry
    {
        public string Time { get; set; }
        public string Tag { get; set; }
        public string Message { get; set; }
    }

    public static class HrmProtocol
    {
        public const int DefaultPort = 9000;
        public const int WebUiPort = 5050;
        public const int HeartbeatIntervalMs = 5000; // Client gửi PING mỗi 5 giây
        public const int OnlineTtlSeconds = 15;      // Khóa Redis tự hủy sau 15 giây nếu mất PING

        public const string CmdLogin = "LOGIN";
        public const string CmdPing = "PING";
        public const string CmdStatus = "STATUS";
        public const string CmdLogout = "LOGOUT";

        // Chống lỗi Delimiter Injection (loại bỏ ký tự '|' hoặc xuống dòng làm hỏng khung gói tin TCP)
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