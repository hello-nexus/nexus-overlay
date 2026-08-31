using System;
using System.Text;

namespace Nexus.Overlay;

/// <summary>
/// The document shown in place of Edge's own failure page when a navigation
/// inside the app fails.
/// </summary>
/// <remarks>
/// WebView2 answers a failed navigation with the browser's built-in error page
/// ("This localhost page can't be found"), which reads as a broken app rather
/// than a Nexus screen. Navigating to this string instead keeps the failure in
/// the app's own language and offers the only two useful actions: retry, or
/// go back to the dashboard.
/// </remarks>
public static class WebViewErrorPage
{
    // COREWEBVIEW2_WEB_ERROR_STATUS values.
    private const int ServerUnreachable = 6;
    private const int Timeout = 7;
    private const int CannotConnect = 12;
    private const int HostNameNotResolved = 13;
    private const int OperationCanceled = 14;

    /// <summary>A cancelled navigation is one the app itself redirected; painting over it would replace a page the user is already on.</summary>
    public static bool ShouldShow(int webErrorStatus) => webErrorStatus != OperationCanceled;

    public static string Html(string url, int webErrorStatus, string homeUrl)
    {
        var detail = Describe(webErrorStatus);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
        sb.Append("<style>");
        sb.Append("html,body{height:100%;margin:0}");
        sb.Append("body{display:flex;align-items:center;justify-content:center;background:#121212;");
        sb.Append("color:#f2f2f2;font-family:Lexend,-apple-system,'Segoe UI',system-ui,sans-serif;-webkit-user-select:none;user-select:none}");
        sb.Append(".box{max-width:26rem;padding:2rem;text-align:center}");
        sb.Append("h1{margin:0 0 .5rem;font-size:1.35rem;font-weight:600}");
        sb.Append("p{margin:0 0 1.5rem;font-size:.9rem;line-height:1.5;color:#9a9a9a}");
        sb.Append("code{display:block;margin-top:.75rem;font-size:.75rem;color:#6f6f6f;word-break:break-all}");
        sb.Append("button{font:inherit;font-size:.85rem;padding:.55rem 1.2rem;margin:0 .25rem;border-radius:999px;");
        sb.Append("border:1px solid #3a3a3a;background:#1e1e1e;color:#f2f2f2;cursor:pointer}");
        sb.Append("button.primary{background:#a855f7;border-color:#a855f7}");
        sb.Append("</style></head><body><div class=\"box\">");
        sb.Append("<h1>This page didn't load</h1>");
        sb.Append("<p>").Append(Escape(detail)).Append("<code>").Append(Escape(url)).Append("</code></p>");
        sb.Append("<button class=\"primary\" onclick=\"location.replace('").Append(ForScript(url)).Append("')\">Try again</button>");
        sb.Append("<button onclick=\"location.replace('").Append(ForScript(homeUrl)).Append("')\">Back to Nexus</button>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static string Describe(int status) => status switch
    {
        ServerUnreachable or CannotConnect or HostNameNotResolved =>
            "Nexus isn't answering on this machine. It may still be starting up.",
        Timeout => "Nexus took too long to answer.",
        _ => "Nexus couldn't open this page.",
    };

    /// <summary>
    /// A URL sitting inside a JS string literal inside an HTML attribute: the
    /// attribute is decoded before the script is parsed, so an entity-escaped
    /// quote would still close the literal. Percent-encode the quotes instead,
    /// which a URL carries harmlessly.
    /// </summary>
    private static string ForScript(string url) => Escape(url
        .Replace("'", "%27", StringComparison.Ordinal)
        .Replace("\"", "%22", StringComparison.Ordinal)
        .Replace("\\", "%5C", StringComparison.Ordinal));

    private static string Escape(string s) => s
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);
}
