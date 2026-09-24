using FluentAssertions;
using Taxation.StatementParser.Console.Configuration;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class AiSettingsTests
{
    [Fact]
    public void Defaults_ShouldBeDisabledFallbackLoopback()
    {
        var settings = new AiSettings();

        settings.IsEnabled.Should().BeFalse("AI must be opt-in so existing behaviour is unchanged");
        settings.IsAlwaysMode.Should().BeFalse();
        settings.IsLoopbackEndpoint().Should().BeTrue("the default endpoint must be local-only");
        settings.Endpoint.Should().Be("http://localhost:11434");
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("on", true)]
    [InlineData("y", true)]
    [InlineData("YES", true)]
    [InlineData("  true  ", true)]
    [InlineData("no", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("", false)]
    [InlineData("maybe", false)]
    public void IsEnabled_ShouldParseFriendlyBooleans(string value, bool expected)
    {
        new AiSettings { Enabled = value }.IsEnabled.Should().Be(expected);
    }

    [Theory]
    [InlineData("always", true)]
    [InlineData("ALWAYS", true)]
    [InlineData("  always  ", true)]
    [InlineData("fallback", false)]
    [InlineData("", false)]
    [InlineData("other", false)]
    public void IsAlwaysMode_ShouldOnlyBeTrueForAlways(string mode, bool expected)
    {
        new AiSettings { Mode = mode }.IsAlwaysMode.Should().Be(expected);
    }

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("http://[::1]:11434", true)]
    [InlineData("https://localhost", true)]
    [InlineData("http://192.168.1.10:11434", false)]
    [InlineData("http://example.com", false)]
    [InlineData("http://ollama.internal:11434", false)]
    [InlineData("not-a-url", false)]
    [InlineData("", false)]
    public void IsLoopbackEndpoint_ShouldOnlyAllowLoopbackHosts(string endpoint, bool expected)
    {
        new AiSettings { Endpoint = endpoint }.IsLoopbackEndpoint().Should().Be(expected);
    }
}
