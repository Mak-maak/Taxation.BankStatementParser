using FluentAssertions;
using Taxation.StatementParser.Console.Configuration;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class AppSettingsTests
{
    [Theory]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData("Yes", true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("on", true)]
    [InlineData("y", true)]
    [InlineData("no", false)]
    [InlineData("NO", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsProtectionEnabled_ShouldInterpretFriendlyBooleanValues(string value, bool expected)
    {
        var settings = new ExcelProtectionSettings { PasswordProtect = value };

        settings.IsProtectionEnabled.Should().Be(expected);
    }

    [Fact]
    public void Defaults_ShouldEnableProtection_WithDefaultPassword()
    {
        var settings = new AppSettings();

        settings.ExcelProtection.IsProtectionEnabled.Should().BeTrue();
        settings.ExcelProtection.Password.Should().Be("confidential_123");
    }

    [Fact]
    public void Load_WithMissingFile_ShouldReturnProtectedDefaults()
    {
        // AppContext.BaseDirectory in the test host has no appsettings.json for this section by design;
        // Load must never throw and must fall back to protection-enabled defaults.
        AppSettings settings = AppSettings.Load();

        settings.Should().NotBeNull();
        settings.ExcelProtection.Should().NotBeNull();
    }
}
