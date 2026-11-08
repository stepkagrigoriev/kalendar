using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Extensions.DiagnosticSources;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Kalendar.WebApi;

static class MongoInfrastructure
{
    static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    static readonly string[] Collections = ["users", "tasks", "taskLists"];

    public static IServiceCollection AddMongoDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["Mongo:ConnectionString"];
        var databaseName = configuration["Mongo:Database"];

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("MongoDB connection string is required.");
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new InvalidOperationException("MongoDB database name is required.");

        MongoClientSettings settings;
        try
        {
            settings = MongoClientSettings.FromConnectionString(connectionString);
        }
        catch (Exception exception) when (exception is FormatException or MongoConfigurationException)
        {
            throw new InvalidOperationException("MongoDB connection string is invalid.");
        }

        settings.ServerSelectionTimeout = OperationTimeout;
        settings.ConnectTimeout = OperationTimeout;
        settings.ClusterConfigurator = cluster => cluster.Subscribe(new DiagnosticsActivityEventSubscriber(
            new InstrumentationOptions { CaptureCommandText = false }));
        services.AddSingleton<IMongoClient>(new MongoClient(settings));
        services.AddSingleton<IMongoDatabase>(provider => provider.GetRequiredService<IMongoClient>().GetDatabase(databaseName));
        return services;
    }

    public static async Task InitializeAsync(IMongoDatabase database, ILogger logger, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            var existing = await (await database.ListCollectionNamesAsync(cancellationToken: timeout.Token)).ToListAsync(timeout.Token);
            foreach (var collection in Collections.Except(existing, StringComparer.Ordinal))
            {
                try { await database.CreateCollectionAsync(collection, cancellationToken: timeout.Token); }
                catch (MongoCommandException exception) when (exception.Code == 48) { }
            }

            await CreateIndexesAsync(database, timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogCritical("Ошибка инициализации MongoDB: {ErrorType}", exception.GetType().Name);
            throw new InvalidOperationException("Не удалось создать коллекции и индексы MongoDB. Проверьте доступность, права и совместимость индексов.");
        }
    }

    public static async Task<IResult> CheckReadinessAsync(IMongoDatabase database, HealthCheckService health, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);

        try
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: timeout.Token);
            if ((await health.CheckHealthAsync(timeout.Token)).Status != HealthStatus.Healthy)
                return Results.Json(new { status = "Unhealthy" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            return Results.Ok(new { status = "Healthy" });
        }
        catch (Exception)
        {
            return Results.Json(new { status = "Unhealthy" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    static async Task CreateIndexesAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        await database.GetCollection<BsonDocument>("users").Indexes.CreateOneAsync(
            Index(new BsonDocument("normalizedEmail", 1), "normalizedEmail_unique", unique: true),
            cancellationToken: cancellationToken);

        await database.GetCollection<BsonDocument>("taskLists").Indexes.CreateOneAsync(
            Index(new BsonDocument { { "userId", 1 }, { "name", 1 } }, "userId_name_unique", unique: true),
            cancellationToken: cancellationToken);

        var tasks = database.GetCollection<BsonDocument>("tasks");
        await tasks.Indexes.CreateManyAsync(
        [
            Index(new BsonDocument { { "userId", 1 }, { "dueAt", 1 } }, "userId_dueAt"),
            Index(new BsonDocument { { "userId", 1 }, { "status", 1 }, { "dueAt", 1 } }, "userId_status_dueAt"),
            Index(new BsonDocument { { "userId", 1 }, { "listId", 1 }, { "dueAt", 1 } }, "userId_listId_dueAt"),
            Index(new BsonDocument { { "userId", 1 }, { "tags", 1 } }, "userId_tags"),
            Index(
                new BsonDocument("recurrenceKey", 1),
                "recurrenceKey_unique",
                unique: true,
                partialFilter: new BsonDocument("recurrenceKey", new BsonDocument("$type", "string")))
        ], cancellationToken);
    }

    static CreateIndexModel<BsonDocument> Index(BsonDocument keys, string name, bool unique = false, BsonDocument partialFilter = null) =>
        new(
            new BsonDocumentIndexKeysDefinition<BsonDocument>(keys),
            new CreateIndexOptions<BsonDocument>
            {
                Name = name,
                Unique = unique,
                PartialFilterExpression = partialFilter is null ? null : new BsonDocumentFilterDefinition<BsonDocument>(partialFilter)
            });
}
