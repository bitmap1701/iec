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
        public const int HeartbeatIntervalMs = 5000; // Client gá»­i PING má»—i 5 giÃ¢y
        public const int OnlineTtlSeconds = 15;      // KhÃ³a Redis tá»± há»§y sau 15 giÃ¢y náº¿u máº¥t PING

        public const string CmdLogin = "LOGIN";
        public const string CmdPing = "PING";
        public const string CmdStatus = "STATUS";
        public const string CmdLogout = "LOGOUT";
    }
}
