using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Nexus.Overlay;

/// <summary>A widget rect in CSS px, relative to the panel page's viewport.</summary>
internal readonly record struct WidgetRect(double X, double Y, double W, double H);

/// <summary>One entry of the panel page's desired widget set.</summary>
internal sealed record WidgetViewSpec(string Id, string Url, WidgetRect Rect, bool Interactive, bool Visible, double Radius = 0, double Zoom = 1);

/// <summary>A pixel rect inside the kiosk client area.</summary>
internal readonly record struct WidgetPixelRect(int Left, int Top, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

internal enum WidgetMessageKind { Unknown, Hello, Sync, Command }

/// <summary>A parsed bridge message from the panel page. Only the fields of <see cref="Kind"/> are set.</summary>
internal sealed class WidgetMessage
{
    public WidgetMessageKind Kind { get; init; }
    public long Seq { get; init; }
    public double ViewportW { get; init; }
    public double ViewportH { get; init; }
    public IReadOnlyList<WidgetViewSpec> Views { get; init; } = Array.Empty<WidgetViewSpec>();
    /// <summary>Ids dropped because the sync listed more than the view limit.</summary>
    public IReadOnlyList<string> OverflowIds { get; init; } = Array.Empty<string>();
    /// <summary>Ids dropped because their url is not an allowed http/https url.</summary>
    public IReadOnlyList<string> InvalidIds { get; init; } = Array.Empty<string>();
    public string CommandId { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
}

/// <summary>Wire format, validation and geometry for the Web View widget bridge. Pure: no Win32, no WebView2.</summary>
internal static class WebViewWidgetProtocol
{
    public const int MaxViews = 4;
    public const int ProtocolVersion = 1;
    private const int MaxMessageChars = 64 * 1024;
    private const int MaxIdChars = 128;
    private const int MaxUrlChars = 2048;

    /// <summary>
    /// Parses one bridge message. Anything malformed or of an unknown type
    /// yields <see cref="WidgetMessageKind.Unknown"/> rather than throwing.
    /// </summary>
    public static WidgetMessage Parse(string? json)
    {
        var unknown = new WidgetMessage { Kind = WidgetMessageKind.Unknown };
        if (string.IsNullOrEmpty(json) || json.Length > MaxMessageChars) return unknown;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return unknown;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) return unknown;
            switch (typeEl.GetString())
            {
                case "nexus.webview.hello":
                    return new WidgetMessage { Kind = WidgetMessageKind.Hello };
                case "nexus.webview.sync":
                    return ParseSync(root) ?? unknown;
                case "nexus.webview.command":
                    return ParseCommand(root) ?? unknown;
                default:
                    return unknown;
            }
        }
        catch (JsonException) { return unknown; }
    }

    private static WidgetMessage? ParseSync(JsonElement root)
    {
        if (!root.TryGetProperty("viewport", out var vp) || vp.ValueKind != JsonValueKind.Object) return null;
        if (!TryNumber(vp, "w", out var vw) || !TryNumber(vp, "h", out var vh)) return null;
        if (!root.TryGetProperty("views", out var viewsEl) || viewsEl.ValueKind != JsonValueKind.Array) return null;

        long seq = 0;
        if (root.TryGetProperty("seq", out var seqEl) && seqEl.ValueKind == JsonValueKind.Number) seqEl.TryGetInt64(out seq);

        var views = new List<WidgetViewSpec>();
        var overflow = new List<string>();
        var invalid = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in viewsEl.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (!el.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
            var id = idEl.GetString();
            if (string.IsNullOrEmpty(id) || id.Length > MaxIdChars || !seen.Add(id)) continue;

            if (views.Count >= MaxViews)
            {
                overflow.Add(id);
                continue;
            }

            var url = el.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString()
                : null;
            if (!IsAllowedUrl(url))
            {
                invalid.Add(id);
                continue;
            }

            if (!el.TryGetProperty("rect", out var rectEl) || rectEl.ValueKind != JsonValueKind.Object
                || !TryNumber(rectEl, "x", out var x) || !TryNumber(rectEl, "y", out var y)
                || !TryNumber(rectEl, "w", out var w) || !TryNumber(rectEl, "h", out var h))
            {
                continue;
            }

            views.Add(new WidgetViewSpec(id, url!, new WidgetRect(x, y, w, h),
                Interactive: ReadBool(el, "interactive"), Visible: ReadBool(el, "visible"), Radius: ReadRadius(el), Zoom: ReadZoom(el)));
        }

        return new WidgetMessage
        {
            Kind = WidgetMessageKind.Sync,
            Seq = seq,
            ViewportW = vw,
            ViewportH = vh,
            Views = views,
            OverflowIds = overflow,
            InvalidIds = invalid,
        };
    }

    private static WidgetMessage? ParseCommand(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return null;
        if (!root.TryGetProperty("command", out var cmdEl) || cmdEl.ValueKind != JsonValueKind.String) return null;
        var id = idEl.GetString();
        var cmd = cmdEl.GetString();
        if (string.IsNullOrEmpty(id) || (cmd != "back" && cmd != "reload")) return null;
        return new WidgetMessage { Kind = WidgetMessageKind.Command, CommandId = id, Command = cmd };
    }

    private static bool TryNumber(JsonElement obj, string name, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number) return false;
        return el.TryGetDouble(out value) && double.IsFinite(value);
    }

    private const double MaxRadius = 1000;

    public const double MinZoom = 0.25;
    public const double MaxZoom = 3;

    private static double ReadZoom(JsonElement obj)
        => TryNumber(obj, "zoom", out var z) && z > 0 ? Math.Clamp(z, MinZoom, MaxZoom) : 1;

    private static double ReadRadius(JsonElement obj)
        => TryNumber(obj, "radius", out var r) ? Math.Clamp(r, 0, MaxRadius) : 0;

    private static bool ReadBool(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    /// <summary>Only absolute http/https urls with a host may be loaded in a widget view.</summary>
    public static bool IsAllowedUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > MaxUrlChars) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(uri.Host);
    }

    /// <summary>Scheme gate for in-view navigations and redirects, which can exceed the configured-url length cap.</summary>
    public static bool IsHttpScheme(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// True when <paramref name="source"/> (the posting frame's url) shares
    /// scheme, host and port with the url the kiosk itself navigated to.
    /// </summary>
    public static bool IsPanelOrigin(string? source, string panelUrl)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var s)) return false;
        if (!Uri.TryCreate(panelUrl, UriKind.Absolute, out var p)) return false;
        return string.Equals(s.Scheme, p.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.Host, p.Host, StringComparison.OrdinalIgnoreCase)
            && s.Port == p.Port;
    }

    /// <summary>
    /// Maps a CSS rect to kiosk client pixels with an independent scale per
    /// axis (native bounds over page viewport), clamped to the bounds. An
    /// unusable viewport or rect yields an empty rect.
    /// </summary>
    public static WidgetPixelRect MapRect(WidgetRect rect, double viewportW, double viewportH, int boundsW, int boundsH)
    {
        if (!(viewportW > 0) || !(viewportH > 0) || boundsW <= 0 || boundsH <= 0) return default;
        if (!double.IsFinite(rect.X) || !double.IsFinite(rect.Y) || !double.IsFinite(rect.W) || !double.IsFinite(rect.H)) return default;
        if (rect.W <= 0 || rect.H <= 0) return default;

        var sx = boundsW / viewportW;
        var sy = boundsH / viewportH;
        var left = Math.Clamp((int)Math.Round(rect.X * sx), 0, boundsW);
        var top = Math.Clamp((int)Math.Round(rect.Y * sy), 0, boundsH);
        var right = Math.Clamp((int)Math.Round((rect.X + rect.W) * sx), 0, boundsW);
        var bottom = Math.Clamp((int)Math.Round((rect.Y + rect.H) * sy), 0, boundsH);
        return new WidgetPixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>Corner radius in pixels per axis, scaled like <see cref="MapRect"/>.</summary>
    public static (int X, int Y) MapRadius(double radius, double viewportW, double viewportH, int boundsW, int boundsH)
    {
        if (!(radius > 0) || !(viewportW > 0) || !(viewportH > 0) || boundsW <= 0 || boundsH <= 0) return (0, 0);
        return ((int)Math.Round(radius * boundsW / viewportW), (int)Math.Round(radius * boundsH / viewportH));
    }

    /// <summary>
    /// Runs in every frame of a hosted view and reports whether a text field
    /// has focus after user input. Child frames relay through parent.postMessage,
    /// so the top frame also sees fields inside cross-origin iframes.
    /// </summary>
    public const string EditFocusScript = """
        (() => {
          const NON_TEXT = /^(button|checkbox|color|file|hidden|image|radio|range|reset|submit)$/i;
          const frames = new WeakMap();
          const editable = (el) => !!el && (el.isContentEditable
            || (el.tagName === 'TEXTAREA' && !el.readOnly && !el.disabled)
            || (el.tagName === 'INPUT' && !NON_TEXT.test(el.type) && !el.readOnly && !el.disabled));
          const focused = () => {
            let el = document.activeElement;
            while (el && el.shadowRoot && el.shadowRoot.activeElement) el = el.shadowRoot.activeElement;
            if (el && (el.tagName === 'IFRAME' || el.tagName === 'FRAME')) return frames.get(el.contentWindow) === true;
            return editable(el);
          };
          const send = (v) => {
            if (v && !(navigator.userActivation && navigator.userActivation.isActive)) return;
            if (window !== top) parent.postMessage({ nexusEditableFocus: v }, '*');
            else if (window.chrome && chrome.webview) chrome.webview.postMessage({ nexusEditableFocus: v });
          };
          // Deferred so a focusout/focusin pair yields one report and activeElement has settled.
          const check = () => setTimeout(() => send(document.hasFocus() && focused()), 0);
          addEventListener('focusin', check, true);
          addEventListener('focusout', check, true);
          addEventListener('pointerup', check, true);
          addEventListener('message', (e) => {
            if (!e.source || !e.data || typeof e.data.nexusEditableFocus !== 'boolean') return;
            frames.set(e.source, e.data.nexusEditableFocus);
            check();
          });
          // Clears whatever the previous document in this frame last reported.
          send(false);
        })();
        """;

    /// <summary>Accepts only <c>{"nexusEditableFocus":bool}</c> from a hosted view; anything else is null.</summary>
    public static bool? ParseEditFocus(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 256) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            bool? value = null;
            foreach (var prop in root.EnumerateObject())
            {
                if (value is not null || prop.Name != "nexusEditableFocus") return null;
                if (prop.Value.ValueKind == JsonValueKind.True) value = true;
                else if (prop.Value.ValueKind == JsonValueKind.False) value = false;
                else return null;
            }
            return value;
        }
        catch (JsonException) { return null; }
    }

    public static string BuildCaps(bool interactive)
        => Write(w =>
        {
            w.WriteString("type", "nexus.webview.caps");
            w.WriteNumber("version", ProtocolVersion);
            w.WriteBoolean("supported", true);
            w.WriteNumber("maxViews", MaxViews);
            w.WriteBoolean("interactive", interactive);
        });

    public static string BuildState(string id, string state, string? code, bool canGoBack)
        => Write(w =>
        {
            w.WriteString("type", "nexus.webview.state");
            w.WriteString("id", id);
            w.WriteString("state", state);
            if (code is not null) w.WriteString("code", code);
            w.WriteBoolean("canGoBack", canGoBack);
        });

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

internal enum WidgetActionKind
{
    /// <summary>Create the native view and navigate it to <see cref="WidgetAction.Spec"/>'s url.</summary>
    Create,
    /// <summary>The configured url changed; navigate the existing view.</summary>
    Navigate,
    /// <summary>Rect, interactive or visible changed; never touches the page.</summary>
    Layout,
    /// <summary>The page no longer lists the view.</summary>
    Destroy,
    /// <summary>Report state error with <see cref="WidgetAction.Code"/>.</summary>
    ReportError,
}

internal readonly record struct WidgetAction(WidgetActionKind Kind, string Id, WidgetViewSpec? Spec = null, string? Code = null);

/// <summary>Diffs each full-set sync against the live views; owns the url-change rule, crash backoff and sync watchdog.</summary>
internal sealed class WidgetRegistry
{
    public const long WatchdogMs = 3_000;
    public const long CrashBackoffMs = 5_000;

    private sealed class Entry
    {
        public required string ConfiguredUrl;
        public required WidgetViewSpec Spec;
    }

    private readonly Dictionary<string, Entry> _live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _crashedAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private long _lastSyncMs;
    private long _lastSeq = long.MinValue;

    public int LiveCount => _live.Count;

    public bool IsLive(string id) => _live.ContainsKey(id);

    public IReadOnlyCollection<string> LiveIds => _live.Keys;

    /// <summary>Last configured url for a live view, null if not live.</summary>
    public string? ConfiguredUrl(string id) => _live.TryGetValue(id, out var e) ? e.ConfiguredUrl : null;

    /// <summary>Applies one sync. A sync older than one already applied is ignored.</summary>
    public List<WidgetAction> Apply(WidgetMessage sync, long nowMs)
    {
        var actions = new List<WidgetAction>();
        if (sync.Seq < _lastSeq) return actions;
        _lastSeq = sync.Seq;
        _lastSyncMs = nowMs;

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in sync.Views) wanted.Add(v.Id);

        foreach (var id in new List<string>(_live.Keys))
        {
            if (wanted.Contains(id)) continue;
            _live.Remove(id);
            actions.Add(new WidgetAction(WidgetActionKind.Destroy, id));
        }
        foreach (var id in new List<string>(_crashedAt.Keys))
        {
            if (!wanted.Contains(id)) _crashedAt.Remove(id);
        }

        var errored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in sync.OverflowIds) Report(actions, errored, id, "limit");
        foreach (var id in sync.InvalidIds) Report(actions, errored, id, "url");
        _reported.IntersectWith(errored);

        foreach (var spec in sync.Views)
        {
            if (_live.TryGetValue(spec.Id, out var entry))
            {
                if (!string.Equals(entry.ConfiguredUrl, spec.Url, StringComparison.Ordinal))
                {
                    entry.ConfiguredUrl = spec.Url;
                    actions.Add(new WidgetAction(WidgetActionKind.Navigate, spec.Id, spec));
                }
                if (entry.Spec.Rect != spec.Rect || entry.Spec.Radius != spec.Radius || entry.Spec.Zoom != spec.Zoom || entry.Spec.Interactive != spec.Interactive || entry.Spec.Visible != spec.Visible)
                {
                    actions.Add(new WidgetAction(WidgetActionKind.Layout, spec.Id, spec));
                }
                entry.Spec = spec;
                continue;
            }

            if (_crashedAt.TryGetValue(spec.Id, out var crashed) && nowMs - crashed < CrashBackoffMs) continue;
            _crashedAt.Remove(spec.Id);
            _live[spec.Id] = new Entry { ConfiguredUrl = spec.Url, Spec = spec };
            actions.Add(new WidgetAction(WidgetActionKind.Create, spec.Id, spec));
        }
        return actions;
    }

    private void Report(List<WidgetAction> actions, HashSet<string> errored, string id, string code)
    {
        errored.Add(id + "|" + code);
        if (_reported.Add(id + "|" + code)) actions.Add(new WidgetAction(WidgetActionKind.ReportError, id, Code: code));
    }

    /// <summary>
    /// A live view's process died or its creation failed: forget it and hold
    /// off recreating it until a sync arrives after the backoff.
    /// </summary>
    public void MarkCrashed(string id, long nowMs)
    {
        if (_live.Remove(id)) _crashedAt[id] = nowMs;
    }

    /// <summary>Forgets every live view; returns their ids so the host can destroy them. Crash backoff survives.</summary>
    public List<string> Clear()
    {
        var ids = new List<string>(_live.Keys);
        _live.Clear();
        _reported.Clear();
        _lastSeq = long.MinValue;
        return ids;
    }

    /// <summary>True when views are live and no sync arrived within <see cref="WatchdogMs"/>.</summary>
    public bool WatchdogExpired(long nowMs) => _live.Count > 0 && nowMs - _lastSyncMs >= WatchdogMs;
}

/// <summary>
/// Whether a view's page has a text field focused. The page's report is not
/// trusted alone: focus is taken only within <see cref="PointerWindowMs"/> of a
/// pointer-down on the view that no earlier report used. Any tap on the view
/// arms one activation, so within that window a page can also focus a field
/// from script; without a tap it cannot take the foreground.
/// </summary>
internal sealed class WidgetEditFocus
{
    /// <summary>The page reports after pointerup, so this also covers a press held on the field.</summary>
    public const long PointerWindowMs = 3_000;

    /// <summary>A page that blurs and refocuses its field (Enter to the next field, a re-mount) keeps focus across this gap.</summary>
    public const long BlurGraceMs = 400;

    private bool _live;
    private long? _pointerDownAt;
    private long? _blurAt;

    public bool Focused { get; private set; }

    /// <summary>When focus ends unless the page reports a field again; null when no blur is pending.</summary>
    public long? BlurDeadline => _blurAt + BlurGraceMs;

    /// <summary>A pointer went down on the view (a window message the page cannot forge).</summary>
    public void OnPointerDown(long nowMs)
    {
        if (_live) _pointerDownAt = nowMs;
    }

    /// <summary>A pointer went down on another view, so this view's tap no longer authorizes anything.</summary>
    public void Disarm() => _pointerDownAt = null;

    public void OnReport(bool focused, long nowMs)
    {
        if (!_live)
        {
            Clear();
            return;
        }
        if (!focused)
        {
            if (Focused) _blurAt ??= nowMs;
            return;
        }
        if (Focused)
        {
            _blurAt = null;
            return;
        }
        if (_pointerDownAt is not { } at || nowMs - at > PointerWindowMs) return;
        _pointerDownAt = null;
        Focused = true;
    }

    /// <summary>Ends focus once a blur has outlasted the grace; true when it did.</summary>
    public bool ExpireBlur(long nowMs)
    {
        if (BlurDeadline is not { } deadline || nowMs < deadline) return false;
        Focused = false;
        _blurAt = null;
        return true;
    }

    /// <summary>Live = shown and interactive; leaving that clears focus at once, which the page does not report.</summary>
    public void SetLive(bool live)
    {
        _live = live;
        if (!live) Clear();
    }

    /// <summary>A new document starts with nothing focused, and a tap that navigated must not authorize its autofocus.</summary>
    public void OnNavigationStarting() => Clear();

    private void Clear()
    {
        Focused = false;
        _pointerDownAt = null;
        _blurAt = null;
    }
}

/// <summary>Per-view navigation bookkeeping: remembers the settled state and swallows the failure that follows a blocked download.</summary>
internal sealed class WidgetNavState
{
    private bool _downloadCancelled;

    public string? SettledState { get; private set; }
    public string? SettledCode { get; private set; }

    public void OnNavigationStarting() => _downloadCancelled = false;

    public void OnDownloadCancelled() => _downloadCancelled = true;

    /// <summary>True once per cancelled download: the next failed completion must not be reported.</summary>
    public bool TryConsumeDownloadFailure()
    {
        var was = _downloadCancelled;
        _downloadCancelled = false;
        return was;
    }

    public void OnState(string state, string? code)
    {
        if (state == "loading") return;
        SettledState = state;
        SettledCode = code;
    }
}

/// <summary>Rate limit for the cursor display-count restore driven by mouse moves.</summary>
internal struct CursorRestoreThrottle
{
    private readonly long _intervalMs;
    private long _lastAt;
    private bool _used;

    public CursorRestoreThrottle(long intervalMs)
    {
        _intervalMs = intervalMs;
    }

    /// <summary>True, and starts a new interval, when at least the interval passed since the last acquire.</summary>
    public bool TryAcquire(long nowMs)
    {
        if (_used && nowMs - _lastAt < _intervalMs) return false;
        _used = true;
        _lastAt = nowMs;
        return true;
    }
}

/// <summary>Decides whether a flagged real mouse move still explains a cursor-moved event.</summary>
internal static class RealMouseSignal
{
    public const long MaxAgeMs = 100;

    /// <summary>False for an unset flag (negative) or one older than <see cref="MaxAgeMs"/>.</summary>
    public static bool IsFresh(long flaggedAtMs, long nowMs) => flaggedAtMs >= 0 && nowMs - flaggedAtMs <= MaxAgeMs;
}

/// <summary>Which ShowCursor call moves a display count toward zero.</summary>
internal static class CursorCountClamp
{
    /// <summary>True to call ShowCursor(true), false for ShowCursor(false), null when the count is already zero.</summary>
    public static bool? NextCall(int count) => count < 0 ? true : count > 0 ? false : null;
}
