using System.Net;
using System.Net.Http.Json;
using DotNet.Testcontainers.Configurations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
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

    readonly MongoDbContainer container = new MongoDbBuilder("mongo:8.0.32")
        .WithUsername(RootUsername)
        .WithPassword(RootPassword)
        .WithEnvironment("KALENDAR_WEBAPI_MONGO_USER", WebApiUsername)
        .WithEnvironment("KALENDAR_WEBAPI_MONGO_PASSWORD", WebApiPassword)
        .WithEnvironment("KALENDAR_WORKER_MONGO_USER", WorkerUsername)
        .WithEnvironment("KALENDAR_WORKER_MONGO_PASSWORD", WorkerPassword)
        .WithBindMount(InitScriptPath(), "/docker-entrypoint-initdb.d/init.js", AccessMode.ReadOnly)
        .Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

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
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mongo:ConnectionString"] = ApplicationConnectionString(),
                ["Mongo:Database"] = DatabaseName
            })));

    IMongoDatabase Database() => new MongoClient(ApplicationConnectionString()).GetDatabase(DatabaseName);

    string ApplicationConnectionString() =>
        $"mongodb://{WebApiUsername}:{WebApiPassword}@{container.Hostname}:{container.GetMappedPublicPort(27017)}/{DatabaseName}?authSource={DatabaseName}";

    static string InitScriptPath() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../infrastructure/mongodb/init.js"));

    static async Task AssertIndexes(IMongoCollection<BsonDocument> collection, params string[] expected)
    {
        var indexes = await (await collection.Indexes.ListAsync()).ToListAsync();
        Assert.Equal(expected.Order(), indexes.Select(document => document["name"].AsString).Order());
    }

    sealed record StatusResponse(string Status);
}
