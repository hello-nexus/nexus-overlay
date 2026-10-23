using Xunit;

namespace Nexus.Overlay.Tests;

public class PanelKioskUrlTests
{
    [Fact]
    public void Y70_url_carries_the_surface_hint()
    {
        Assert.Equal("http://localhost:9400/panel?surface=y70&token=a%2Bb", PanelKioskUrl.Y70("http://localhost:9400", "a+b", false));
    }

    [Fact]
    public void Y70_url_appends_the_desktop_backdrop_when_see_through()
    {
        Assert.Equal("http://localhost:9400/panel?surface=y70&token=t&backdrop=desktop", PanelKioskUrl.Y70("http://localhost:9400", "t", true));
    }
}
