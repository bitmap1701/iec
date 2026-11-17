using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace iec
{
    // Toàn bộ trạng thái dùng chung nằm trên Redis (một TCP server riêng, cổng 6379), không nằm trong RAM
    // của tiến trình này. Nhờ vậy nhiều Server IEC cùng trỏ về một Redis sẽ thấy chung nhân sự và chat,
    // và dữ liệu còn nguyên khi một Server IEC khởi động lại.
    public class RedisPresenceService
    {
        private readonly ConnectionMultiplexer _redis;
        private readonly IDatabase _db;
        public string Endpoint { get; }

        private const string StaffSetKey = "staff:ids";                         // SET: danh sách mã NV
        private const string ProfilePrefix = "staff:profile:";                  // HASH: hồ sơ
        private const string OnlinePrefix = "staff:online:";                    // STRING + TTL: trạng thái online
        private const string UptimeZSetKey = "staff:leaderboard:uptime";        // ZSET: xếp hạng uptime
        private const string ChatListKey = "staff:chat:history";                // LIST: 50 tin gần nhất
        private const string ChatChannel = "staff:chat:channel";                // PUB/SUB: phát tin tới mọi Server IEC
        private const string RateLimitPrefix = "security:ratelimit:";           // Bộ đếm + TTL
        private const string BlockedPrefix = "security:blocked:";

        // INCR và EXPIRE phải chạy trong một lệnh: nếu tách đôi mà Server chết giữa chừng thì khóa đếm sống mãi
        private const string RateLimitScript =
            "local c = redis.call('INCR', KEYS[1]) if c == 1 then redis.call('EXPIRE', KEYS[1], ARGV[1]) end return c";

        // Chỉ phục vụ dashboard của riêng tiến trình này nên không cần đưa lên Redis
        private readonly ConcurrentDictionary<string, int> _pingCounters = new();
        private readonly ConcurrentDictionary<string, string> _lastBroadcasts = new();

        // Ném RedisConnectionException nếu không kết nối được Redis
        public RedisPresenceService(string connectionString)
        {
            var options = ConfigurationOptions.Parse(connectionString);
            options.AbortOnConnectFail = true;
            options.ConnectTimeout = 1200;
            _redis = ConnectionMultiplexer.Connect(options);
            _db = _redis.GetDatabase();
            Endpoint = connectionString;
        }

        // Fixed-window rate limiter: tối đa MaxPacketsPerWindow dòng lệnh / RateWindowSeconds cho mỗi mã NV
        public async Task<(bool Allowed, double BlockedSecondsLeft)> CheckAndIncrementRateLimitAsync(string empId)
        {
            string blockKey = $"{BlockedPrefix}{empId}";
            var blockTtl = await _db.KeyTimeToLiveAsync(blockKey);
            if (blockTtl.HasValue && blockTtl.Value.TotalSeconds > 0)
            {
                return (false, Math.Ceiling(blockTtl.Value.TotalSeconds));
            }

            long count = (long)await _db.ScriptEvaluateAsync(RateLimitScript,
                new RedisKey[] { $"{RateLimitPrefix}{empId}" },
                new RedisValue[] { HrmProtocol.RateWindowSeconds });

            if (count > HrmProtocol.MaxPacketsPerWindow)
            {
                await _db.StringSetAsync(blockKey, "BLOCKED_FLOOD", TimeSpan.FromSeconds(HrmProtocol.BlockDurationSeconds));
                await _db.KeyDeleteAsync($"{OnlinePrefix}{empId}");
                return (false, HrmProtocol.BlockDurationSeconds);
            }
            return (true, 0);
        }

        public async Task RegisterLoginAsync(string empId, string fullName, string dept, string clientIp)
        {
            _pingCounters[empId] = 0;
            string profileKey = $"{ProfilePrefix}{empId}";

            await _db.SetAddAsync(StaffSetKey, empId);
            await _db.HashSetAsync(profileKey, new HashEntry[]
            {
                new("FullName", fullName),
                new("Department", dept),
                new("ClientIp", clientIp),
                new("LastSeen", DateTime.Now.ToString("HH:mm:ss"))
            });
            await _db.HashDeleteAsync(profileKey, "RttMs"); // RTT của phiên trước không còn đúng
            await _db.StringSetAsync($"{OnlinePrefix}{empId}", "Online", TimeSpan.FromSeconds(HrmProtocol.OnlineTtlSeconds));
            await _db.SortedSetIncrementAsync(UptimeZSetKey, empId, 0);
        }

        // rttMs = null khi client chưa đo được (chưa nhận PONG nào)
        public async Task RefreshHeartbeatAsync(string empId, double? rttMs)
        {
            _pingCounters.AddOrUpdate(empId, 1, (_, count) => count + 1);

            string onlineKey = $"{OnlinePrefix}{empId}";
            // EXPIRE giữ nguyên giá trị Online/Busy/Away; chỉ SET lại khi khóa đã hết hạn
            if (!await _db.KeyExpireAsync(onlineKey, TimeSpan.FromSeconds(HrmProtocol.OnlineTtlSeconds)))
                await _db.StringSetAsync(onlineKey, "Online", TimeSpan.FromSeconds(HrmProtocol.OnlineTtlSeconds));

            var fields = new List<HashEntry> { new("LastSeen", DateTime.Now.ToString("HH:mm:ss")) };
            if (rttMs.HasValue)
                fields.Add(new("RttMs", Math.Round(Math.Clamp(rttMs.Value, 0, 9999), 2).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await _db.HashSetAsync($"{ProfilePrefix}{empId}", fields.ToArray());

            // shortcut: cộng cứng 5s mỗi PING nên client gửi PING dày hơn 5s sẽ thổi phồng uptime;
            // muốn chính xác thì cộng theo thời gian thực giữa hai PING đo tại Server.
            await _db.SortedSetIncrementAsync(UptimeZSetKey, empId, HrmProtocol.HeartbeatIntervalMs / 1000.0);
        }

        public async Task UpdateStatusAsync(string empId, string newStatus)
        {
            await _db.StringSetAsync($"{OnlinePrefix}{empId}", newStatus, TimeSpan.FromSeconds(HrmProtocol.OnlineTtlSeconds));
            await _db.HashSetAsync($"{ProfilePrefix}{empId}", "LastSeen", DateTime.Now.ToString("HH:mm:ss"));
        }

        public async Task SetOfflineImmediatelyAsync(string empId)
        {
            await _db.KeyDeleteAsync($"{OnlinePrefix}{empId}");
            await _db.HashSetAsync($"{ProfilePrefix}{empId}", "LastSeen", DateTime.Now.ToString("HH:mm:ss"));
        }

        // Xóa mọi khóa của một nhân viên. Lịch sử chat giữ nguyên vì là nhật ký chung.
        public async Task RemoveStaffAsync(string empId)
        {
            await _db.SetRemoveAsync(StaffSetKey, empId);
            await _db.SortedSetRemoveAsync(UptimeZSetKey, empId);
            await _db.KeyDeleteAsync(new RedisKey[]
            {
                $"{ProfilePrefix}{empId}", $"{OnlinePrefix}{empId}", $"{RateLimitPrefix}{empId}", $"{BlockedPrefix}{empId}"
            });
            _pingCounters.TryRemove(empId, out _);
            _lastBroadcasts.TryRemove(empId, out _);
        }

        // Mã NV đã từng đăng nhập (có trong SET staff:ids) thì mới là người nhận hợp lệ
        public Task<bool> IsKnownStaffAsync(string empId) => _db.SetContainsAsync(StaffSetKey, empId);

        public Task<bool> IsOnlineAsync(string empId) => _db.KeyExistsAsync($"{OnlinePrefix}{empId}");

        // Lưu tin vào LIST (để REPLAY) rồi PUBLISH lên kênh: mọi Server IEC đang SUBSCRIBE sẽ tự đẩy tin
        // xuống các socket của mình. Server gửi không cần biết người nhận đang nối vào Server nào.
        public async Task PublishChatAsync(string sender, string target, string content)
        {
            string raw = $"{DateTime.Now:HH:mm:ss}|{HrmProtocol.Sanitize(sender, "SYSTEM")}|{HrmProtocol.Sanitize(target, "ALL")}|{HrmProtocol.Sanitize(content, "")}";
            await _db.ListLeftPushAsync(ChatListKey, raw);
            await _db.ListTrimAsync(ChatListKey, 0, 49);
            await _redis.GetSubscriber().PublishAsync(RedisChannel.Literal(ChatChannel), raw);
        }

        public void SubscribeChat(Func<ChatMessageEntry, Task> onMessage)
        {
            // ChannelMessageQueue xử lý tuần tự nên tin tới socket đúng thứ tự đã PUBLISH
            _redis.GetSubscriber().Subscribe(RedisChannel.Literal(ChatChannel)).OnMessage(async m =>
            {
                var entry = ParseChat(m.Message);
                if (entry != null) await onMessage(entry);
            });
        }

        private static ChatMessageEntry ParseChat(string raw)
        {
            string[] p = raw.Split('|', 4);
            return p.Length < 4 ? null : new ChatMessageEntry { Time = p[0], Sender = p[1], Target = p[2], Content = p[3] };
        }

        // Mới nhất đứng đầu
        public async Task<List<ChatMessageEntry>> GetRecentChatHistoryAsync(int count = 25)
        {
            RedisValue[] vals = await _db.ListRangeAsync(ChatListKey, 0, count - 1);
            return vals.Select(v => ParseChat(v)).Where(e => e != null).ToList();
        }

        public void RecordBroadcastForStaff(string empId, string message)
        {
            _lastBroadcasts[empId] = $"[{DateTime.Now:HH:mm:ss}] {message}";
        }

        public async Task<List<StaffMember>> GetAllStaffPresenceAsync(Func<string, bool> isSocketActiveFunc = null)
        {
            var result = new List<StaffMember>();
            foreach (var idVal in await _db.SetMembersAsync(StaffSetKey))
            {
                string empId = idVal.ToString();
                var dict = (await _db.HashGetAllAsync($"{ProfilePrefix}{empId}")).ToStringDictionary();
                var onlineVal = await _db.StringGetWithExpiryAsync($"{OnlinePrefix}{empId}");
                var blockTtl = await _db.KeyTimeToLiveAsync($"{BlockedPrefix}{empId}");
                double? uptimeScore = await _db.SortedSetScoreAsync(UptimeZSetKey, empId);

                bool isBlocked = blockTtl.HasValue && blockTtl.Value.TotalSeconds > 0;
                bool isOnline = !isBlocked && onlineVal.Value.HasValue;
                double? rtt = dict.TryGetValue("RttMs", out string rttStr) &&
                              double.TryParse(rttStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double r) ? r : null;

                result.Add(new StaffMember
                {
                    EmpId = empId,
                    FullName = dict.TryGetValue("FullName", out string name) ? name : empId,
                    Department = dict.TryGetValue("Department", out string dept) ? dept : "",
                    ClientIp = dict.TryGetValue("ClientIp", out string ip) ? ip : "",
                    LastSeen = dict.TryGetValue("LastSeen", out string seen) ? seen : "N/A",
                    RttMs = rtt,
                    UptimeSeconds = uptimeScore ?? 0,
                    IsOnline = isOnline,
                    IsRateLimited = isBlocked,
                    BlockedRemainingSeconds = isBlocked ? Math.Ceiling(blockTtl.Value.TotalSeconds) : 0,
                    CurrentStatus = isBlocked ? "BLOCKED" : (isOnline ? onlineVal.Value.ToString() : "Offline"),
                    RemainingTtlSeconds = isOnline && onlineVal.Expiry.HasValue ? Math.Max(0, Math.Round(onlineVal.Expiry.Value.TotalSeconds, 0)) : 0,
                    PingCount = _pingCounters.TryGetValue(empId, out int p) ? p : 0,
                    LastBroadcast = _lastBroadcasts.TryGetValue(empId, out string b) ? b : null,
                    IsSocketConnected = isSocketActiveFunc != null && isSocketActiveFunc(empId)
                });
            }
            return result.OrderByDescending(x => x.UptimeSeconds).ThenBy(x => x.EmpId).ToList();
        }
    }
}
