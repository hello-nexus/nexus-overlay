using System;
using System.Diagnostics;

namespace Nexus.Overlay.Media;

/// <summary>
/// One session's shared-section path: opens the section the service creates when the ingest
/// binds (retrying briefly, since the bind is asynchronous), writes frames into it, and watches
/// the reader's progress. Used from the capture pump thread only. A frame it cannot take goes
/// over HTTP instead.
/// </summary>
internal sealed unsafe class SharedFrameLink : IDisposable
{
    private const long OpenWindowMs = 5000;
    private const long OpenRetryMs = 100;

    private readonly string _name;
    private readonly int _frameBytes;
    private readonly Action _onStale;
    private readonly string _logTag;
    private readonly SharedFrameLiveness _liveness = new();
    private SharedFrameWriter? _writer;
    private long _startMs;
    private long _nextTryMs;
    private bool _gaveUp;
    private bool _fallbackLogged;
    private bool _staleRaised;

    public SharedFrameLink(string name, int frameBytes, Action onStale, string logTag)
    {
        _name = name;
        _frameBytes = frameBytes;
        _onStale = onStale;
        _logTag = logTag;
    }

    /// <summary>False when the frame must take the HTTP path.</summary>
    public bool TryWrite(byte* source, uint pitch, int width, int height)
    {
        var now = Environment.TickCount64;
        if (_writer is null)
        {
            if (_gaveUp)
            {
                return false;
            }
            if (_startMs == 0)
            {
                _startMs = now;
            }
            if (now < _nextTryMs)
            {
                return false;
            }
            _nextTryMs = now + OpenRetryMs;
            _writer = SharedFrameWriter.TryOpen(_name, _frameBytes);
            if (_writer is null)
            {
                if (now - _startMs > OpenWindowMs)
                {
                    _gaveUp = true;
                    Log.Warn($"{_logTag}: shared section never opened; using HTTP ingest");
                }
                return false;
            }
            Log.Info($"{_logTag}: shared section open after {now - _startMs} ms");
        }
        if (!_writer.TryWrite(source, pitch, width, height))
        {
            if (!_fallbackLogged)
            {
                _fallbackLogged = true;
                Log.Warn($"{_logTag}: {width}x{height} frame does not fit the shared section; using HTTP for it");
            }
            return false;
        }
        if (!_staleRaised && _liveness.IsStale(now, _writer.WrittenSequence, _writer.ConsumedSequence))
        {
            _staleRaised = true;
            Log.Warn($"{_logTag}: service stopped consuming shared frames; faulting");
            _onStale();
        }
        return true;
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
    }
}
