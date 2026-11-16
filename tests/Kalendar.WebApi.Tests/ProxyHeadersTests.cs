using Microsoft.Extensions.Configuration;
using Xunit;

namespace Kalendar.WebApi.Tests;

public class ProxyHeadersTests
{
    [Theory]
    [InlineData("not-a-network")]
    [InlineData("10.112.0.0/16,10.0.0.0/99")]
    public void InvalidNetworkFailsAtStartup(string networks)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ProxyHeaders.Read(Configuration(networks)));
        Assert.Equal("Некорректный CIDR в ForwardedHeaders:KnownNetworks.", exception.Message);
    }

    [Fact]
    public void EmptyNetworksDisableForwardedHeaders()
    {
        Assert.Null(ProxyHeaders.Read(Configuration(null)));
    }

    [Fact]
    public void OnlyConfiguredNetworksAreTrusted()
    {
        var options = ProxyHeaders.Read(Configuration("10.112.0.0/16, 10.96.0.0/16"));
        Assert.Equal(1, options.ForwardLimit);
        Assert.Empty(options.KnownProxies);
        Assert.Equal(["10.112.0.0/16", "10.96.0.0/16"], options.KnownIPNetworks.Select(network => network.ToString()));
    }

    static IConfiguration Configuration(string? networks) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ForwardedHeaders:KnownNetworks"] = networks }).Build();
}
