using System;
using Xunit;

namespace Nexus.Overlay.Tests;

public class WebViewErrorPageTests
{
    // COREWEBVIEW2_WEB_ERROR_STATUS values, mirrored from the page they drive.
    private const int Unknown = 0;
    private const int CertificateCommonNameIsIncorrect = 1;
    private const int CertificateExpired = 2;
    private const int ServerUnreachable = 6;
    private const int Timeout = 7;
    private const int ErrorHttpInvalidServerResponse = 8;
    private const int ConnectionAborted = 9;
    private const int ConnectionReset = 10;
    private const int Disconnected = 11;
    private const int CannotConnect = 12;
    private const int HostNameNotResolved = 13;
    private const int OperationCanceled = 14;
    private const int RedirectFailed = 15;

    private const string HomeUrl = "http://localhost:9400/?token=homer";
    private const string PlainUrl = "http://localhost:9400/dashboard?token=abc123";

    [Theory]
    [InlineData(Unknown)]
    [InlineData(ServerUnreachable)]
    [InlineData(Timeout)]
    [InlineData(CannotConnect)]
    [InlineData(HostNameNotResolved)]
    [InlineData(RedirectFailed)]
    public void Still_paints_over_every_failure_except_a_cancelled_one(int status)
    {
        Assert.True(WebViewErrorPage.ShouldShow(status));
        Assert.False(WebViewErrorPage.ShouldShow(OperationCanceled));
    }

    [Theory]
    [InlineData(ServerUnreachable)]
    [InlineData(CannotConnect)]
    [InlineData(HostNameNotResolved)]
    [InlineData(Timeout)]
    [InlineData(ConnectionAborted)]
    [InlineData(ConnectionReset)]
    [InlineData(Disconnected)]
    [InlineData(ErrorHttpInvalidServerResponse)]
    public void Auto_retries_the_statuses_a_restart_can_produce(int status)
    {
        Assert.True(WebViewErrorPage.ShouldAutoRetry(status));
    }

    /// A reload against a stopped service was measured reporting Unknown, so
    /// that status has to retry: gating it out is what left the real failure
    /// parked on a static page.
    [Fact]
    public void Auto_retries_the_unknown_status_a_stopped_service_reports()
    {
        Assert.True(WebViewErrorPage.ShouldAutoRetry(Unknown));
    }

    [Theory]
    [InlineData(OperationCanceled)]
    [InlineData(CertificateCommonNameIsIncorrect)]
    [InlineData(CertificateExpired)]
    [InlineData(RedirectFailed)]
    public void Leaves_the_statuses_waiting_cannot_fix_static(int status)
    {
        Assert.False(WebViewErrorPage.ShouldAutoRetry(status));
    }

    [Fact]
    public void Emits_the_retry_loop_for_a_transient_failure()
    {
        var html = WebViewErrorPage.Html(PlainUrl, ServerUnreachable, HomeUrl);

        Assert.Contains("<script>function nexusRetry(", html, StringComparison.Ordinal);
        Assert.Contains("onload=\"nexusRetry(", html, StringComparison.Ordinal);
        Assert.Contains("id=\"retry\"", html, StringComparison.Ordinal);
        Assert.Contains("location.replace(u)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Emits_no_script_at_all_for_a_permanent_failure()
    {
        var html = WebViewErrorPage.Html(PlainUrl, CertificateExpired, HomeUrl);

        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("nexusRetry", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"retry\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Retry_loop_backs_off_to_a_cap_and_stops()
    {
        var html = WebViewErrorPage.Html(PlainUrl, Timeout, HomeUrl);

        // The budget bounds the loop, the backoff factor and the cap shape the
        // wait, and the give-up text is what the status line ends on.
        Assert.Contains("end=now+60000", html, StringComparison.Ordinal);
        Assert.Contains("Math.min(1000*Math.pow(2,n),5000)", html, StringComparison.Ordinal);
        Assert.Contains("setTimeout(tick,1000)", html, StringComparison.Ordinal);
        Assert.Contains("Nexus is still not answering.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Retry_state_rides_window_name_and_clears_on_give_up()
    {
        var html = WebViewErrorPage.Html(PlainUrl, ServerUnreachable, HomeUrl);

        // Page variables reset on every repaint, so the count, the deadline and
        // the moment of the write are carried across and dropped on give-up.
        Assert.Contains("window.name=t+(n+1)+\",\"+end+\",\"+Date.now();", html, StringComparison.Ordinal);
        Assert.Contains("if(now>=end){window.name=\"\";", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Carried_state_is_taken_only_while_it_is_fresh()
    {
        var html = WebViewErrorPage.Html(PlainUrl, ServerUnreachable, HomeUrl);

        // An expired deadline means give up, and it is also what an outage that
        // already recovered leaves behind, so the age of the write is what
        // separates them. Widening this guard to the budget would retire the
        // loop before its first attempt after any recovered outage.
        Assert.Contains("var age=now-pw;", html, StringComparison.Ordinal);
        Assert.Contains("if(pn>=0&&pe>0&&age>=0&&age<=15000){n=pn;end=pe;}", html, StringComparison.Ordinal);
        Assert.DoesNotContain("pe>now-60000", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ServerUnreachable)]
    [InlineData(CertificateExpired)]
    public void Keeps_both_manual_buttons_in_either_mode(int status)
    {
        var html = WebViewErrorPage.Html(PlainUrl, status, HomeUrl);

        Assert.Contains(">Try again</button>", html, StringComparison.Ordinal);
        Assert.Contains(">Back to Nexus</button>", html, StringComparison.Ordinal);
        Assert.Contains("onclick=\"location.replace('" + PlainUrl + "')\"", html, StringComparison.Ordinal);
        Assert.Contains("onclick=\"location.replace('" + HomeUrl + "')\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quoted_token_url_survives_into_the_load_handler_intact()
    {
        var url = "http://localhost:9400/dashboard?token=a'b\"c&next=/x";

        var html = WebViewErrorPage.Html(url, ServerUnreachable, HomeUrl);

        // Quotes are percent-encoded so they cannot close the literal, and the
        // ampersand is entity-escaped so the attribute decodes back to the URL
        // the navigation used.
        var argument = ArgumentOf(html, "onload=\"nexusRetry('");
        Assert.Equal("http://localhost:9400/dashboard?token=a%27b%22c&amp;next=/x", argument);
        Assert.DoesNotContain("'", argument, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", argument, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", argument, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quoted_token_url_survives_into_the_manual_button_intact()
    {
        var url = "http://localhost:9400/dashboard?token=a'b\"c&next=/x";

        var html = WebViewErrorPage.Html(url, ServerUnreachable, HomeUrl);

        var argument = ArgumentOf(html, "onclick=\"location.replace('");
        Assert.Equal("http://localhost:9400/dashboard?token=a%27b%22c&amp;next=/x", argument);
    }

    [Fact]
    public void Never_writes_the_url_into_the_script_body()
    {
        // Entities are not decoded inside a script element, so a URL inlined
        // there would carry its escaped ampersands into the request.
        var html = WebViewErrorPage.Html(PlainUrl, ServerUnreachable, HomeUrl);

        var start = html.IndexOf("<script>", StringComparison.Ordinal);
        var end = html.IndexOf("</script>", StringComparison.Ordinal);
        Assert.InRange(start, 0, end);
        var script = html[start..end];

        Assert.DoesNotContain("abc123", script, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", script, StringComparison.Ordinal);
    }

    private static string ArgumentOf(string html, string opening)
    {
        var start = html.IndexOf(opening, StringComparison.Ordinal);
        Assert.True(start >= 0, "expected the page to contain " + opening);
        start += opening.Length;
        var end = html.IndexOf("')", start, StringComparison.Ordinal);
        Assert.True(end > start, "expected the call to be closed");
        return html[start..end];
    }
}
