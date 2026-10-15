using NKeeper.Api.Configuration;

namespace NKeeper.Api.Tests;

/// <summary>Pure unit tests; these run without PostgreSQL.</summary>
public sealed class ConfigurationValidationTests
{
    [Theory]
    [InlineData("http://localhost:5173", true)]
    [InlineData("https://nkeeper.example.com", true)]
    [InlineData("http://localhost:5173/", false)]
    [InlineData("https://nkeeper.example.com/app", false)]
    [InlineData("*", false)]
    [InlineData("ftp://example.com", false)]
    public void CorsOrigins_MustBeExactHttpOrigins(string origin, bool expectedValid)
    {
        var settings = new CorsSettings { AllowedOrigins = [origin] };

        Assert.Equal(expectedValid, settings.IsValid());
    }

    [Theory]
    [InlineData("172.16.0.0/12", true)]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("not-a-network", false)]
    public void ForwardedHeaderNetworks_MustBeCidr(string network, bool expectedValid)
    {
        var settings = new ForwardedHeadersSettings { KnownNetworks = [network] };

        Assert.Equal(expectedValid, settings.IsValid());
    }
}
