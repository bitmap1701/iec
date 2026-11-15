using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace iec
{
    public class RedisPresenceService
    {
        private readonly ConnectionMultiplexer _redis;
        private readonly IDatabase _db;
        public bool IsUsingFallbackMemory { get; private set; } = false;

        // 5 Cấu trúc dữ liệu Redis cốt lõi + Khóa Rate-Limit
        private const string StaffSetKey = "staff:ids";                         // 1. Redis SET
        private const string ProfilePrefix = "staff:profile:";                  // 2. Redis HASH
        private const string OnlinePrefix = "staff:online:";                    // 3. Redis STRING + TTL
        private const string UptimeZSetKey = "staff:leaderboard:uptime";        // 4. Redis SORTED SET (ZSET)
        private const string ChatListKey = "staff:chat:history";                // 5. Redis LIST
        private const string RateLimitPrefix = "security:ratelimit:";           // 6. Redis ATOMIC COUNTER + TTL
        private const string BlockedPrefix = "security:blocked:";

        // Bộ nhớ giả lập đầy đủ 5 cấu trúc Redis khi máy chưa cài Redis Server cổng 6379
        private readonly ConcurrentDictionary<string, byte> _memSetIds = new();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _memHashes = new();
        private readonly ConcurrentDictionary<string, (string Status, DateTime ExpireAt)> _memOnlineTtl = new();
        private readonly ConcurrentDictionary<string, double> _memZSetUptime = new();
        private readonly LinkedList<ChatMessageEntry> _memChatList = new();
        private readonly object _chatLock = new();

        private readonly ConcurrentDictionary<string, (int Count, DateTime WindowEnd)> _memRateLimits = new();
        private readonly ConcurrentDictionary<string, DateTime> _memBlockedUntil = new();
        private readonly ConcurrentDictionary<string, int> _pingCounters = new();
        private readonly ConcurrentDictionary<string, string> _lastBroadcasts = new();

        public RedisPresenceService(string connectionString)
        {
            try
            {
                var options = ConfigurationOptions.Parse(connectionString);
                options.AbortOnConnectFail = true;
                options.ConnectTimeout = 1200;
                _redis = ConnectionMultiplexer.Connect(options);
                _db = _redis.GetDatabase();
            }
            catch
            {
                IsUsingFallbackMemory = true;
            }
        }

        public async Task<bool> CheckConnectionAsync()
        {
            if (IsUsingFallbackMemory) return true;
            try
            {
                TimeSpan latency = await _db.PingAsync();
                return latency.TotalMilliseconds >= 0;
            }
            catch
            {
                IsUsingFallbackMemory = true;
                return true;
            }
        }

        // Kiểm tra & Đếm tần suất gói tin bằng Redis Atomic Counter (Chống TCP Flood)
        public async Task<(bool Allowed, double BlockedSecondsLeft)> CheckAndIncrementRateLimitAsync(string empId)
        {
            if (string.IsNullOrWhiteSpace(empId)) return (true, 0);
            empId = HrmProtocol.Sanitize(empId, "UNKNOWN").ToUpperInvariant();

            if (!IsUsingFallbackMemory)
            {
                string blockKey = $"{BlockedPrefix}{empId}";
                var blockTtl = await _db.KeyTimeToLiveAsync(blockKey);
                if (blockTtl.HasValue && blockTtl.Value.TotalSeconds > 0)
                {
                    return (false, Math.Ceiling(blockTtl.Value.TotalSeconds));
                }

                string rateKey = $"{RateLimitPrefix}{empId}";
                long count = await _db.StringIncrementAsync(rateKey);
                if (count == 1)
                {
                    await _db.KeyExpireAsync(rateKey, TimeSpan.FromSeconds(HrmProtocol.RateWindowSeconds));
                }

                if (count > HrmProtocol.MaxPacketsPerWindow)
                {
                    await _db.StringSetAsync(blockKey, "BLOCKED_FLOOD", TimeSpan.FromSeconds(HrmProtocol.BlockDurationSeconds));
                    await _db.KeyDeleteAsync($"{OnlinePrefix}{empId}");
                    return (false, HrmProtocol.BlockDurationSeconds);
                }
                return (true, 0);
            }

            DateTime now = DateTime.UtcNow;
            if (_memBlockedUntil.TryGetValue(empId, out DateTime blockedUntil))
            {
                if (blockedUntil > now)
                    return (false, Math.Ceiling((blockedUntil - now).TotalSeconds));
                _memBlockedUntil.TryRemove(empId, out _);
            }

            var win = _memRateLimits.AddOrUpdate(empId,
                _ => (1, now.AddSeconds(HrmProtocol.RateWindowSeconds)),
                (_, old) => old.WindowEnd <= now
                    ? (1, now.AddSeconds(HrmProtocol.RateWindowSeconds))
                    : (old.Count + 1, old.WindowEnd));

            if (win.Count > HrmProtocol.MaxPacketsPerWindow)
            {
                _memBlockedUntil[empId] = now.AddSeconds(HrmProtocol.BlockDurationSeconds);
                _memOnlineTtl.TryRemove(empId, out _);
                return (false, HrmProtocol.BlockDurationSeconds);
            }
            return (true, 0);
        }

        public async Task RegisterLoginAsync(string empId, string fullName, string dept, string clientIp, string initialTask = "Đang trực ca")
        {
            empId = HrmProtocol.Sanitize(empId, "NV01").ToUpperInvariant();
            fullName = HrmProtocol.Sanitize(fullName, "Nhân viên");
            dept = HrmProtocol.Sanitize(dept, "IT");
            initialTask = HrmProtocol.Sanitize(initialTask, "Đang trực ca");
            string now = DateTime.Now.ToString("HH:mm:ss");
            _pingCounters.TryAdd(empId, 0);

            if (!IsUsingFallbackMemory)
            {
                await _db.KeyDeleteAsync($"{BlockedPrefix}{empId}");
                await _db.SetAddAsync(StaffSetKey, empId);
                await _db.HashSetAsync($"{ProfilePrefix}{empId}", new HashEntry[]
                {
                    new("FullName", fullName),
                    new("Department", dept),
                    new("ClientIp", clientIp),
                    new("LastSeen", now),
                    new("CurrentTask", initialTask),
                    new("RttMs", "1.2")
                });
                await _db.StringSetAsync($"{OnlinePrefix}{empId}", "Online", TimeSpan.FromSeconds(HrmProtocol.OnlineTtlSeconds));
                await _db.SortedSetIncrementAsync(UptimeZSetKey, empId, 0);
                return;
            }

            _memBlockedUntil.TryRemove(empId, out _);
            _memSetIds[empId] = 1;
            var hash = _memHashes.GetOrAdd(empId, _ => new ConcurrentDictionary<string, string>());
            hash["FullName"] = fullName;
            hash["Department"] = dept;
            hash["ClientIp"] = clientIp;
            hash["LastSeen"] = now;
            if (!hash.ContainsKey("CurrentTask")) hash["CurrentTask"] = initialTask;
            if (!hash.ContainsKey("RttMs")) hash["RttMs"] = "1.2";

            _memOnlineTtl[empId] = ("Online", DateTime.UtcNow.AddSeconds(HrmProtocol.OnlineTtlSeconds));
            _memZSetUptime.TryAdd(empId, 0);
        }

        public async Task RefreshHeartbeatAsync(string empId, string clientIp = "127.0.0.1", double rttMs = 1.0)
        {
            empId = HrmProtocol.Sanitize(empId, "NV01").ToUpperInvariant();
            string now = DateTime.Now.ToString("HH:mm:ss");
            _pingCounters.AddOrUpdate(empId, 1, (_, count) => count + 1);
            double cleanRtt = Math.Round(Math.Clamp(rttMs, 0.2, 999.0), 1);

            if (!IsUsingFallbackMemory)
            {
                await _db.SetAddAsync(StaffSetKey, empId);
                string onlineKey = $"{OnlinePrefix}{empId}";
                if (await _db.KeyExistsAsync(onlineKey))
                    await _db.KeyExpireAsync(onlineKey, TimeSpan.FromSeconds(HrmProtocol.OnlineTtlSeconds));
                else
                    await _db.StringSetAsync(onlineKey, "Online", TimeSpan.FromSeconds(HrmProtocol.OnlineTtlSeconds));

                await _db.HashSetAsync($"{ProfilePrefix}{empId}", new HashEntry[]
                {
                    new("LastSeen", now),
                    new("RttMs", cleanRtt.ToString("0.0"))
                });
                // Cộng 5 giây vào bảng xếp hạng chuyên cần Redis ZSET
                await _db.SortedSetIncrementAsync(UptimeZSetKey, empId, HrmProtocol.HeartbeatIntervalMs / 1000.0);
                return;
            }

            _memSetIds[empId] = 1;
            string currentStatus = "Online";
            if (_memOnlineTtl.TryGetValue(empId, out var existing) && existing.ExpireAt > DateTime.UtcNow)
            {
                currentStatus = existing.Status;
            }
            _memOnlineTtl[empId] = (currentStatus, DateTime.UtcNow.AddSeconds(HrmProtocol.OnlineTtlSeconds));

            var hash = _memHashes.GetOrAdd(empId, _ => new ConcurrentDictionary<string, string>
            {
                ["FullName"] = empId,
                ["Department"] = "LAN Client",
                ["ClientIp"] = clientIp,
                ["CurrentTask"] = "Đang trực ca"
            });
            hash["LastSeen"] = now;
            hash["RttMs"] = cleanRtt.ToString("0.0");

            _memZSetUptime.AddOrUpdate(empId, 5, (_, old) => old + 5);
        }

        public async Task UpdateStatusAsync(string empId, string newStatus)
        {
            empId = HrmProtocol.Sanitize(empId, "NV01").ToUpperInvariant();
            newStatus = HrmProtocol.Sanitize(newStatus, "Online");
            string now = DateTime.Now.ToString("HH:mm:ss");

            if (!IsUsingFallbackMemory)
            {
                await _db.SetAddAsync(StaffSetKey, empId);
                await _db.StringSetAsync($"{OnlinePrefix}{empId}", newStatus, TimeSpan.FromSeconds(HrmProtocol.OnlineTtlSeconds));
                await _db.HashSetAsync($"{ProfilePrefix}{empId}", "LastSeen", now);
                return;
            }

            _memSetIds[empId] = 1;
            _memOnlineTtl[empId] = (newStatus, DateTime.UtcNow.AddSeconds(HrmProtocol.OnlineTtlSeconds));
            if (_memHashes.TryGetValue(empId, out var hash))
            {
                hash["LastSeen"] = now;
            }
        }

        public async Task UpdateTaskAsync(string empId, string taskDesc)
        {
            empId = HrmProtocol.Sanitize(empId, "NV01").ToUpperInvariant();
            taskDesc = HrmProtocol.Sanitize(taskDesc, "Đang trực ca");
            string now = DateTime.Now.ToString("HH:mm:ss");

            if (!IsUsingFallbackMemory)
            {
                await _db.HashSetAsync($"{ProfilePrefix}{empId}", new HashEntry[]
                {
                    new("CurrentTask", taskDesc),
                    new("LastSeen", now)
                });
                return;
            }

            var hash = _memHashes.GetOrAdd(empId, _ => new ConcurrentDictionary<string, string>());
            hash["CurrentTask"] = taskDesc;
            hash["LastSeen"] = now;
        }

        public async Task AppendChatMessageAsync(string sender, string target, string content)
        {
            var entry = new ChatMessageEntry
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Sender = HrmProtocol.Sanitize(sender, "SYSTEM"),
                Target = HrmProtocol.Sanitize(target, "ALL"),
                Content = HrmProtocol.Sanitize(content, "")
            };

            if (!IsUsingFallbackMemory)
            {
                string raw = $"{entry.Time}|{entry.Sender}|{entry.Target}|{entry.Content}";
                await _db.ListLeftPushAsync(ChatListKey, raw);
                await _db.ListTrimAsync(ChatListKey, 0, 49); // Giữ đúng 50 tin nhắn mới nhất trong Redis LIST
                return;
            }

            lock (_chatLock)
            {
                _memChatList.AddFirst(entry);
                while (_memChatList.Count > 50) _memChatList.RemoveLast();
            }
        }

        public async Task<List<ChatMessageEntry>> GetRecentChatHistoryAsync(int count = 25)
        {
            if (!IsUsingFallbackMemory)
            {
                RedisValue[] vals = await _db.ListRangeAsync(ChatListKey, 0, count - 1);
                var list = new List<ChatMessageEntry>();
                foreach (var v in vals)
                {
                    string[] p = v.ToString().Split('|');
                    if (p.Length >= 4)
                    {
                        list.Add(new ChatMessageEntry { Time = p[0], Sender = p[1], Target = p[2], Content = p[3] });
                    }
                }
                return list;
            }

            lock (_chatLock)
            {
                return _memChatList.Take(count).ToList();
            }
        }

        public async Task SetOfflineImmediatelyAsync(string empId)
        {
            empId = HrmProtocol.Sanitize(empId, "NV01").ToUpperInvariant();
            string now = DateTime.Now.ToString("HH:mm:ss");

            if (!IsUsingFallbackMemory)
            {
                await _db.KeyDeleteAsync($"{OnlinePrefix}{empId}");
                await _db.HashSetAsync($"{ProfilePrefix}{empId}", "LastSeen", now);
                return;
            }

            _memOnlineTtl.TryRemove(empId, out _);
            if (_memHashes.TryGetValue(empId, out var hash))
            {
                hash["LastSeen"] = now;
            }
        }

        public void RecordBroadcastForStaff(string empId, string message)
        {
            _lastBroadcasts[empId] = $"[{DateTime.Now:HH:mm:ss}] {message}";
        }

        public async Task<List<StaffMember>> GetAllStaffPresenceAsync(Func<string, bool> isSocketActiveFunc = null)
        {
            var result = new List<StaffMember>();

            if (!IsUsingFallbackMemory)
            {
                RedisValue[] allIds = await _db.SetMembersAsync(StaffSetKey);
                foreach (var idVal in allIds)
                {
                    string empId = idVal.ToString();
                    HashEntry[] profile = await _db.HashGetAllAsync($"{ProfilePrefix}{empId}");
                    var dict = profile.ToStringDictionary();
                    var onlineVal = await _db.StringGetWithExpiryAsync($"{OnlinePrefix}{empId}");
                    var blockTtl = await _db.KeyTimeToLiveAsync($"{BlockedPrefix}{empId}");
                    double? uptimeScore = await _db.SortedSetScoreAsync(UptimeZSetKey, empId);

                    bool isBlocked = blockTtl.HasValue && blockTtl.Value.TotalSeconds > 0;
                    bool isOnline = !isBlocked && onlineVal.Value.HasValue;
                    double.TryParse(dict.TryGetValue("RttMs", out string rttStr) ? rttStr : "1.0", out double rtt);

                    result.Add(new StaffMember
                    {
                        EmpId = empId,
                        FullName = dict.TryGetValue("FullName", out string name) ? name : empId,
                        Department = dict.TryGetValue("Department", out string dept) ? dept : "IT",
                        ClientIp = dict.TryGetValue("ClientIp", out string ip) ? ip : "127.0.0.1",
                        LastSeen = dict.TryGetValue("LastSeen", out string seen) ? seen : "N/A",
                        CurrentTask = dict.TryGetValue("CurrentTask", out string task) ? task : "Đang trực ca",
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

            DateTime nowUtc = DateTime.UtcNow;
            foreach (var empId in _memSetIds.Keys)
            {
                _memHashes.TryGetValue(empId, out var hash);
                bool isBlocked = false;
                double blockLeft = 0;
                if (_memBlockedUntil.TryGetValue(empId, out DateTime bUntil))
                {
                    if (bUntil > nowUtc)
                    {
                        isBlocked = true;
                        blockLeft = Math.Ceiling((bUntil - nowUtc).TotalSeconds);
                    }
                    else
                    {
                        _memBlockedUntil.TryRemove(empId, out _);
                    }
                }

                bool isOnline = false;
                string status = isBlocked ? "BLOCKED" : "Offline";
                double ttlRem = 0;

                if (!isBlocked && _memOnlineTtl.TryGetValue(empId, out var onlineInfo))
                {
                    double secondsLeft = (onlineInfo.ExpireAt - nowUtc).TotalSeconds;
                    if (secondsLeft > 0)
                    {
                        isOnline = true;
                        status = onlineInfo.Status;
                        ttlRem = Math.Ceiling(secondsLeft);
                    }
                    else
                    {
                        _memOnlineTtl.TryRemove(empId, out _);
                    }
                }

                double rtt = 1.2;
                if (hash != null && hash.TryGetValue("RttMs", out string rttS))
                    double.TryParse(rttS, out rtt);

                result.Add(new StaffMember
                {
                    EmpId = empId,
                    FullName = hash != null && hash.TryGetValue("FullName", out string n) ? n : empId,
                    Department = hash != null && hash.TryGetValue("Department", out string d) ? d : "IT",
                    ClientIp = hash != null && hash.TryGetValue("ClientIp", out string ip) ? ip : "127.0.0.1",
                    LastSeen = hash != null && hash.TryGetValue("LastSeen", out string s) ? s : "N/A",
                    CurrentTask = hash != null && hash.TryGetValue("CurrentTask", out string t) ? t : "Đang trực ca",
                    RttMs = rtt,
                    UptimeSeconds = _memZSetUptime.TryGetValue(empId, out double up) ? up : 0,
                    IsOnline = isOnline,
                    IsRateLimited = isBlocked,
                    BlockedRemainingSeconds = blockLeft,
                    CurrentStatus = status,
                    RemainingTtlSeconds = ttlRem,
                    PingCount = _pingCounters.TryGetValue(empId, out int p) ? p : 0,
                    LastBroadcast = _lastBroadcasts.TryGetValue(empId, out string b) ? b : null,
                    IsSocketConnected = isSocketActiveFunc != null && isSocketActiveFunc(empId)
                });
            }
            return result.OrderByDescending(x => x.UptimeSeconds).ThenBy(x => x.EmpId).ToList();
        }
    }
}