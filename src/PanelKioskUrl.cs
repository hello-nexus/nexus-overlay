namespace Nexus.Overlay;

internal static class PanelKioskUrl
{
    // The overlay identified the panel by EDID, so it declares the surface: the
    // page's viewport guess fails because Windows can drive a panel at any resolution.
    internal static string For(string serviceOrigin, string surface, string token, bool seeThrough) =>
        $"{serviceOrigin}/panel?surface={System.Uri.EscapeDataString(surface)}&token={System.Uri.EscapeDataString(token)}{(seeThrough ? "&backdrop=desktop" : "")}";
}
