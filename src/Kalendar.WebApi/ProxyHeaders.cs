using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace Kalendar.WebApi;

static class ProxyHeaders
{
    public static ForwardedHeadersOptions Read(IConfiguration configuration)
    {
        var value = configuration["ForwardedHeaders:KnownNetworks"];
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var network in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            options.KnownIPNetworks.Add(IPNetwork.TryParse(network, out var parsed)
                ? parsed : throw new InvalidOperationException("Некорректный CIDR в ForwardedHeaders:KnownNetworks."));
        return options;
    }
}
