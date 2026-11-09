using Kalendar.WebApi.Auth;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Kalendar.WebApi.Tests;

public class AuthValidationTests
{
    [Theory]
    [InlineData("user-name", "12345678", true)]
    [InlineData(" user-name ", "12345678", true)]
    [InlineData("user name", "12345678", false)]
    [InlineData("", "12345678", false)]
    [InlineData("user-name", "short", false)]
    public void CredentialsAreValidated(string username, string password, bool valid)
    {
        Assert.Equal(valid, AuthEndpoints.Validate(new(username, password)).Count == 0);
    }

    [Theory]
    [InlineData("short", "issuer", "audience", "30", false)]
    [InlineData("01234567890123456789012345678901", "", "audience", "30", false)]
    [InlineData("01234567890123456789012345678901", "issuer", "", "30", false)]
    [InlineData("01234567890123456789012345678901", "issuer", "audience", "0", false)]
    [InlineData("01234567890123456789012345678901", "issuer", "audience", "broken", false)]
    [InlineData("01234567890123456789012345678901", "issuer", "audience", "30", true)]
    public void JwtConfigurationFailsClosed(string key, string issuer, string audience, string minutes, bool valid)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = key,
            ["Jwt:Issuer"] = issuer,
            ["Jwt:Audience"] = audience,
            ["Jwt:AccessTokenLifetimeMinutes"] = minutes
        }).Build();
        if (valid)
        {
            var settings = JwtSettings.Read(configuration);
            Assert.Equal(30, settings.LifetimeMinutes);
            Assert.True(settings.ValidationParameters().ValidateLifetime);
        }
        else
            Assert.Throws<InvalidOperationException>(() => JwtSettings.Read(configuration));
    }
}
