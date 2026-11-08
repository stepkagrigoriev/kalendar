extern alias web;
extern alias worker;
extern alias notification;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;
using Xunit;

namespace Kalendar.EndToEnd.Tests;

public sealed class MvpTests : IAsyncLifetime
{
    readonly MongoDbContainer mongo = new MongoDbBuilder("mongo:8.0.32").WithUsername("root").WithPassword("root-test-password").Build();
    readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4.1.4-management").WithUsername("tests").WithPassword("rabbit-test-password").Build();
    readonly FakeTimeProvider clock = new(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    WebApplicationFactory<web::Program> api = null!;
    WebApplicationFactory<worker::Program> scheduler = null!;
    WebApplicationFactory<notification::Program> notifications = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(mongo.StartAsync(), rabbit.StartAsync());
        var variables = new Dictionary<string, string>
        {
            ["MONGO_HOST"] = $"{mongo.Hostname}:{mongo.GetMappedPublicPort(27017)}",
            ["MONGO_ROOT_USERNAME"] = "root",
            ["MONGO_ROOT_PASSWORD"] = "root-test-password",
            ["KALENDAR_WEBAPI_MONGO_USER"] = "webapi",
            ["KALENDAR_WEBAPI_MONGO_PASSWORD"] = "webapi-test-password",
            ["KALENDAR_WORKER_MONGO_USER"] = "worker",
            ["KALENDAR_WORKER_MONGO_PASSWORD"] = "worker-test-password"
        };
        await DatabaseSetup.RunAsync(readVariable: key => variables.GetValueOrDefault(key));
        api = new WebApplicationFactory<web::Program>().WithWebHostBuilder(builder =>
        {
            Configure(builder, "webapi");
            builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        });
        api.CreateClient();
        notifications = new WebApplicationFactory<notification::Program>().WithWebHostBuilder(builder => Configure(builder, null));
        notifications.CreateClient();
        scheduler = new WebApplicationFactory<worker::Program>().WithWebHostBuilder(builder =>
        {
            Configure(builder, "worker");
            builder.UseSetting("WebApi:BaseUrl", api.Server.BaseAddress.ToString()).UseSetting("Scheduler:PollIntervalSeconds", "1");
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
                services.AddHttpClient<worker::Kalendar.Worker.Scheduling.InternalApiClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => api.Server.CreateHandler());
            });
        });
        scheduler.CreateClient();
    }

    void Configure(IWebHostBuilder builder, string? databaseUser)
    {
        builder.UseSetting("RabbitMq:Host", rabbit.Hostname).UseSetting("RabbitMq:Port", rabbit.GetMappedPublicPort(5672).ToString())
            .UseSetting("RabbitMq:Username", "tests").UseSetting("RabbitMq:Password", "rabbit-test-password")
            .UseSetting("Jwt:SigningKey", "test-signing-key-with-at-least-32-bytes")
            .UseSetting("Jwt:Issuer", "mvp-tests").UseSetting("Jwt:Audience", "mvp-tests")
            .UseSetting("Jwt:AccessTokenLifetimeMinutes", "60").UseSetting("InternalApi:Key", "test-internal-key-with-at-least-32-bytes");
        if (databaseUser is not null)
            builder.UseSetting("Mongo:Database", "kalendar").UseSetting("Mongo:ConnectionString",
                $"mongodb://{databaseUser}:{databaseUser}-test-password@{mongo.Hostname}:{mongo.GetMappedPublicPort(27017)}/kalendar?authSource=kalendar");
    }

    public async Task DisposeAsync()
    {
        if (scheduler is not null) await scheduler.DisposeAsync();
        if (notifications is not null) await notifications.DisposeAsync();
        if (api is not null) await api.DisposeAsync();
        await rabbit.DisposeAsync();
        await mongo.DisposeAsync();
    }

    [Fact]
    public async Task RegistrationTaskReminderRecurrenceAndSignalRWorkAcrossAllServices()
    {
        var client = api.CreateClient();
        var credentials = new { email = "mvp@example.com", password = "mvp-test-password" };
        var registered = await client.PostAsJsonAsync("/api/auth/register", credentials);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var login = await (await client.PostAsJsonAsync("/api/auth/login", credentials)).Content.ReadFromJsonAsync<JsonElement>();
        var token = login.GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new HubConnectionBuilder().WithUrl(new Uri(notifications.Server.BaseAddress, "/hubs/notifications"), options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.AccessTokenProvider = () => Task.FromResult(token)!;
            options.HttpMessageHandlerFactory = _ => notifications.Server.CreateHandler();
        }).Build();
        connection.On<JsonElement>("notification", payload => received.TrySetResult(payload));
        await connection.StartAsync();
        var list = await (await client.PostAsJsonAsync("/api/lists", new { name = "Учёба" })).Content.ReadFromJsonAsync<JsonElement>();
        var due = clock.GetUtcNow().AddMinutes(1);
        var response = await client.PostAsJsonAsync("/api/tasks", new
        {
            title = "Подготовить отчёт",
            priority = "high",
            listId = list.GetProperty("id").GetString(),
            dueAt = due,
            reminder = new { at = clock.GetUtcNow().AddSeconds(10) },
            recurrence = new { frequency = "daily", interval = 1 }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var task = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = task.GetProperty("id").GetString()!;
        var database = new MongoClient(mongo.GetConnectionString()).GetDatabase("kalendar");
        var jobs = database.GetCollection<BsonDocument>("backgroundJobs");
        await Until(async () => await jobs.CountDocumentsAsync(new BsonDocument("taskId", ObjectId.Parse(id))) == 2);
        clock.Advance(TimeSpan.FromSeconds(11));
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(id, message.GetProperty("taskId").GetString());
        await Until(async () => (await database.GetCollection<BsonDocument>("tasks").Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync())["reminder"]["sentAt"].IsBsonDateTime);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Until(async () => await database.GetCollection<BsonDocument>("tasks").CountDocumentsAsync(new BsonDocument("recurrenceKey", $"recurrence:{id}:{due.UtcDateTime:O}")) == 1);
        foreach (var endpoint in new[] { "/health", "/ready", "/metrics" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(endpoint)).StatusCode);
    }

    static async Task Until(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await condition()) await Task.Delay(50, timeout.Token);
    }
}
