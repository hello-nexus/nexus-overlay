using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Nexus.Overlay.Capture;

internal enum PanelTouchPhase { Down, Move, Up }

/// <summary>One contact from the service's touch stream: X and Y are fractions of the page's width and height.</summary>
internal readonly record struct PanelTouchContact(uint PointerId, PanelTouchPhase Phase, double X, double Y)
{
    /// <summary>Parses "&lt;pointerId&gt; &lt;down|move|up&gt; &lt;x&gt; &lt;y&gt;"; blank keepalive lines and anything malformed fail.</summary>
    public static bool TryParse(string line, out PanelTouchContact contact)
    {
        contact = default;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;
        PanelTouchPhase phase;
        switch (parts[1])
        {
            case "down": phase = PanelTouchPhase.Down; break;
            case "move": phase = PanelTouchPhase.Move; break;
            case "up": phase = PanelTouchPhase.Up; break;
            default: return false;
        }
        contact = new PanelTouchContact(id, phase, Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        return true;
    }
}

/// <summary>
/// Turns contacts into CDP Input.dispatchTouchEvent parameters. Every event carries all points
/// down, as the protocol documents, and a release that leaves others down is a touchMove without
/// it, since touchEnd must carry none. Chromium's synthetic-pointer dispatch releases the omitted
/// point; its default dispatch keeps it down until the last finger lifts. Which path WebView2
/// uses is unverified. Only the first point down is a touchStart: the synthetic path rejects a
/// touchStart while points are active, and both paths press a new id seen in a touchMove.
/// </summary>
internal sealed class CdpTouchState
{
    private readonly SortedDictionary<uint, (double X, double Y)> _down = new();

    /// <summary>The event params for a contact on a page of the given CSS size, or null when it changes nothing.</summary>
    public string? Apply(PanelTouchContact contact, double cssWidth, double cssHeight)
    {
        var at = (contact.X * cssWidth, contact.Y * cssHeight);
        bool known = _down.ContainsKey(contact.PointerId);
        switch (contact.Phase)
        {
            case PanelTouchPhase.Down:
                bool first = _down.Count == 0;
                _down[contact.PointerId] = at;
                return Build(first ? "touchStart" : "touchMove");
            case PanelTouchPhase.Move:
                if (!known) return null;
                _down[contact.PointerId] = at;
                return Build("touchMove");
            default:
                if (!_down.Remove(contact.PointerId)) return null;
                return Build(_down.Count == 0 ? "touchEnd" : "touchMove");
        }
    }

    /// <summary>Cancels every contact still down, or null when none is.</summary>
    public string? ReleaseAll()
    {
        if (_down.Count == 0) return null;
        _down.Clear();
        return Build("touchCancel");
    }

    private string Build(string type)
    {
        var json = new StringBuilder("{\"type\":\"").Append(type).Append("\",\"touchPoints\":[");
        if (type is not ("touchEnd" or "touchCancel"))
        {
            bool first = true;
            foreach (var (id, (x, y)) in _down)
            {
                if (!first) json.Append(',');
                first = false;
                json.Append(CultureInfo.InvariantCulture, $"{{\"x\":{x:0.##},\"y\":{y:0.##},\"id\":{id}}}");
            }
        }
        return json.Append("]}").ToString();
    }
}
