using System.Net;
using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;
using Xunit;

namespace Kalendar.WebApi.Tests;

public sealed class MongoIntegrationTests : IAsyncLifetime
{
    const string RootUsername = "root";
    const string RootPassword = "root-test-password";
    const string WebApiUsername = "webapi";
    const string WebApiPassword = "webapi-test-password";
    const string WorkerUsername = "worker";
    const string WorkerPassword = "worker-test-password";
    const string DatabaseName = "kalendar";
    readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4.1.4-management")
        .WithUsername("kalendar-tests").WithPassword("rabbit-test-password").Build();

    readonly MongoDbContainer container = new MongoDbBuilder("mongo:8.0.32")
        .WithUsername(RootUsername)
        .WithPassword(RootPassword)
        .WithPortBinding(AvailablePort(), 27017)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(27017))
        .WithEnvironment("KALENDAR_WEBAPI_MONGO_USER", WebApiUsername)
        .WithEnvironment("KALENDAR_WEBAPI_MONGO_PASSWORD", WebApiPassword)
        .WithEnvironment("KALENDAR_WORKER_MONGO_USER", WorkerUsername)
        .WithEnvironment("KALENDAR_WORKER_MONGO_PASSWORD", WorkerPassword)
        .Build();

    static int AvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await rabbit.StartAsync();
        var variables = new Dictionary<string, string>
        {
            ["MONGO_HOST"] = $"{container.Hostname}:{container.GetMappedPublicPort(27017)}",
            ["MONGO_ROOT_USERNAME"] = RootUsername,
            ["MONGO_ROOT_PASSWORD"] = RootPassword,
            ["KALENDAR_WEBAPI_MONGO_USER"] = WebApiUsername,
            ["KALENDAR_WEBAPI_MONGO_PASSWORD"] = WebApiPassword,
            ["KALENDAR_WORKER_MONGO_USER"] = WorkerUsername,
            ["KALENDAR_WORKER_MONGO_PASSWORD"] = WorkerPassword
        };
        await DatabaseSetup.RunAsync(readVariable: name => variables.GetValueOrDefault(name));
        await DatabaseSetup.RunAsync(readVariable: name => variables.GetValueOrDefault(name));
    }

    public async Task DisposeAsync()
    {
        await rabbit.DisposeAsync();
        await container.DisposeAsync();
    }

    [Fact]
    public async Task InternalCommandsRequireServiceKeyAndAreIdempotent()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var credentials = new { email = "internal@example.com", password = "test-password-123" };
        await client.PostAsJsonAsync("/api/auth/register", credentials);
        var token = (await (await client.PostAsJsonAsync("/api/auth/login", credentials)).Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
        var creation = await client.PostAsJsonAsync("/api/tasks", new
        {
            title = "Повторение",
            dueAt = "2026-11-08T10:00:00Z",
            reminder = new { at = "2026-11-08T09:00:00Z" },
            recurrence = new { frequency = "daily", interval = 1 }
        });
        var task = await creation.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var id = task.GetProperty("id").GetString()!;
        var owner = task.GetProperty("userId").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/internal/tasks/{id}?userId={owner}")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", "test-internal-key-with-at-least-32-bytes");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/internal/tasks/{id}?userId={owner}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/internal/tasks/{id}?userId={ObjectId.GenerateNewId()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/internal/tasks/scheduled?limit=1")).StatusCode);
        var confirmation = new { userId = owner, reminderAt = "2026-11-08T09:00:00Z" };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/internal/tasks/{id}/reminder-sent", confirmation)).StatusCode);
        var documents = Database().GetCollection<BsonDocument>("tasks");
        var first = await documents.Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/internal/tasks/{id}/reminder-sent", confirmation)).StatusCode);
        var repeated = await documents.Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync();
        Assert.Equal(first["reminder"]["sentAt"], repeated["reminder"]["sentAt"]);
        var instance = new { userId = owner, dueAt = "2026-11-09T10:00:00Z", idempotencyKey = $"recurrence:{id}:2026-11-08T10:00:00.0000000Z" };
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.PostAsJsonAsync($"/internal/tasks/{id}/recurrence-instances", instance)));
        var ids = new List<string>();
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ids.Add((await response.Content.ReadFromJsonAsync<IdResponse>())!.Id);
        }
        Assert.Single(ids.Distinct());
        Assert.Equal(2, await documents.CountDocumentsAsync(new BsonDocument("userId", ObjectId.Parse(owner))));
    }

    [Fact]
    public async Task TasksListsFiltersAndOwnershipWorkTogether()
    {
        await using var factory = CreateFactory();
        async Task<HttpClient> User(string email)
        {
            var client = factory.CreateClient();
            var credentials = new { email, password = "test-password-123" };
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", credentials)).StatusCode);
            var token = (await (await client.PostAsJsonAsync("/api/auth/login", credentials)).Content.ReadFromJsonAsync<TokenResponse>())!;
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
            return client;
        }
        var first = await User("first@example.com");
        var second = await User("second@example.com");
        var list = await first.PostAsJsonAsync("/api/lists", new { name = " Учёба ", color = "#AAbb12" });
        Assert.Equal(HttpStatusCode.Created, list.StatusCode);
        var listId = (await list.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        Assert.Equal(HttpStatusCode.Conflict, (await first.PostAsJsonAsync("/api/lists", new { name = "Учёба" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await second.PostAsJsonAsync("/api/lists", new { name = "Учёба" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.PatchAsJsonAsync($"/api/lists/{listId}", new { color = "#FFFFFF" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/lists")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await second.PostAsJsonAsync("/api/tasks", new { title = "Чужой список", listId })).StatusCode);
        var creation = await first.PostAsJsonAsync("/api/tasks", new
        {
            title = " Работа .*+?()[] ",
            description = "Описание",
            listId,
            priority = "high",
            tags = new[] { " Учёба ", "учёба" },
            dueAt = "2026-11-08T10:00:00Z",
            reminder = new { at = "2026-11-08T09:00:00Z" }
        });
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        var taskId = (await creation.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync($"/api/tasks/{taskId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await second.PatchAsJsonAsync($"/api/tasks/{taskId}", new { title = "Чужая" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await second.DeleteAsync($"/api/tasks/{taskId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.PatchAsJsonAsync($"/api/tasks/{taskId}", new { description = "Новое" })).StatusCode);
        foreach (var query in new[] { "sort=priority", "status=open&priority=high", $"listId={listId}", "tag=учёба", "q=.*%2B%3F()%5B%5D", "from=2026-11-08T00:00:00Z&to=2026-11-09T00:00:00Z" })
        {
            var response = await first.GetAsync($"/api/tasks?{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal(1, page.GetProperty("total").GetInt32());
        }
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsync($"/api/tasks/{taskId}/complete", null)).StatusCode);
        var completed = await first.PostAsync($"/api/tasks/{taskId}/complete", null);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsync($"/api/tasks/{taskId}/reopen", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await first.DeleteAsync($"/api/lists/{listId}")).StatusCode);
        var task = await first.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/tasks/{taskId}");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, task.GetProperty("listId").ValueKind);
        Assert.Equal(HttpStatusCode.NoContent, (await first.DeleteAsync($"/api/tasks/{taskId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await first.GetAsync($"/api/tasks/{taskId}")).StatusCode);
    }

    [Fact]
    public async Task RegistrationLoginAndTokenProtectUserData()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users/me")).StatusCode);
        var credentials = new { email = " User@example.com ", password = "test-password-123" };
        var registration = await client.PostAsJsonAsync("/api/auth/register", credentials);
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        var id = (await registration.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        var user = await Database().GetCollection<BsonDocument>("users").Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync();
        Assert.Equal("USER@EXAMPLE.COM", user["normalizedEmail"].AsString);
        Assert.NotEqual(credentials.password, user["passwordHash"].AsString);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/register", credentials)).StatusCode);
        var invalid = await client.PostAsJsonAsync("/api/auth/register", new { email = "broken", password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType!.MediaType);

        var wrongPassword = await client.PostAsJsonAsync("/api/auth/login", new { email = "User@example.com", password = "wrong" });
        var unknown = await client.PostAsJsonAsync("/api/auth/login", new { email = "missing@example.com", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", credentials);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
        var me = await client.GetFromJsonAsync<IdResponse>("/api/users/me");
        Assert.Equal(id, me!.Id);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users/me")).StatusCode);
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<Kalendar.WebApi.Auth.UserDocument>(
            Microsoft.Extensions.Options.Options.Create(new Microsoft.AspNetCore.Identity.PasswordHasherOptions { IterationCount = 10 }));
        var oldHash = hasher.HashPassword(new Kalendar.WebApi.Auth.UserDocument(), credentials.password);
        await Database().GetCollection<BsonDocument>("users").UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(id)),
            new BsonDocument("$set", new BsonDocument("passwordHash", oldHash)));
        var rehashed = await client.PostAsJsonAsync("/api/auth/login", credentials);
        Assert.Equal(HttpStatusCode.OK, rehashed.StatusCode);
        var updated = await Database().GetCollection<BsonDocument>("users").Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync();
        Assert.NotEqual(oldHash, updated["passwordHash"].AsString);
        await Database().GetCollection<BsonDocument>("users").UpdateOneAsync(new BsonDocument("_id", ObjectId.Parse(id)),
            new BsonDocument("$set", new BsonDocument("isActive", false)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", credentials)).StatusCode);
    }

    [Fact]
    public async Task AuthRateLimitDoesNotLimitHealth()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync("/api/auth/login", new { email = "unknown@example.com", password = "wrong" })).StatusCode);
        var limited = await client.PostAsJsonAsync("/api/auth/login", new { email = "unknown@example.com", password = "wrong" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        await using var freshFactory = CreateFactory();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await freshFactory.CreateClient().PostAsync("/api/auth/register", new StringContent("{", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task StartupCreatesCollectionsIndexesAndHealthyEndpoints()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        var ready = await client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", (await ready.Content.ReadFromJsonAsync<StatusResponse>())?.Status);
        var metrics = await client.GetStringAsync("/metrics");
        Assert.Contains("http_requests_received_total", metrics);
        Assert.Contains("tasks_pending_total", metrics);
        Assert.Contains("rabbitmq_messages_total", metrics);

        var database = Database();
        var collections = await (await database.ListCollectionNamesAsync()).ToListAsync();
        Assert.Equal(["taskLists", "tasks", "users"], collections.Order());
        await AssertIndexes(database.GetCollection<BsonDocument>("users"), "_id_", "normalizedEmail_unique");
        await AssertIndexes(database.GetCollection<BsonDocument>("taskLists"), "_id_", "userId_name_unique");
        await AssertIndexes(
            database.GetCollection<BsonDocument>("tasks"),
            "_id_", "recurrenceKey_unique", "userId_dueAt", "userId_listId_dueAt", "userId_status_dueAt", "userId_tags");

        var recurrenceIndex = (await (await database.GetCollection<BsonDocument>("tasks").Indexes.ListAsync()).ToListAsync())
            .Single(document => document["name"] == "recurrenceKey_unique");
        Assert.True(recurrenceIndex["unique"].AsBoolean);
        Assert.Equal("string", recurrenceIndex["partialFilterExpression"]["recurrenceKey"]["$type"].AsString);

        await Assert.ThrowsAnyAsync<MongoException>(() =>
            database.GetCollection<BsonDocument>("backgroundJobs").InsertOneAsync(new BsonDocument("test", true)));
        await Assert.ThrowsAnyAsync<MongoException>(() =>
            database.GetCollection<BsonDocument>("backgroundJobs").Find(FilterDefinition<BsonDocument>.Empty).FirstOrDefaultAsync());
        var port = container.GetMappedPublicPort(27017);
        await container.StopAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        using var recovered = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await container.StartAsync(recovered.Token);
        Assert.Equal(port, container.GetMappedPublicPort(27017));
        while ((await client.GetAsync("/ready", recovered.Token)).StatusCode != HttpStatusCode.OK)
            await Task.Delay(100, recovered.Token);
    }

    [Fact]
    public async Task InitializationIsIdempotentAndUniqueIndexesAreEnforced()
    {
        await using (var firstFactory = CreateFactory())
            firstFactory.CreateClient();

        var database = Database();
        var users = database.GetCollection<BsonDocument>("users");
        await users.InsertOneAsync(new BsonDocument("normalizedEmail", "USER@EXAMPLE.COM"));
        await Assert.ThrowsAsync<MongoWriteException>(() =>
            users.InsertOneAsync(new BsonDocument("normalizedEmail", "USER@EXAMPLE.COM")));

        var tasks = database.GetCollection<BsonDocument>("tasks");
        await tasks.InsertManyAsync([new BsonDocument("title", "one"), new BsonDocument("title", "two")]);

        await using (var secondFactory = CreateFactory())
            secondFactory.CreateClient();

        Assert.Equal(2, await tasks.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        await AssertIndexes(tasks, "_id_", "recurrenceKey_unique", "userId_dueAt", "userId_listId_dueAt", "userId_status_dueAt", "userId_tags");
    }

    WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Mongo:ConnectionString", ApplicationConnectionString())
            .UseSetting("RabbitMq:Host", rabbit.Hostname)
            .UseSetting("RabbitMq:Port", rabbit.GetMappedPublicPort(5672).ToString())
            .UseSetting("RabbitMq:Username", "kalendar-tests")
            .UseSetting("RabbitMq:Password", "rabbit-test-password")
            .UseSetting("Mongo:Database", DatabaseName)
            .UseSetting("InternalApi:Key", "test-internal-key-with-at-least-32-bytes")
            .UseSetting("Jwt:SigningKey", "test-signing-key-with-at-least-32-bytes")
            .UseSetting("Jwt:Issuer", "kalendar-tests")
            .UseSetting("Jwt:Audience", "kalendar-tests")
            .UseSetting("Jwt:AccessTokenLifetimeMinutes", "30"));

    IMongoDatabase Database() => new MongoClient(ApplicationConnectionString()).GetDatabase(DatabaseName);

    string ApplicationConnectionString() =>
        $"mongodb://{WebApiUsername}:{WebApiPassword}@{container.Hostname}:{container.GetMappedPublicPort(27017)}/{DatabaseName}?authSource={DatabaseName}";

    static async Task AssertIndexes(IMongoCollection<BsonDocument> collection, params string[] expected)
    {
        var indexes = await (await collection.Indexes.ListAsync()).ToListAsync();
        Assert.Equal(expected.Order(), indexes.Select(document => document["name"].AsString).Order());
    }

    sealed record StatusResponse(string Status);
    sealed record IdResponse(string Id);
    sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt);
}
