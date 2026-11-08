using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using Xunit;

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

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task StartupCreatesCollectionIndexesAndHealthyEndpoints()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var ready = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", (await ready.Content.ReadFromJsonAsync<StatusResponse>())?.Status);

        var database = Database();
        Assert.Equal(["backgroundJobs"], (await (await database.ListCollectionNamesAsync()).ToListAsync()).Order());
        await AssertIndexes(database.GetCollection<BsonDocument>("backgroundJobs"));
        await Assert.ThrowsAnyAsync<MongoException>(() =>
            database.GetCollection<BsonDocument>("users").InsertOneAsync(new BsonDocument("test", true)));
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
            .UseSetting("Mongo:Database", DatabaseName));

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
