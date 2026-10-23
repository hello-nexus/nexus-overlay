namespace Nexus.Overlay;

internal static class PanelKioskUrl
{
    // The EDID match declares the surface; the page's viewport guess fails at a non-native Windows resolution.
    internal static string For(string serviceOrigin, string surface, string token, bool seeThrough) =>
        $"{serviceOrigin}/panel?surface={System.Uri.EscapeDataString(surface)}&token={System.Uri.EscapeDataString(token)}{(seeThrough ? "&backdrop=desktop" : "")}";
}
