using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Extensions.DiagnosticSources;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Kalendar.Worker;

static class MongoInfrastructure
{
    static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

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
            if (!existing.Contains("backgroundJobs", StringComparer.Ordinal))
            {
                try { await database.CreateCollectionAsync("backgroundJobs", cancellationToken: timeout.Token); }
                catch (MongoCommandException exception) when (exception.Code == 48) { }
            }

            var collection = database.GetCollection<BsonDocument>("backgroundJobs");
            await collection.Indexes.CreateManyAsync(
            [
                Index(new BsonDocument { { "status", 1 }, { "scheduledAt", 1 }, { "leaseUntil", 1 } }, "status_scheduledAt_leaseUntil"),
                Index(new BsonDocument("idempotencyKey", 1), "idempotencyKey_unique", unique: true)
            ], timeout.Token);
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

    static CreateIndexModel<BsonDocument> Index(BsonDocument keys, string name, bool unique = false) =>
        new(
            new BsonDocumentIndexKeysDefinition<BsonDocument>(keys),
            new CreateIndexOptions { Name = name, Unique = unique });
}
