using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nexus.Overlay;

/// <summary>
/// Publishes the monitor map with one post in flight at a time. Triggers that
/// land mid-flight coalesce into one more pass that rebuilds the map, so the
/// service always ends on the latest one and an older post can never finish
/// after a newer one. Single-threaded by contract (the message-loop thread).
/// </summary>
internal sealed class MonitorMapPublisher
{
    private readonly Func<List<OverlayMonitorEntry>> _build;
    private readonly Func<List<OverlayMonitorEntry>, Task<bool>> _post;
    private bool _inFlight;
    private bool _dirty;
    private string? _acknowledged;

    public MonitorMapPublisher(Func<List<OverlayMonitorEntry>> build, Func<List<OverlayMonitorEntry>, Task<bool>> post)
    {
        _build = build;
        _post = post;
    }

    /// <summary>Forget what the service holds, e.g. after it may have restarted.</summary>
    public void Invalidate() => _acknowledged = null;

    public async Task RequestAsync()
    {
        _dirty = true;
        if (_inFlight) return;
        _inFlight = true;
        try
        {
            while (_dirty)
            {
                _dirty = false;
                var entries = _build();
                var signature = MonitorMap.Signature(entries);
                if (signature == _acknowledged) continue;
                if (!await _post(entries)) break;
                _acknowledged = signature;
            }
        }
        finally
        {
            _inFlight = false;
        }
    }
}
