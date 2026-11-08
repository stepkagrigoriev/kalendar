using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;
using Xunit;
using Kalendar.Worker.Scheduling;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using MassTransit;

namespace Kalendar.Worker.Tests;

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
        .WithEnvironment("KALENDAR_WEBAPI_MONGO_USER", WebApiUsername)
        .WithEnvironment("KALENDAR_WEBAPI_MONGO_PASSWORD", WebApiPassword)
        .WithEnvironment("KALENDAR_WORKER_MONGO_USER", WorkerUsername)
        .WithEnvironment("KALENDAR_WORKER_MONGO_PASSWORD", WorkerPassword)
        .Build();

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
    }

    public async Task DisposeAsync()
    {
        await rabbit.DisposeAsync();
        await container.DisposeAsync();
    }

    [Fact]
    public async Task ExecutionRetriesFailuresCancelsStaleJobsAndCompletesReminders()
    {
        await using var factory = CreateFactory();
        factory.CreateClient();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new JobStore(Database(), clock, NullLogger<JobStore>.Instance);
        ScheduledTask? current = null;
        var reject = false;
        var conflict = false;
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (reject) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (request.RequestUri!.AbsolutePath == "/internal/tasks/scheduled")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(current is null ? Array.Empty<ScheduledTask>() : new[] { current }) };
            if (request.Method == HttpMethod.Get)
                return current is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(current) };
            return new HttpResponseMessage(conflict ? HttpStatusCode.Conflict : HttpStatusCode.OK);
        }))
        { BaseAddress = new Uri("http://internal.test") };
        var scheduler = new Scheduler(store, new InternalApiClient(http), factory.Services.GetRequiredService<IBus>(), clock,
            new SchedulerSettings(1, 60, 2, 10), NullLogger<Scheduler>.Instance);
        var documents = Database().GetCollection<BackgroundJob>("backgroundJobs");
        ScheduledTask NewReminder() => new(ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(), "Напоминание", "", null,
            "open", new TaskReminder(RecurrenceDates.Milliseconds(clock.GetUtcNow().UtcDateTime), null), null!);

        current = NewReminder();
        await store.PlanAsync(current, CancellationToken.None);
        var job = await store.ClaimAsync(60, CancellationToken.None);
        await scheduler.ExecuteJobAsync(job, CancellationToken.None);
        Assert.Equal("completed", (await documents.Find(x => x.Id == job.Id).SingleAsync()).Status);

        current = NewReminder();
        await store.PlanAsync(current, CancellationToken.None);
        job = await store.ClaimAsync(60, CancellationToken.None);
        current = null;
        await scheduler.ExecuteJobAsync(job, CancellationToken.None);
        Assert.Equal("cancelled", (await documents.Find(x => x.Id == job.Id).SingleAsync()).Status);

        current = NewReminder();
        await store.PlanAsync(current, CancellationToken.None);
        job = await store.ClaimAsync(60, CancellationToken.None);
        reject = true;
        await scheduler.ExecuteJobAsync(job, CancellationToken.None);
        var pending = await documents.Find(x => x.Id == job.Id).SingleAsync();
        Assert.Equal("pending", pending.Status);
        Assert.Equal(1, pending.Attempts);
        Assert.Equal(job.TargetAt, pending.TargetAt);
        clock.Advance(TimeSpan.FromSeconds(11));
        job = await store.ClaimAsync(60, CancellationToken.None);
        await scheduler.ExecuteJobAsync(job, CancellationToken.None);
        Assert.Equal("failed", (await documents.Find(x => x.Id == job.Id).SingleAsync()).Status);
        reject = false;

        current = NewReminder() with { Reminder = null!, DueAt = RecurrenceDates.Milliseconds(clock.GetUtcNow().UtcDateTime), Recurrence = new TaskRecurrence("daily", 1, null!, null) };
        await store.PlanAsync(current, CancellationToken.None);
        job = await store.ClaimAsync(60, CancellationToken.None);
        conflict = true;
        await scheduler.ExecuteJobAsync(job, CancellationToken.None);
        Assert.Equal("cancelled", (await documents.Find(x => x.Id == job.Id).SingleAsync()).Status);
        conflict = false;

        current = NewReminder();
        await scheduler.ReconcileAsync(CancellationToken.None);
        Assert.Single(await store.PendingAsync(CancellationToken.None));
        current = null;
        await scheduler.ReconcileAsync(CancellationToken.None);
        Assert.Empty(await store.PendingAsync(CancellationToken.None));
    }

    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    [Fact]
    public async Task JobPlanningClaimsCancellationAndLeaseOwnershipAreAtomic()
    {
        await using var factory = CreateFactory();
        factory.CreateClient();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var store = new JobStore(Database(), clock, NullLogger<JobStore>.Instance);
        var taskId = ObjectId.GenerateNewId().ToString();
        var userId = ObjectId.GenerateNewId().ToString();
        var task = new ScheduledTask(taskId, userId, "Задача", "", null, "open", new TaskReminder(clock.GetUtcNow().UtcDateTime, null), null!);
        await store.PlanAsync(task, CancellationToken.None);
        await store.PlanAsync(task, CancellationToken.None);
        Assert.Single(await store.PendingAsync(CancellationToken.None));
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.ClaimAsync(60, CancellationToken.None)));
        var first = Assert.Single(claims, job => job is not null);
        clock.Advance(TimeSpan.FromSeconds(61));
        var replacement = await store.ClaimAsync(60, CancellationToken.None);
        Assert.NotNull(replacement);
        Assert.NotEqual(first.LeaseOwner, replacement.LeaseOwner);
        Assert.False(await store.FinishAsync(first, "completed", CancellationToken.None));
        await store.CancelAsync(taskId, userId, CancellationToken.None);
        Assert.False(await store.FinishAsync(replacement, "completed", CancellationToken.None));
        Assert.Empty(await store.PendingAsync(CancellationToken.None));
        task = task with { Reminder = new TaskReminder(clock.GetUtcNow().UtcDateTime.AddMinutes(1), null) };
        await store.PlanAsync(task, CancellationToken.None);
        Assert.Single(await store.PendingAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(1));
        var retry = await store.ClaimAsync(60, CancellationToken.None);
        var retryAt = clock.GetUtcNow().UtcDateTime.AddSeconds(10);
        Assert.True(await store.FinishAsync(retry, "pending", CancellationToken.None, 1, "TimeoutException", retryAt));
        await store.PlanAsync(task, CancellationToken.None);
        var pending = Assert.Single(await store.PendingAsync(CancellationToken.None));
        Assert.Equal(1, pending.Attempts);
        Assert.Equal(retryAt, pending.ScheduledAt);
        Assert.Equal(retry.IdempotencyKey, pending.IdempotencyKey);
        Assert.Equal(retry.TargetAt, pending.TargetAt);
    }

    [Fact]
    public async Task StartupCreatesCollectionIndexesAndHealthyEndpoints()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var ready = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", (await ready.Content.ReadFromJsonAsync<StatusResponse>())?.Status);
        var metrics = await client.GetStringAsync("/metrics");
        Assert.Contains("worker_background_jobs", metrics);
        Assert.Contains("rabbitmq_messages_total", metrics);

        var database = Database();
        Assert.Equal(["backgroundJobs"], (await (await database.ListCollectionNamesAsync()).ToListAsync()).Order());
        await AssertIndexes(database.GetCollection<BsonDocument>("backgroundJobs"));
        await Assert.ThrowsAnyAsync<MongoException>(() =>
            database.GetCollection<BsonDocument>("users").InsertOneAsync(new BsonDocument("test", true)));
        await Assert.ThrowsAnyAsync<MongoException>(() =>
            database.GetCollection<BsonDocument>("users").Find(FilterDefinition<BsonDocument>.Empty).FirstOrDefaultAsync());
    }

    [Fact]
    public async Task InitializationIsIdempotentAndUniqueIndexIsEnforced()
    {
        await using (var firstFactory = CreateFactory())
            firstFactory.CreateClient();

        var jobs = Database().GetCollection<BsonDocument>("backgroundJobs");
        await jobs.InsertOneAsync(new BsonDocument("idempotencyKey", "job-1"));
        await Assert.ThrowsAsync<MongoWriteException>(() =>
            jobs.InsertOneAsync(new BsonDocument("idempotencyKey", "job-1")));

        await using (var secondFactory = CreateFactory())
            secondFactory.CreateClient();

        Assert.Equal(1, await jobs.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        await AssertIndexes(jobs);
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
            .UseSetting("WebApi:BaseUrl", "http://127.0.0.1:1"));

    IMongoDatabase Database() => new MongoClient(ApplicationConnectionString()).GetDatabase(DatabaseName);

    string ApplicationConnectionString() =>
        $"mongodb://{WorkerUsername}:{WorkerPassword}@{container.Hostname}:{container.GetMappedPublicPort(27017)}/{DatabaseName}?authSource={DatabaseName}";

    static async Task AssertIndexes(IMongoCollection<BsonDocument> collection)
    {
        var indexes = await (await collection.Indexes.ListAsync()).ToListAsync();
        Assert.Equal(
            ["_id_", "idempotencyKey_unique", "status_scheduledAt_leaseUntil"],
            indexes.Select(document => document["name"].AsString).Order());
    }

    sealed record StatusResponse(string Status);
}
