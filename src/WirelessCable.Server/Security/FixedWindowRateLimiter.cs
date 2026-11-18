using System.Collections.Concurrent;

namespace WirelessCable.Server.Security;

/// <summary>
/// 固定窗口限流（每 key 每分钟 N 次）。阶段 1 只按客户端 IP 计，
/// 阶段 2 起改为"已配对设备 ID + IP"双维度。
/// </summary>
public sealed class FixedWindowRateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private int _callCount;

    public FixedWindowRateLimiter(int limitPerMinute, TimeSpan? window = null)
    {
        _limit = Math.Max(1, limitPerMinute);
        _window = window ?? TimeSpan.FromMinutes(1);
    }

    public bool TryAcquire(string key, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        var now = DateTimeOffset.UtcNow;
        var counter = _counters.GetOrAdd(key, static _ => new Counter());

        lock (counter)
        {
            if (counter.WindowStart == default || now - counter.WindowStart >= _window)
            {
                counter.WindowStart = now;
                counter.Count = 0;
            }

            if (counter.Count >= _limit)
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((_window - (now - counter.WindowStart)).TotalSeconds));
                return false;
            }

            counter.Count++;
        }

        if (Interlocked.Increment(ref _callCount) % 2048 == 0)
        {
            Purge(now);
        }

        return true;
    }

    private void Purge(DateTimeOffset now)
    {
        foreach (var pair in _counters)
        {
            if (now - pair.Value.WindowStart > _window * 2)
            {
                _counters.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class Counter
    {
        public DateTimeOffset WindowStart;
        public int Count;
    }
}
