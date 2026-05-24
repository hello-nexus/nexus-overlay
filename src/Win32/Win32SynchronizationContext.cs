using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Nexus.Overlay.Win32;

/// <summary>
/// Marshals async continuations back onto the UI thread that owns the
/// message loop. WebView2 callbacks fire on the COM RPC thread; without
/// this, anything awaited off a WebView2 event would resume on a thread
/// pool worker that can't touch the controller. Posts a registered window
/// message to wake the loop, which drains the queue inline.
/// </summary>
internal sealed class Win32SynchronizationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private IntPtr _marshalerHwnd;

    public uint DrainMessage { get; } = Native.RegisterWindowMessageW("Nexus.Overlay.SyncContextDrain");

    public void Bind(IntPtr marshalerHwnd) => _marshalerHwnd = marshalerHwnd;

    public override void Post(SendOrPostCallback d, object? state)
    {
        _queue.Enqueue((d, state));
        var hwnd = _marshalerHwnd;
        if (hwnd != IntPtr.Zero)
        {
            Native.PostMessageW(hwnd, DrainMessage, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    /// <summary>
    /// Pull all queued continuations and invoke them on the calling
    /// thread. Called from the message loop when the drain message arrives.
    /// </summary>
    public void Drain()
    {
        while (_queue.TryDequeue(out var item))
        {
            try { item.Callback(item.State); }
            catch (Exception ex) { Log.Error($"sync-context drain item threw: {ex.Message}"); }
        }
    }
}
