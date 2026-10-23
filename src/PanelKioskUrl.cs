namespace Nexus.Overlay;

internal static class PanelKioskUrl
{
    // The overlay identified the Y70 by EDID, so the page must not guess its
    // surface from the viewport: Windows can drive the panel at any resolution.
    internal static string Y70(string serviceOrigin, string token, bool seeThrough) =>
        $"{serviceOrigin}/panel?surface=y70&token={System.Uri.EscapeDataString(token)}{(seeThrough ? "&backdrop=desktop" : "")}";
}
