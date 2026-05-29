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
}

internal sealed class PairResponse
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";
}

/// <summary>
/// Tiny subset of the service's nested preferences shape that the host
/// actually consumes. JSON deserialization is lenient about extra fields,
/// so we only need these on the wire.
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
    /// Pinned overlay-widget entries. Only the count is consumed here -
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
    /// that land on it back to a normal monitor. Defaults on; older services
    /// that omit the field leave it on via this initializer.
    /// </summary>
    [JsonPropertyName("reserveMonitor")]
    public bool ReserveMonitor { get; set; } = true;
}

/// <summary>
/// Stub for counting only. Per-entry fields are consumed by the SPA,
/// not the native host.
/// </summary>
internal sealed class OverlayLayoutEntry { }

[JsonSerializable(typeof(PairResponse))]
[JsonSerializable(typeof(UiPrefs))]
[JsonSerializable(typeof(OverlayBlock))]
[JsonSerializable(typeof(PanelBlock))]
[JsonSerializable(typeof(OverlayLayoutEntry))]
[JsonSerializable(typeof(System.Collections.Generic.List<OverlayLayoutEntry>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class ApiJson : JsonSerializerContext
{
}
