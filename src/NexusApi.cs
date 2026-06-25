using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Nexus.Overlay;

/// <summary>
/// Minimal HTTP client for the local nexus-service. Pulls the auth token
/// via <c>/pair</c> (LAN-restricted) and reads <c>UiSettings</c> via
/// <c>/preferences</c> for initial Z-order. Anything else flows through
/// the SPA + WebSocket inside the WebView2.
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

    public async Task<UiPrefs> GetPreferencesAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/preferences");
            if (!string.IsNullOrEmpty(Token))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return new UiPrefs();
            await using var stream = await resp.Content.ReadAsStreamAsync();
            var doc = await JsonSerializer.DeserializeAsync(stream, ApiJson.Default.UiPrefs);
            return doc ?? new UiPrefs();
        }
        catch
        {
            return new UiPrefs();
        }
    }

    /// <summary>
    /// Monitor-panel assignments (displayId -> panelDeviceId) driving the
    /// per-monitor kiosk reconcile. Empty list on any failure - the caller
    /// treats that as "close nothing new, spawn nothing" only when the
    /// service is unreachable, so transient errors don't tear kiosks down.
    /// Null = request failed; empty list = service says no assignments.
    /// </summary>
    public async Task<System.Collections.Generic.List<DisplayAssignment>?> GetDisplayAssignmentsAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/displays/assignments");
            if (!string.IsNullOrEmpty(Token))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync();
            var doc = await JsonSerializer.DeserializeAsync(stream, ApiJson.Default.DisplayAssignmentsResponse);
            return doc?.Assignments ?? new System.Collections.Generic.List<DisplayAssignment>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fire-and-forget POST of a WebView2 working-set sample to
    /// <c>/diagnostics/client-mem</c> (loopback-only). Lands in nexus-service.log
    /// next to the renderer's own samples so a memory leak's host-side curve is
    /// visible in the log a tester submits. Best-effort: never awaited, never
    /// throws into the sampler.
    /// </summary>
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

/// <summary>
/// Subset of the service's nested preferences the host consumes.
/// Deserialization ignores extra fields, so only these are on the wire.
/// </summary>
internal sealed class UiPrefs
{
    [JsonPropertyName("overlay")]
    public OverlayBlock Overlay { get; set; } = new();
    [JsonPropertyName("panel")]
    public PanelBlock Panel { get; set; } = new();
}

internal sealed class OverlayBlock
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
    [JsonPropertyName("alwaysOnTop")]
    public bool AlwaysOnTop { get; set; }
    /// <summary>
    /// Monitor index (zero-based) where the single overlay should render.
    /// -1 = "use the primary monitor" (sentinel for unset / first-run).
    /// </summary>
    [JsonPropertyName("monitor")]
    public int Monitor { get; set; } = -1;
    /// <summary>
    /// Pinned overlay-widget entries. Only the count is consumed here;
    /// per-entry rendering happens inside the WebView2 SPA.
    /// </summary>
    [JsonPropertyName("layout")]
    public System.Collections.Generic.List<OverlayLayoutEntry> Layout { get; set; } = new();
}

internal sealed class PanelBlock
{
    /// <summary>
    /// Auto-open the fullscreen panel kiosk window when a recognized HYTE
    /// touch panel is connected.
    /// </summary>
    [JsonPropertyName("autoLaunch")]
    public bool AutoLaunch { get; set; }

    /// <summary>
    /// Keep the panel monitor exclusive to the kiosk: relocate foreign windows
    /// that land on it back to a normal monitor. Defaults on; services that
    /// omit the field leave it on via this initializer.
    /// </summary>
    [JsonPropertyName("reserveMonitor")]
    public bool ReserveMonitor { get; set; } = true;
}

/// <summary>
/// Stub for counting only. Per-entry fields are consumed by the SPA,
/// not the native host.
/// </summary>
internal sealed class OverlayLayoutEntry { }

internal sealed class DisplayAssignment
{
    [JsonPropertyName("displayId")]
    public string DisplayId { get; set; } = "";
    [JsonPropertyName("panelDeviceId")]
    public string PanelDeviceId { get; set; } = "";
    /// <summary>Per-panel "keep clear of other windows" (record setting; default on).</summary>
    [JsonPropertyName("reserveMonitor")]
    public bool ReserveMonitor { get; set; } = true;
}

internal sealed class DisplayAssignmentsResponse
{
    [JsonPropertyName("assignments")]
    public System.Collections.Generic.List<DisplayAssignment> Assignments { get; set; } = new();
}

/// <summary>
/// Host-side WebView2 working-set sample. Property names serialize camelCase
/// (see <see cref="ApiJson"/> below) to match the service's
/// <c>ClientMemBody</c> (Source="host"): totalWsMB, largestWsMB, largestPid,
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
[JsonSerializable(typeof(UiPrefs))]
[JsonSerializable(typeof(OverlayBlock))]
[JsonSerializable(typeof(PanelBlock))]
[JsonSerializable(typeof(OverlayLayoutEntry))]
[JsonSerializable(typeof(System.Collections.Generic.List<OverlayLayoutEntry>))]
[JsonSerializable(typeof(DisplayAssignment))]
[JsonSerializable(typeof(DisplayAssignmentsResponse))]
[JsonSerializable(typeof(System.Collections.Generic.List<DisplayAssignment>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class ApiJson : JsonSerializerContext
{
}
