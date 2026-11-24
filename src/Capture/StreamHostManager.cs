using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Overlay.Capture;

/// <summary>
/// Hosts one <see cref="StreamPanelHost"/> per desired stream session,
/// reconciled against GET /panel/streams/assignments on every prefs
/// poll/push. SessionIds are boot-scoped and re-minted server-side on any
/// config change, so the diff (<see cref="StreamHostPlan"/>) is a pure
/// spawn/close set difference. All calls run on the message-loop thread.
/// </summary>
internal sealed class StreamHostManager
{
    private readonly string _serviceOrigin;
    private readonly Dictionary<string, StreamPanelHost> _hosts = new(StringComparer.Ordinal);
    private readonly StreamRebuildTracker _rebuilds = new();
    private readonly StreamRenderMode _renderMode = new();

    public StreamHostManager(string serviceOrigin)
    {
        _serviceOrigin = serviceOrigin;
    }

    public int Count => _hosts.Count;

    /// <summary>Sessions waiting out a rebuild backoff; they have no host yet but the overlay must not idle-exit and lose them.</summary>
    public bool HasPendingRebuilds => _rebuilds.PendingCount > 0;

    /// <summary>The user session unlocked: drop the backoff and let the next reconcile respawn pending rebuilds at once.</summary>
public void OnSessionUnlocked() => _rebuilds.OnUnlocked();

    public void Reconcile(IReadOnlyList<StreamAssignment> assignments, string pairedToken)
    {
        var desired = new Dictionary<string, StreamAssignment>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            if (!string.IsNullOrEmpty(assignment.SessionId) && !string.IsNullOrEmpty(assignment.PanelDeviceId))
                desired[assignment.SessionId] = assignment;
        }

        _rebuilds.Prune(desired.Keys);

        var (spawn, close) = StreamHostPlan.Compute(desired.Keys.ToList(), _hosts.Keys.ToList());

        foreach (var sessionId in close)
        {
            CloseHost(sessionId, "unassigned");
        }

        foreach (var sessionId in spawn)
        {
            if (_rebuilds.IsBlocked(sessionId, Environment.TickCount64)) continue;
            SpawnHost(desired[sessionId], pairedToken);
        }
    }

    public void CloseAll()
    {
        foreach (var host in _hosts.Values.ToList())
        {
            try { host.Dispose(); } catch { }
        }
        _hosts.Clear();
        _rebuilds.Clear();
        _renderMode.Reset();
    }

    private void SpawnHost(StreamAssignment assignment, string pairedToken)
    {
        try
        {
            var host = new StreamPanelHost(assignment, _serviceOrigin, pairedToken, _renderMode.Software, _renderMode.StaleProfileDeletable);
            // A faulted host is dropped immediately; the session stays desired
            // server-side, so the next reconcile respawns a fresh one (whose
            // new ingest connection supersedes any half-open predecessor).
            host.Faulted = () =>
            {
                if (_hosts.Remove(host.SessionId, out _))
                    Log.Warn($"stream-hosts faulted session={host.SessionId}");
            };
            host.RebuildRequested = () =>
            {
                if (!_hosts.Remove(host.SessionId, out _)) return;
                var delay = _rebuilds.OnRebuilt(host.SessionId, Environment.TickCount64);
                if (_renderMode.OnRebuilt(_rebuilds.RebuildCount(host.SessionId)))
                    Log.Warn($"stream-hosts session={host.SessionId}: rebuilt host still produced no frames, switching stream browser to software compositing (--disable-gpu) until all stream hosts are closed");
                Log.Warn($"stream-hosts rebuild session={host.SessionId}, respawn in {delay / 1000}s");
            };
            host.BrowserExited = () =>
            {
                if (!_hosts.Remove(host.SessionId, out _)) return;
                var delay = _rebuilds.OnBrowserExited(host.SessionId, Environment.TickCount64);
                Log.Warn($"stream-hosts browser exited session={host.SessionId}, respawn in {delay / 1000}s");
            };
            host.Recovered = () =>
            {
                if (_rebuilds.OnRecovered(host.SessionId)) Log.Info($"stream-hosts recovered session={host.SessionId}");
            };
            _hosts[assignment.SessionId] = host;
            _rebuilds.OnSpawned(assignment.SessionId);
            Log.Info($"stream-hosts opened session={assignment.SessionId} device={assignment.PanelDeviceId} "
                + $"{assignment.CssWidth}x{assignment.CssHeight}@{assignment.Fps} dpr={assignment.Dpr}");
        }
        catch (Exception ex)
        {
            Log.Error($"stream-hosts spawn {assignment.SessionId}: {ex.Message}");
        }
    }

    private void CloseHost(string sessionId, string reason)
    {
        _rebuilds.Forget(sessionId);
        if (!_hosts.Remove(sessionId, out var host)) return;
        try { host.Dispose(); }
        catch (Exception ex) { Log.Error($"stream-hosts dispose {sessionId}: {ex.Message}"); }
        Log.Info($"stream-hosts closed session={sessionId} ({reason})");
    }
}
