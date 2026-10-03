namespace FixRedis.Core;

/// <summary>通过全局互斥锁阻止多个修复进程同时操作同一 Redis 服务。</summary>
public sealed class SingleInstanceLease : IDisposable
{
    private const string MutexName = @"Global\FixRedis.RedisRepair.v1";
    private readonly Mutex _mutex;
    private SingleInstanceLease(Mutex mutex) => _mutex = mutex;
    public static SingleInstanceLease? TryAcquire()
    {
        var mutex = new Mutex(true, MutexName, out var created);
        if (created) return new SingleInstanceLease(mutex);
        mutex.Dispose(); return null;
    }
    // 互斥锁有线程归属，必须在同一 STA 线程上获取和释放。
    public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
}
