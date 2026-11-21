using System.Collections.Generic;

namespace Nexus.Overlay.Capture;

/// <summary>Per-session rebuild backoff state kept by the manager across host lifetimes: how many rebuilds in a row, and the earliest tick the next spawn may run.</summary>
internal sealed class StreamRebuildTracker
{
    private readonly Dictionary<string, int> _rebuilds = new(System.StringComparer.Ordinal);
    private readonly Dictionary<string, long> _notBefore = new(System.StringComparer.Ordinal);

    /// <summary>Sessions waiting out a backoff before their host is built again; the process must stay alive for them.</summary>
    public int PendingCount => _notBefore.Count;

    /// <summary>Rebuilds in a row for the session without frames flowing in between.</summary>
    public int RebuildCount(string sessionId) => _rebuilds.GetValueOrDefault(sessionId);

    /// <summary>Records a rebuild and returns the delay before the respawn.</summary>
    public long OnRebuilt(string sessionId, long nowMs)
    {
        var count = _rebuilds.GetValueOrDefault(sessionId) + 1;
        _rebuilds[sessionId] = count;
        var delay = StreamRebuildBackoff.DelayMs(count);
        _notBefore[sessionId] = nowMs + delay;
        return delay;
    }

    /// <summary>Records a browser process exit and returns the delay before the respawn: none for the first, then the rebuild backoff for repeats without a recovery in between.</summary>
    public long OnBrowserExited(string sessionId, long nowMs)
    {
        var count = _rebuilds.GetValueOrDefault(sessionId) + 1;
        _rebuilds[sessionId] = count;
        var delay = StreamRebuildBackoff.DelayMs(count - 1);
        if (delay > 0) _notBefore[sessionId] = nowMs + delay;
        return delay;
    }

    public bool IsBlocked(string sessionId, long nowMs) => _notBefore.TryGetValue(sessionId, out var t) && nowMs < t;

    /// <summary>The respawn ran; the rebuild count stays until frames flow again.</summary>
    public void OnSpawned(string sessionId) => _notBefore.Remove(sessionId);

    /// <summary>Frames flowed past the initial one: forget the session's history. True when it had any.</summary>
    public bool OnRecovered(string sessionId)
    {
        _notBefore.Remove(sessionId);
        return _rebuilds.Remove(sessionId);
    }

    /// <summary>The session unlocked: composition resumes, so the backoff earned while it was suspended no longer applies.</summary>
    public void OnUnlocked()
    {
        _rebuilds.Clear();
        _notBefore.Clear();
    }

    public void Forget(string sessionId)
    {
        _rebuilds.Remove(sessionId);
        _notBefore.Remove(sessionId);
    }

    /// <summary>Drops state for sessions the service no longer wants.</summary>
    public void Prune(IReadOnlyCollection<string> desiredSessionIds)
    {
        var desired = new HashSet<string>(desiredSessionIds, System.StringComparer.Ordinal);
        foreach (var id in new List<string>(_notBefore.Keys))
            if (!desired.Contains(id)) Forget(id);
        foreach (var id in new List<string>(_rebuilds.Keys))
            if (!desired.Contains(id)) Forget(id);
    }

    public void Clear()
    {
        _rebuilds.Clear();
        _notBefore.Clear();
    }
}
