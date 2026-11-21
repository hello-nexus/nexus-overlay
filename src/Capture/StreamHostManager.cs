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
    // Hosts torn down for a dead capture: consecutive rebuild count and the earliest tick the next spawn may run.
    private readonly Dictionary<string, int> _rebuilds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _spawnNotBefore = new(StringComparer.Ordinal);

    public StreamHostManager(string serviceOrigin)
    {
        _serviceOrigin = serviceOrigin;
    }

    public int Count => _hosts.Count;

    public void Reconcile(IReadOnlyList<StreamAssignment> assignments, string pairedToken)
    {
        var desired = new Dictionary<string, StreamAssignment>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            if (!string.IsNullOrEmpty(assignment.SessionId) && !string.IsNullOrEmpty(assignment.PanelDeviceId))
                desired[assignment.SessionId] = assignment;
        }

        foreach (var id in _spawnNotBefore.Keys.Where(id => !desired.ContainsKey(id)).ToList())
        {
            _spawnNotBefore.Remove(id);
            _rebuilds.Remove(id);
        }

        var (spawn, close) = StreamHostPlan.Compute(desired.Keys.ToList(), _hosts.Keys.ToList());

        foreach (var sessionId in close)
        {
            CloseHost(sessionId, "unassigned");
        }

        foreach (var sessionId in spawn)
        {
            if (_spawnNotBefore.TryGetValue(sessionId, out var notBefore) && Environment.TickCount64 < notBefore) continue;
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
        _spawnNotBefore.Clear();
    }

    private void SpawnHost(StreamAssignment assignment, string pairedToken)
    {
        try
        {
            var host = new StreamPanelHost(assignment, _serviceOrigin, pairedToken);
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
                var count = _rebuilds.GetValueOrDefault(host.SessionId) + 1;
                _rebuilds[host.SessionId] = count;
                var delay = StreamRebuildBackoff.DelayMs(count);
                _spawnNotBefore[host.SessionId] = Environment.TickCount64 + delay;
                Log.Warn($"stream-hosts rebuild session={host.SessionId} (x{count}), respawn in {delay / 1000}s");
            };
            host.Recovered = () =>
            {
                if (_rebuilds.Remove(host.SessionId)) Log.Info($"stream-hosts recovered session={host.SessionId}");
                _spawnNotBefore.Remove(host.SessionId);
            };
            _hosts[assignment.SessionId] = host;
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
        _rebuilds.Remove(sessionId);
        _spawnNotBefore.Remove(sessionId);
        if (!_hosts.Remove(sessionId, out var host)) return;
        try { host.Dispose(); }
        catch (Exception ex) { Log.Error($"stream-hosts dispose {sessionId}: {ex.Message}"); }
        Log.Info($"stream-hosts closed session={sessionId} ({reason})");
    }
}
