using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace Kalendar.NotificationService.Tests;

public class NotificationServiceTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/ready")]
    public async Task ServiceEndpointReturnsHealthyJson(string path)
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<StatusResponse>();
        Assert.Equal("Healthy", body?.Status);
    }

    [Fact]
    public async Task UnknownRouteReturnsNotFound()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SignalRClientConnectsAndDisconnects()
    {
        var timeout = TimeSpan.FromSeconds(10);
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/notifications"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();

        await connection.StartAsync().WaitAsync(timeout);
        Assert.Equal(HubConnectionState.Connected, connection.State);

        await connection.StopAsync().WaitAsync(timeout);
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    record StatusResponse(string Status);
}
