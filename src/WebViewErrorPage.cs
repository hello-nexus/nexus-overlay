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

    // Auto-retry cadence. CountdownStepMs is both the tick of the visible
    // countdown and the unit its remaining count is labelled in, so the two
    // move together. StaleStateMs has to outlast a failed navigation and the
    // repaint that follows it, since that is the gap a live loop writes its
    // state across, while staying far below the time a recovered session
    // lasts.
    private const int FirstRetryDelayMs = 1000;
    private const int MaxRetryDelayMs = 5000;
    private const int RetryBudgetMs = 60000;
    private const int RetryBackoffFactor = 2;
    private const int CountdownStepMs = 1000;
    private const int StaleStateMs = 15000;

    /// <summary>A cancelled navigation is one the app itself redirected; painting over it would replace a page the user is already on.</summary>
    public static bool ShouldShow(int webErrorStatus) => webErrorStatus != OperationCanceled;

    /// <summary>
    /// Whether the failure is worth retrying on its own. Only the transient
    /// connectivity statuses qualify: those are what a service restart, an
    /// upgrade, or a redeploy looks like from the WebView, and they clear by
    /// themselves. Every other status describes a request that will fail the
    /// same way however often it is repeated, so those pages stay static.
    /// </summary>
    public static bool ShouldAutoRetry(int webErrorStatus) =>
        webErrorStatus is ServerUnreachable or CannotConnect or HostNameNotResolved or Timeout;

    public static string Html(string url, int webErrorStatus, string homeUrl)
    {
        var detail = Describe(webErrorStatus);
        var autoRetry = ShouldAutoRetry(webErrorStatus);
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
        // min-height keeps the buttons from shifting as the status line changes.
        if (autoRetry) sb.Append("#retry{margin:1.25rem 0 0;font-size:.8rem;min-height:1.2em}");
        sb.Append("</style></head><body");
        // The retry URL travels as an attribute, the context ForScript is
        // written for, and never as script text: entities are not decoded
        // inside a script element, so a query string would lose its ampersands.
        if (autoRetry) sb.Append(" onload=\"nexusRetry('").Append(ForScript(url)).Append("')\"");
        sb.Append("><div class=\"box\">");
        sb.Append("<h1>This page didn't load</h1>");
        sb.Append("<p>").Append(Escape(detail)).Append("<code>").Append(Escape(url)).Append("</code></p>");
        sb.Append("<button class=\"primary\" onclick=\"location.replace('").Append(ForScript(url)).Append("')\">Try again</button>");
        sb.Append("<button onclick=\"location.replace('").Append(ForScript(homeUrl)).Append("')\">Back to Nexus</button>");
        if (autoRetry)
        {
            sb.Append("<p id=\"retry\"></p>");
            AppendRetryScript(sb);
        }
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// The retry loop. A failed attempt is answered with a freshly built copy
    /// of this document, so an attempt count and a deadline kept in page
    /// variables would start over on every miss and the loop would never end.
    /// They ride window.name instead, the one store that survives a navigation
    /// in a document whose origin is opaque; session storage throws there, and
    /// the target cannot be probed first because a page that is not a secure
    /// context is refused any subresource request into the loopback address
    /// space. The state carries the moment it was written, because giving up
    /// means honouring a deadline that has passed and that is also what state
    /// left behind by an outage which already recovered looks like: window.name
    /// outlives a successful navigation. Only a write from within the last
    /// StaleStateMs is this loop's, so a later outage starts its own budget
    /// instead of retiring on the previous one. The URL is never written into
    /// this script; it arrives as the argument the body's load handler passes
    /// in.
    /// </summary>
    private static void AppendRetryScript(StringBuilder sb)
    {
        sb.Append("<script>function nexusRetry(u){");
        sb.Append("var l=document.getElementById(\"retry\"),t=\"nexus-retry:\",w=String(window.name||\"\");");
        sb.Append("var now=Date.now(),n=0,end=now+").Append(RetryBudgetMs).Append(';');
        sb.Append("if(w.substring(0,t.length)===t){");
        sb.Append("var p=w.substring(t.length).split(\",\"),pn=parseInt(p[0],10),");
        sb.Append("pe=parseInt(p[1],10),pw=parseInt(p[2],10);");
        sb.Append("var age=now-pw;");
        sb.Append("if(pn>=0&&pe>0&&age>=0&&age<=").Append(StaleStateMs).Append("){n=pn;end=pe;}}");
        sb.Append("if(now>=end){window.name=\"\";");
        sb.Append("l.textContent=\"Nexus is still not answering. Use Try again once it is back.\";return;}");
        sb.Append("var left=Math.round(Math.min(").Append(FirstRetryDelayMs);
        sb.Append("*Math.pow(").Append(RetryBackoffFactor).Append(",n),").Append(MaxRetryDelayMs);
        sb.Append(")/").Append(CountdownStepMs).Append(");");
        sb.Append("function tick(){");
        sb.Append("if(left>0){l.textContent=\"Retrying in \"+left+\"s (attempt \"+(n+1)+\")\";");
        sb.Append("left--;setTimeout(tick,").Append(CountdownStepMs).Append(");return;}");
        sb.Append("l.textContent=\"Reconnecting\u2026 (attempt \"+(n+1)+\")\";");
        sb.Append("window.name=t+(n+1)+\",\"+end+\",\"+Date.now();");
        sb.Append("location.replace(u);}");
        sb.Append("tick();}</script>");
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
