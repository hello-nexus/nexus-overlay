using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Nexus.Overlay.Media;

/// <summary>
/// One long-lived chunked POST to
/// <c>/panel/streams/{sessionId}/ingest</c> carrying framed Annex-B access
/// units (see <see cref="FrameFraming"/>). The request body never ends while
/// the session is healthy; a response arriving at all means the service ended
/// or rejected the session, which is a fault for the host to react to.
/// </summary>
internal sealed class IngestClient : IDisposable
{
    // The encoder produces ~fps/s and loopback drains instantly, so a full
    // channel means the service stopped reading; fault instead of buffering.
    private const int ChannelCapacity = 600;
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(5);

    private readonly Channel<(byte[] Payload, byte Flags, Action<byte[]>? Sent)> _channel;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _sessionId;
    private int _faulted;
    private long _lastSendTicks;

    /// <summary>Fires once, from a worker thread, when the connection ends for
    /// any reason. The host tears down and lets reconcile respawn.</summary>
    public Action? Faulted;

    public IngestClient(string serviceOrigin, string sessionId, string token)
    {
        _sessionId = sessionId;
        // Wait mode makes TryWrite return false when full instead of silently
        // dropping, which is the fault signal Send relies on.
        _channel = Channel.CreateBounded<(byte[], byte, Action<byte[]>?)>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{serviceOrigin}/panel/streams/{Uri.EscapeDataString(sessionId)}/ingest")
        {
            Content = new FrameStreamContent(this),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        _lastSendTicks = Environment.TickCount64;
        _ = RunAsync(request);
        _ = KeepaliveAsync();
    }

    /// <summary>Queues one payload; <paramref name="sent"/> gets the array back once its bytes are written.</summary>
    public void Send(byte[] annexBAccessUnit, bool idr, Action<byte[]>? sent = null)
    {
        Volatile.Write(ref _lastSendTicks, Environment.TickCount64);
        if (!_channel.Writer.TryWrite((annexBAccessUnit, idr ? FrameFraming.FlagIdr : (byte)0, sent)))
        {
            // Channel full: the send loop is stuck (service hung mid-stream).
            Log.Warn($"ingest {_sessionId}: send queue full, faulting");
            Fault();
        }
    }

    public void Dispose()
    {
        // Suppress Faulted on deliberate teardown.
        Interlocked.Exchange(ref _faulted, 1);
        _cts.Cancel();
        _channel.Writer.TryComplete();
        _http.Dispose();
    }

    private async Task RunAsync(HttpRequestMessage request)
    {
        try
        {
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
            // Headers only arrive when the service ends the request: 404 for a
            // stale session, 200 on service-side teardown.
            Log.Info($"ingest {_sessionId}: connection ended {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"ingest {_sessionId}: {ex.GetType().Name}: {ex.Message}");
        }
        Fault();
    }

    private async Task KeepaliveAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(KeepaliveInterval, _cts.Token);
                if (Environment.TickCount64 - Volatile.Read(ref _lastSendTicks)
                    >= (long)KeepaliveInterval.TotalMilliseconds)
                {
                    _channel.Writer.TryWrite((Array.Empty<byte>(), FrameFraming.FlagControl, null));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Fault()
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0) return;
        try { Faulted?.Invoke(); } catch { }
    }

    private sealed class FrameStreamContent : HttpContent
    {
        private readonly IngestClient _owner;

        public FrameStreamContent(IngestClient owner)
        {
            _owner = owner;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var header = new byte[FrameFraming.HeaderSize];
            var reader = _owner._channel.Reader;
            while (await reader.WaitToReadAsync(_owner._cts.Token))
            {
                while (reader.TryRead(out var frame))
                {
                    FrameFraming.WriteHeader(header, frame.Payload.Length, frame.Flags);
                    await stream.WriteAsync(header, _owner._cts.Token);
                    if (frame.Payload.Length > 0)
                        await stream.WriteAsync(frame.Payload, _owner._cts.Token);
                    frame.Sent?.Invoke(frame.Payload);
                }
                // One flush per drained batch keeps latency at one frame while
                // letting catch-up batches coalesce.
                await stream.FlushAsync(_owner._cts.Token);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
