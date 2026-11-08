using MongoDB.Bson;
using MongoDB.Driver;

try
{
    await DatabaseSetup.RunAsync();
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Не удалось настроить MongoDB: {exception.GetType().Name}.");
    return 1;
}

public static class DatabaseSetup
{
    public static async Task RunAsync(CancellationToken cancellationToken = default, Func<string, string?>? readVariable = null)
    {
        readVariable ??= Environment.GetEnvironmentVariable;
        string Required(string name) => readVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Не задана переменная {name}.");

        var host = Required("MONGO_HOST");
        var root = Required("MONGO_ROOT_USERNAME");
        var password = Required("MONGO_ROOT_PASSWORD");
        var settings = MongoClientSettings.FromConnectionString(
            $"mongodb://{Uri.EscapeDataString(root)}:{Uri.EscapeDataString(password)}@{host}/admin");
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var database = new MongoClient(settings).GetDatabase("kalendar");

        foreach (var (role, prefix, collections) in new[]
        {
            ("kalendarWebApi", "KALENDAR_WEBAPI_MONGO", new[] { "users", "tasks", "taskLists" }),
            ("kalendarWorker", "KALENDAR_WORKER_MONGO", new[] { "backgroundJobs" })
        })
        {
            var privileges = new BsonArray(collections.Select(collection => new BsonDocument
            {
                { "resource", new BsonDocument { { "db", "kalendar" }, { "collection", collection } } },
                { "actions", new BsonArray { "find", "insert", "update", "remove", "createCollection", "createIndex", "listIndexes" } }
            }));
            privileges.Add(new BsonDocument
            {
                { "resource", new BsonDocument { { "db", "kalendar" }, { "collection", "" } } },
                { "actions", new BsonArray { "listCollections" } }
            });
            var roles = await database.RunCommandAsync<BsonDocument>(new BsonDocument("rolesInfo", role), cancellationToken: timeout.Token);
            if (roles["roles"].AsBsonArray.Count == 0)
                await database.RunCommandAsync<BsonDocument>(new BsonDocument
                {
                    { "createRole", role }, { "privileges", privileges }, { "roles", new BsonArray() }
                }, cancellationToken: timeout.Token);

            var username = Required($"{prefix}_USER");
            var userPassword = Required($"{prefix}_PASSWORD");
            var users = await database.RunCommandAsync<BsonDocument>(new BsonDocument("usersInfo", username), cancellationToken: timeout.Token);
            if (users["users"].AsBsonArray.Count == 0)
                await database.RunCommandAsync<BsonDocument>(new BsonDocument
                {
                    { "createUser", username }, { "pwd", userPassword },
                    { "roles", new BsonArray { new BsonDocument { { "role", role }, { "db", "kalendar" } } } }
                }, cancellationToken: timeout.Token);
        }
    }
}
