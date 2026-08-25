using Xunit;

namespace Nexus.Overlay.Tests;

public class LogRedactTests
{
    [Fact]
    public void Masks_the_pair_token_in_a_panel_url()
    {
        Assert.Equal(
            "http://localhost:9400/panel?token=***",
            LogRedact.Url("http://localhost:9400/panel?token=Zm9vYmFyYmF6cXV1eA"));
    }

    [Fact]
    public void Keeps_non_sensitive_parameters_intact()
    {
        Assert.Equal(
            "http://localhost:9400/overlay?monitor=1&token=***",
            LogRedact.Url("http://localhost:9400/overlay?monitor=1&token=abc123"));
    }

    [Fact]
    public void Masks_every_sensitive_key_case_insensitively()
    {
        Assert.Equal(
            "https://x/y?Token=***&apiKey=***&key=***&secret=***&code=***",
            LogRedact.Url("https://x/y?Token=t&apiKey=live&key=k&secret=s&code=c"));
    }

    [Theory]
    [InlineData("access_token")]
    [InlineData("id_token")]
    [InlineData("refresh_token")]
    [InlineData("api_key")]
    [InlineData("client_secret")]
    [InlineData("authorization")]
    [InlineData("sessionId")]
    public void Masks_credential_names_an_oauth_redirect_can_carry(string name)
    {
        Assert.Equal($"https://x/cb?{name}=***", LogRedact.Url($"https://x/cb?{name}=SECRET"));
    }

    [Fact]
    public void Masks_an_implicit_flow_token_in_the_fragment()
    {
        Assert.Equal(
            "https://x/cb#access_token=***&expires_in=3600",
            LogRedact.Url("https://x/cb#access_token=SECRET&expires_in=3600"));
    }

    [Fact]
    public void Masks_a_double_encoded_separator()
    {
        Assert.Equal("https://x/y?token%3D***", LogRedact.Url("https://x/y?token%3DSECRET"));
    }

    [Fact]
    public void Leaves_a_non_sensitive_parameter_alone()
    {
        Assert.Equal("https://x/y?monitor=1&expires_in=3600", LogRedact.Url("https://x/y?monitor=1&expires_in=3600"));
    }

    [Fact]
    public void Leaves_a_url_without_a_query_unchanged()
    {
        Assert.Equal("http://localhost:9400/panel", LogRedact.Url("http://localhost:9400/panel"));
    }

    [Fact]
    public void Masks_a_token_carried_inside_a_fragment()
    {
        Assert.Equal(
            "http://localhost:9400/#/system?token=***",
            LogRedact.Url("http://localhost:9400/#/system?token=live"));
    }

    [Fact]
    public void Preserves_a_fragment_that_follows_the_query()
    {
        Assert.Equal(
            "http://localhost:9400/panel?token=***#/lighting",
            LogRedact.Url("http://localhost:9400/panel?token=secret#/lighting"));
    }

    [Fact]
    public void Handles_a_valueless_and_an_empty_parameter()
    {
        Assert.Equal("http://x/?token&a=1", LogRedact.Url("http://x/?token&a=1"));
        Assert.Equal("http://x/?token=***", LogRedact.Url("http://x/?token="));
    }

    [Fact]
    public void Passes_null_and_empty_through_as_empty()
    {
        Assert.Equal("", LogRedact.Url(null));
        Assert.Equal("", LogRedact.Url(""));
    }
}
