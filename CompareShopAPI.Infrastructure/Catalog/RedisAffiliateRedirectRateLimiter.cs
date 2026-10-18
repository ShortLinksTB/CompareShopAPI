using System.Security.Cryptography;
using System.Text;
using CompareShopAPI.Application.Catalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CompareShopAPI.Infrastructure.Catalog;

public sealed class RedisAffiliateRedirectRateLimiter : IAffiliateRedirectRateLimiter, IDisposable
{
    private sealed class LocalWindow
    {
        public readonly Queue<long> Timestamps = new();
        public long LastSeenMilliseconds;
    }

    private const int LocalWindowCacheSize = 10_000;
    private const string Script = """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], 0, ARGV[1] - ARGV[2])
        local count = redis.call('ZCARD', KEYS[1])
        if count >= tonumber(ARGV[3]) then
            local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')[2]
            local retry = math.max(1, math.ceil((tonumber(oldest) + tonumber(ARGV[2]) - tonumber(ARGV[1])) / 1000))
            redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[2]) + tonumber(ARGV[4]))
            return { 0, retry }
        end
        redis.call('ZADD', KEYS[1], ARGV[1], ARGV[5])
        redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[2]) + tonumber(ARGV[4]))
        return { 1, 0 }
        """;
    private readonly AffiliateRedirectRateLimitOptions options;
    private readonly ILogger<RedisAffiliateRedirectRateLimiter> logger;
    private readonly string redisKeyPrefix;
    private readonly Lazy<ConnectionMultiplexer?> connection;
    private readonly object localWindowsGate = new();
    private readonly Dictionary<string, LocalWindow> localWindows = new(StringComparer.Ordinal);

    public RedisAffiliateRedirectRateLimiter(string? connectionString, string? redisKeyPrefix, IOptions<AffiliateRedirectRateLimitOptions> options, ILogger<RedisAffiliateRedirectRateLimiter> logger)
    {
        this.options = options.Value;
        this.logger = logger;
        this.redisKeyPrefix = string.IsNullOrWhiteSpace(redisKeyPrefix) ? "compareshop:" : redisKeyPrefix;
        connection = new Lazy<ConnectionMultiplexer?>(() => Connect(connectionString));
    }

    public async Task<AffiliateRedirectRateLimitDecision> TryAcquireAsync(string canonicalClientIp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(canonicalClientIp) || options.PermitLimit <= 0 || options.WindowSeconds <= 0) return AffiliateRedirectRateLimitDecision.Open();
        try
        {
            var db = connection.Value?.GetDatabase();
            if (db is null) return FailOpen("Redis connection is not configured", canonicalClientIp);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalClientIp)));
            var key = $"{redisKeyPrefix}ratelimit:v1:affiliate:redirect:ip:{digest}";
            var result = (RedisResult[]?)await db.ScriptEvaluateAsync(Script, [key], [
                now, TimeSpan.FromSeconds(options.WindowSeconds).TotalMilliseconds, options.PermitLimit,
                TimeSpan.FromSeconds(Math.Max(1, options.GraceSeconds)).TotalMilliseconds, $"{now}:{Guid.NewGuid():N}"
            ]).WaitAsync(ct);
            if (result is null || result.Length < 2) return FailOpen("Redis rate-limit response was invalid", canonicalClientIp);
            return (long)result[0] == 1 ? AffiliateRedirectRateLimitDecision.Allow() : AffiliateRedirectRateLimitDecision.Reject((int)(long)result[1]);
        }
        catch (Exception exception) when (exception is RedisException or InvalidOperationException or TimeoutException)
        {
            return FailOpen("Redis rate-limit operation failed", canonicalClientIp, exception);
        }
    }

    public void Dispose() { if (connection.IsValueCreated) connection.Value?.Dispose(); }

    private AffiliateRedirectRateLimitDecision FailOpen(string reason, string canonicalClientIp, Exception? exception = null)
    {
        logger.LogWarning(exception, "Affiliate redirect Redis rate limiting unavailable; using per-process fallback. Reason: {Reason}", reason);
        return TryAcquireLocally(canonicalClientIp);
    }

    private AffiliateRedirectRateLimitDecision TryAcquireLocally(string canonicalClientIp)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowMilliseconds = (long)TimeSpan.FromSeconds(options.WindowSeconds).TotalMilliseconds;
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalClientIp)));
        lock (localWindowsGate)
        {
            if (!localWindows.TryGetValue(key, out var state))
            {
                if (localWindows.Count >= LocalWindowCacheSize)
                {
                    var oldestKey = localWindows.MinBy(pair => pair.Value.LastSeenMilliseconds).Key;
                    localWindows.Remove(oldestKey);
                }
                state = new LocalWindow();
                localWindows.Add(key, state);
            }

            while (state.Timestamps.TryPeek(out var oldest) && oldest <= now - windowMilliseconds)
                state.Timestamps.Dequeue();

            state.LastSeenMilliseconds = now;
            if (state.Timestamps.Count >= options.PermitLimit)
            {
                var retryAfter = Math.Max(1, (int)Math.Ceiling((state.Timestamps.Peek() + windowMilliseconds - now) / 1000d));
                return AffiliateRedirectRateLimitDecision.Reject(retryAfter);
            }

            state.Timestamps.Enqueue(now);
            return AffiliateRedirectRateLimitDecision.Allow();
        }
    }

    private static ConnectionMultiplexer? Connect(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        try
        {
            var configuration = ConfigurationOptions.Parse(connectionString);
            configuration.AbortOnConnectFail = false;
            configuration.ConnectRetry = 0;
            configuration.ConnectTimeout = 1000;
            configuration.SyncTimeout = 1000;
            return ConnectionMultiplexer.Connect(configuration);
        }
        catch (Exception exception) when (exception is RedisException or InvalidOperationException or ArgumentException or FormatException) { return null; }
    }
}
