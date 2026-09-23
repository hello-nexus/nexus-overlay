using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Nexus.Overlay;

/// <summary>
/// Minimal HTTP client for the local nexus-service: the auth token via
/// <c>/pair</c> (loopback-restricted) and the one state document the host
/// reconciles against via <c>/overlay/state</c>. Everything else flows
/// through the SPA + WebSocket inside the WebView2.
/// </summary>
internal sealed class NexusApi
{
    public string ServiceOrigin { get; }
    public string Token { get; private set; } = "";

    private readonly HttpClient _http;

    public NexusApi(string origin)
    {
        ServiceOrigin = origin.TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri(ServiceOrigin),
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    public async Task<string> PairAsync()
    {
        try
        {
            using var resp = await _http.GetAsync("/pair");
            if (!resp.IsSuccessStatusCode) return "";
            await using var stream = await resp.Content.ReadAsStreamAsync();
            var doc = await JsonSerializer.DeserializeAsync(stream, ApiJson.Default.PairResponse);
            Token = doc?.Token ?? "";
            return Token;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Null = the request failed; the caller changes nothing on null.</summary>
    public async Task<OverlayState?> GetStateAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/overlay/state");
            if (!string.IsNullOrEmpty(Token))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync(stream, ApiJson.Default.OverlayState);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fire and forget: diagnostics must never delay or fail a caller.</summary>
    public void PostClientMem(ClientMemSample sample) => _ = PostClientMemAsync(sample);

    private async Task PostClientMemAsync(ClientMemSample sample)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/diagnostics/client-mem")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(sample, ApiJson.Default.ClientMemSample),
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
            if (!string.IsNullOrEmpty(Token))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        }
        catch
        {
            /* diagnostics POST is best-effort */
        }
    }
}

internal sealed class PairResponse
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";
}

/// <summary>GET /overlay/state. Field names mirror the service DTO.</summary>
internal sealed class OverlayState
{
    [JsonPropertyName("autoLaunch")]
    public bool AutoLaunch { get; set; }
    [JsonPropertyName("reserveMonitor")]
    public bool ReserveMonitor { get; set; } = true;
    [JsonPropertyName("y70Backdrop")]
    public string Y70Backdrop { get; set; } = "";
    [JsonPropertyName("y70CompatibilityRendering")]
    public bool Y70CompatibilityRendering { get; set; }
    [JsonPropertyName("overlayEnabled")]
    public bool OverlayEnabled { get; set; }
    [JsonPropertyName("alwaysOnTop")]
    public bool AlwaysOnTop { get; set; }
    [JsonPropertyName("monitor")]
    public int Monitor { get; set; } = -1;
    [JsonPropertyName("pinned")]
    public int Pinned { get; set; }
    [JsonPropertyName("assignments")]
    public List<DisplayAssignment> Assignments { get; set; } = new();
    [JsonPropertyName("streams")]
    public List<StreamAssignment> Streams { get; set; } = new();
}

internal sealed class DisplayAssignment
{
    [JsonPropertyName("displayId")]
    public string DisplayId { get; set; } = "";
    [JsonPropertyName("panelDeviceId")]
    public string PanelDeviceId { get; set; } = "";
    [JsonPropertyName("reserveMonitor")]
    public bool ReserveMonitor { get; set; } = true;
    [JsonPropertyName("backdrop")]
    public string Backdrop { get; set; } = "";
}

/// <summary>
/// One desired streamed-panel session. SessionIds are boot-scoped and
/// re-minted on any config change, so the reconcile diff is a pure
/// spawn/close on sessionId.
/// </summary>
internal sealed class StreamAssignment
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";
    [JsonPropertyName("panelDeviceId")]
    public string PanelDeviceId { get; set; } = "";
    [JsonPropertyName("cssWidth")]
    public int CssWidth { get; set; }
    [JsonPropertyName("cssHeight")]
    public int CssHeight { get; set; }
    [JsonPropertyName("dpr")]
    public double Dpr { get; set; } = 1.0;
    [JsonPropertyName("fps")]
    public int Fps { get; set; } = 60;
    [JsonPropertyName("bitrateKbps")]
    public int BitrateKbps { get; set; } = 8000;
    [JsonPropertyName("codec")]
    public string Codec { get; set; } = "h264";
}

/// <summary>
/// Host-side WebView2 working-set sample. Property names serialize camelCase
/// to match the service's ClientMemBody: totalWsMB, largestWsMB, largestPid,
/// children.
/// </summary>
internal sealed class ClientMemSample
{
    public string Source { get; set; } = "host";
    public int TotalWsMB { get; set; }
    public int LargestWsMB { get; set; }
    public int LargestPid { get; set; }
    public int Children { get; set; }
}

[JsonSerializable(typeof(ClientMemSample))]
[JsonSerializable(typeof(PairResponse))]
[JsonSerializable(typeof(OverlayState))]
[JsonSerializable(typeof(DisplayAssignment))]
[JsonSerializable(typeof(List<DisplayAssignment>))]
[JsonSerializable(typeof(StreamAssignment))]
[JsonSerializable(typeof(List<StreamAssignment>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class ApiJson : JsonSerializerContext
{
}
