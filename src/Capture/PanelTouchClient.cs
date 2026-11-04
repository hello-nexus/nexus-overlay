using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Overlay.Capture;

/// <summary>
/// The long-lived GET /panel/streams/{sessionId}/touch, reopened while the session lives.
/// Callbacks run on a worker thread; <c>onDrop</c> fires whenever a connection ends, so the
/// host can release contacts still down.
/// </summary>
internal sealed class PanelTouchClient : IDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);
    // Comfortably above the service's keepalive line interval.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource _cts = new();
    private readonly string _url;
    private readonly string _token;
    private readonly string _sessionId;
    private readonly Action<PanelTouchContact> _onContact;
    private readonly Action _onDrop;

    public PanelTouchClient(string serviceOrigin, string sessionId, string token, Action<PanelTouchContact> onContact, Action onDrop)
    {
        _url = $"{serviceOrigin}/panel/streams/{Uri.EscapeDataString(sessionId)}/touch";
        _token = token;
        _sessionId = sessionId;
        _onContact = onContact;
        _onDrop = onDrop;
        _ = RunAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await ReadOnceAsync(ct)) return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Warn($"touch {_sessionId}: stream dropped: {ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
                return;
            }
            finally
            {
                try { _onDrop(); } catch { }
            }
            try { await Task.Delay(ReconnectDelay, ct); } catch { return; }
        }
    }

    // False when the service refused the stream for good: the session is gone or the token is not accepted.
    private async Task<bool> ReadOnceAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_token}");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Log.Info($"touch {_sessionId}: stream refused ({(int)response.StatusCode})");
            return false;
        }
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body);
        Log.Info($"touch {_sessionId}: stream open");
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(IdleTimeout);
            var line = await reader.ReadLineAsync(idle.Token);
            if (line is null) return true;
            if (PanelTouchContact.TryParse(line, out var contact))
                _onContact(contact);
        }
    }
}
