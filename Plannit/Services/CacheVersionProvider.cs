using System.Collections.Concurrent;

namespace Plannit.Services;

/// <summary>
/// Per-user generation counter used as part of cache keys for net-worth/recurring-detection
/// caches: bumping it makes every previously cached key for that user unreachable without
/// having to enumerate and remove entries. Backed by a singleton in-memory dictionary — fine
/// for a single-process deployment.
/// </summary>
public interface ICacheVersionProvider
{
    int GetVersion(string userId);
    void Bump(string userId);
}

public class CacheVersionProvider : ICacheVersionProvider
{
    private readonly ConcurrentDictionary<string, int> _versions = new();

    public int GetVersion(string userId) => _versions.GetOrAdd(userId, 0);

    public void Bump(string userId) => _versions.AddOrUpdate(userId, 1, (_, current) => current + 1);
}
