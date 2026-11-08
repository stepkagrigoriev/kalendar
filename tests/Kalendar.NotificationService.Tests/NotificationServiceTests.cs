using System.Net;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.RabbitMq;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Kalendar.Contracts;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace Kalendar.NotificationService.Tests;

public class NotificationServiceTests : IAsyncLifetime
{
    readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4.1.4-management")
        .WithUsername("kalendar-tests").WithPassword("rabbit-test-password").WithPortBinding(15672, true).Build();
    WebApplicationFactory<Program> factory;
    const string SigningKey = "test-signing-key-with-at-least-32-bytes";

    public async Task InitializeAsync()
    {
        await rabbit.StartAsync();
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("RabbitMq:Host", rabbit.Hostname)
            .UseSetting("RabbitMq:Port", rabbit.GetMappedPublicPort(5672).ToString())
            .UseSetting("RabbitMq:Username", "kalendar-tests")
            .UseSetting("RabbitMq:Password", "rabbit-test-password")
            .UseSetting("Jwt:SigningKey", SigningKey)
            .UseSetting("Jwt:Issuer", "kalendar-tests")
            .UseSetting("Jwt:Audience", "kalendar-tests"));
    }

    public async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        await rabbit.DisposeAsync();
    }

    static string Token(string userId)
    {
        var token = new JwtSecurityToken("kalendar-tests", "kalendar-tests", [new Claim("sub", userId)],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(30),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
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
        var metrics = await client.GetStringAsync("/metrics");
        Assert.Contains("http_requests_received_total", metrics);
        Assert.Contains("signalr_delivery_errors_total", metrics);
    }

    [Fact]
    public async Task UnknownRouteReturnsNotFound()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task InvalidAndPoisonMessagesReachErrorQueueAndUnknownMessagesAreSkipped()
    {
        factory.CreateClient();
        using var management = new HttpClient { BaseAddress = new Uri($"http://{rabbit.Hostname}:{rabbit.GetMappedPublicPort(15672)}") };
        management.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("kalendar-tests:rabbit-test-password")));
        async Task<long> Messages(string queue)
        {
            using var response = await management.GetAsync($"/api/queues/%2F/{queue}");
            if (!response.IsSuccessStatusCode) return 0;
            var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            return json.TryGetProperty("messages", out var count) ? count.GetInt64() : 0;
        }
        async Task WaitForQueue(string queue, long count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (await Messages(queue) < count) await Task.Delay(100, timeout.Token);
        }
        await factory.Services.GetRequiredService<IBus>().Publish(new NotificationRequested("invalid", "broken", "broken", "", "", DateTimeOffset.UtcNow));
        await WaitForQueue("notification-service-notification-requested_error", 1);
        async Task PublishRaw(string payload)
        {
            using var response = await management.PostAsJsonAsync("/api/exchanges/%2F/notification-service-notification-requested/publish", new
            {
                properties = new { content_type = "application/vnd.masstransit+json" },
                routing_key = "",
                payload,
                payload_encoding = "string"
            });
            response.EnsureSuccessStatusCode();
        }
        await PublishRaw("{invalid json");
        await WaitForQueue("notification-service-notification-requested_error", 2);
        await PublishRaw("{\"messageType\":[\"urn:message:Unknown:Event\"],\"message\":{}}");
        await WaitForQueue("notification-service-notification-requested_skipped", 1);
    }

    [Fact]
    public async Task NotificationsReachOnlyTheAuthenticatedRecipient()
    {
        var firstId = "0123456789abcdef01234567";
        var secondId = "1123456789abcdef01234567";
        factory.CreateClient();
        HubConnection Connection(string id) => new HubConnectionBuilder().WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/notifications"), options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.AccessTokenProvider = () => Task.FromResult(Token(id));
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
        }).Build();
        await using var first = Connection(firstId);
        await using var second = Connection(secondId);
        var received = new TaskCompletionSource<System.Text.Json.JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.On<System.Text.Json.JsonElement>("notification", payload => received.TrySetResult(payload));
        second.On<System.Text.Json.JsonElement>("notification", _ => leaked.TrySetResult());
        await first.StartAsync();
        await second.StartAsync();
        await factory.Services.GetRequiredService<IBus>().Publish(new NotificationRequested("notification-1", firstId,
            "2123456789abcdef01234567", "Напоминание", "Тест", DateTimeOffset.UtcNow));
        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("notification-1", payload.GetProperty("notificationId").GetString());
        await Task.Delay(250);
        Assert.False(leaked.Task.IsCompleted);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync("/hubs/notifications/negotiate?negotiateVersion=1", null)).StatusCode);
    }

    [Fact]
    public async Task SignalRClientConnectsAndDisconnects()
    {
        var timeout = TimeSpan.FromSeconds(10);
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/notifications"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult(Token("0123456789abcdef01234567"));
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
