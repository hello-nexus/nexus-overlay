using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Qos.Overlay;

/// <summary>
/// Minimal HTTP client for the local qos-service. Pulls the auth token
/// via <c>/pair</c> (LAN-restricted) and reads <c>UiSettings</c> via
/// <c>/preferences</c> for initial Z-order. Anything else flows through
/// the SPA + WebSocket inside the WebView2.
/// </summary>
internal sealed class QosApi
{
    public string ServiceOrigin { get; }
    public string Token { get; private set; } = "";

    private readonly HttpClient _http;

    public QosApi(string origin)
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
/// Tiny subset of the service's UiSettings that the host actually consumes.
/// JSON deserialization is lenient about extra fields, so we only need
/// these on the wire.
/// </summary>
internal sealed class UiPrefs
{
    [JsonPropertyName("overlayWidgetsEnabled")]
    public bool OverlayWidgetsEnabled { get; set; }
    [JsonPropertyName("overlayWidgetsAlwaysOnTop")]
    public bool OverlayWidgetsAlwaysOnTop { get; set; }
    /// <summary>
    /// Monitor index (zero-based) where the single overlay should render.
    /// -1 = "use the primary monitor" (sentinel for unset / first-run).
    /// </summary>
    [JsonPropertyName("overlayWidgetsMonitor")]
    public int OverlayWidgetsMonitor { get; set; } = -1;
}

[JsonSerializable(typeof(PairResponse))]
[JsonSerializable(typeof(UiPrefs))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class ApiJson : JsonSerializerContext
{
}
