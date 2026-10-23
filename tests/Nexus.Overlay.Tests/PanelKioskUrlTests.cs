using Xunit;

namespace Nexus.Overlay.Tests;

public class PanelKioskUrlTests
{
    [Fact]
    public void Url_carries_the_surface_hint()
    {
        Assert.Equal("http://localhost:9400/panel?surface=y70&token=a%2Bb", PanelKioskUrl.For("http://localhost:9400", "y70", "a+b", false));
    }

    [Fact]
    public void Url_appends_the_desktop_backdrop_when_see_through()
    {
        Assert.Equal("http://localhost:9400/panel?surface=y70&token=t&backdrop=desktop", PanelKioskUrl.For("http://localhost:9400", "y70", "t", true));
    }
}
