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
        private ConnectionMultiplexer _redis;
        private IDatabase _db;
        public bool IsUsingFallbackMemory { get; private set; } = false;

        private const string StaffSetKey = "staff:ids";
        private const string ProfilePrefix = "staff:profile:";
        private const string OnlinePrefix = "staff:online:";

        // Bá»™ nhá»› giáº£ láº­p Redis (Set + Hash + String TTL) khi mÃ¡y chÆ°a báº­t dá»‹ch vá»¥ Redis cá»•ng 6379
        private readonly ConcurrentDictionary<string, byte> _memSetIds = new();
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _memHashes = new();
        private readonly ConcurrentDictionary<string, (string Status, DateTime ExpireAt)> _memOnlineTtl = new();
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

        public async Task RegisterLoginAsync(string empId, string fullName, string dept, string clientIp)
        {
            string now = DateTime.Now.ToString("HH:mm:ss");
            _pingCounters.TryAdd(empId, 0);

            if (!IsUsingFallbackMemory)
            {
                await _db.SetAddAsync(StaffSetKey, empId);
                await _db.HashSetAsync($"{ProfilePrefix}{empId}", new HashEntry[]
                {
                    new("FullName", fullName),
                    new("Department", dept),
                    new("ClientIp", clientIp),
                    new("LastSeen", now)
                });
                await _db.StringSetAsync($"{OnlinePrefix}{empId}", "Online", TimeSpan.FromSeconds(HrmProtocol.OnlineTtlSeconds));
                return;
            }

            _memSetIds[empId] = 1;
            var hash = _memHashes.GetOrAdd(empId, _ => new ConcurrentDictionary<string, string>());
            hash["FullName"] = fullName;
            hash["Department"] = dept;
            hash["ClientIp"] = clientIp;
            hash["LastSeen"] = now;
            _memOnlineTtl[empId] = ("Online", DateTime.UtcNow.AddSeconds(HrmProtocol.OnlineTtlSeconds));
        }

        public async Task RefreshHeartbeatAsync(string empId)
        {
            string now = DateTime.Now.ToString("HH:mm:ss");
            _pingCounters.AddOrUpdate(empId, 1, (_, count) => count + 1);

            if (!IsUsingFallbackMemory)
            {
                string onlineKey = $"{OnlinePrefix}{empId}";
                if (await _db.KeyExistsAsync(onlineKey))
                    await _db.KeyExpireAsync(onlineKey, TimeSpan.FromSeconds(HrmProtocol.OnlineTtlSeconds));
                else
                    await _db.StringSetAsync(onlineKey, "Online", TimeSpan.FromSeconds(HrmProtocol.OnlineTtlSeconds));

                await _db.HashSetAsync($"{ProfilePrefix}{empId}", "LastSeen", now);
                return;
            }

            string currentStatus = "Online";
            if (_memOnlineTtl.TryGetValue(empId, out var existing) && existing.ExpireAt > DateTime.UtcNow)
            {
                currentStatus = existing.Status;
            }
            _memOnlineTtl[empId] = (currentStatus, DateTime.UtcNow.AddSeconds(HrmProtocol.OnlineTtlSeconds));

            if (_memHashes.TryGetValue(empId, out var hash))
            {
                hash["LastSeen"] = now;
            }
        }

        public async Task UpdateStatusAsync(string empId, string newStatus)
        {
            string now = DateTime.Now.ToString("HH:mm:ss");

            if (!IsUsingFallbackMemory)
            {
                await _db.StringSetAsync($"{OnlinePrefix}{empId}", newStatus, TimeSpan.FromSeconds(HrmProtocol.OnlineTtlSeconds));
                await _db.HashSetAsync($"{ProfilePrefix}{empId}", "LastSeen", now);
                return;
            }

            _memOnlineTtl[empId] = (newStatus, DateTime.UtcNow.AddSeconds(HrmProtocol.OnlineTtlSeconds));
            if (_memHashes.TryGetValue(empId, out var hash))
            {
                hash["LastSeen"] = now;
            }
        }

        public async Task SetOfflineImmediatelyAsync(string empId)
        {
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
                    bool isOnline = onlineVal.Value.HasValue;

                    result.Add(new StaffMember
                    {
                        EmpId = empId,
                        FullName = dict.TryGetValue("FullName", out string name) ? name : "N/A",
                        Department = dict.TryGetValue("Department", out string dept) ? dept : "N/A",
                        ClientIp = dict.TryGetValue("ClientIp", out string ip) ? ip : "127.0.0.1",
                        LastSeen = dict.TryGetValue("LastSeen", out string seen) ? seen : "N/A",
                        IsOnline = isOnline,
                        CurrentStatus = isOnline ? onlineVal.Value.ToString() : "Offline",
                        RemainingTtlSeconds = onlineVal.Expiry.HasValue ? Math.Max(0, Math.Round(onlineVal.Expiry.Value.TotalSeconds, 0)) : 0,
                        PingCount = _pingCounters.TryGetValue(empId, out int p) ? p : 0,
                        LastBroadcast = _lastBroadcasts.TryGetValue(empId, out string b) ? b : null,
                        IsSocketConnected = isSocketActiveFunc != null && isSocketActiveFunc(empId)
                    });
                }
                return result.OrderBy(x => x.EmpId).ToList();
            }

            foreach (var empId in _memSetIds.Keys)
            {
                _memHashes.TryGetValue(empId, out var hash);
                bool isOnline = false;
                string status = "Offline";
                double ttlRem = 0;

                if (_memOnlineTtl.TryGetValue(empId, out var onlineInfo))
                {
                    double secondsLeft = (onlineInfo.ExpireAt - DateTime.UtcNow).TotalSeconds;
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

                result.Add(new StaffMember
                {
                    EmpId = empId,
                    FullName = hash != null && hash.TryGetValue("FullName", out string n) ? n : "N/A",
                    Department = hash != null && hash.TryGetValue("Department", out string d) ? d : "N/A",
                    ClientIp = hash != null && hash.TryGetValue("ClientIp", out string ip) ? ip : "127.0.0.1",
                    LastSeen = hash != null && hash.TryGetValue("LastSeen", out string s) ? s : "N/A",
                    IsOnline = isOnline,
                    CurrentStatus = status,
                    RemainingTtlSeconds = ttlRem,
                    PingCount = _pingCounters.TryGetValue(empId, out int p) ? p : 0,
                    LastBroadcast = _lastBroadcasts.TryGetValue(empId, out string b) ? b : null,
                    IsSocketConnected = isSocketActiveFunc != null && isSocketActiveFunc(empId)
                });
            }
            return result.OrderBy(x => x.EmpId).ToList();
        }
    }
}
