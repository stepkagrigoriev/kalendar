using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kalendar.WebApi.Tasks;

static class InternalEndpoints
{
    public static void AddInternalApi(this IServiceCollection services, IConfiguration configuration)
    {
        var key = configuration["InternalApi:Key"];
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
            throw new InvalidOperationException("InternalApi:Key должен содержать не меньше 32 байт.");
        services.AddSingleton(new InternalKey(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
    }

    public static void MapInternalEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal").AddEndpointFilter(async (context, next) =>
        {
            var expected = context.HttpContext.RequestServices.GetRequiredService<InternalKey>();
            var supplied = context.HttpContext.Request.Headers["X-Internal-Api-Key"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(expected.Hash, SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
                return Results.Problem(statusCode: 401);
            return await next(context);
        });
        group.MapGet("/tasks/scheduled", async (HttpRequest request, IMongoDatabase db, CancellationToken ct) =>
        {
            var filter = Builders<TaskDocument>.Filter.Or(
                Builders<TaskDocument>.Filter.Ne("reminder.at", BsonNull.Value),
                Builders<TaskDocument>.Filter.Ne(x => x.Recurrence, null));
            if (request.Query.TryGetValue("after", out var cursor) && !string.IsNullOrEmpty(cursor))
            {
                if (!ObjectId.TryParse(cursor, out var after)) return Results.BadRequest();
                filter &= Builders<TaskDocument>.Filter.Gt(x => x.Id, after);
            }
            var limit = request.Query.TryGetValue("limit", out var size) ? int.TryParse(size, out var count) ? count : 0 : 500;
            if (limit is < 1 or > 500) return Results.BadRequest();
            var tasks = await TaskEndpoints.Tasks(db).Find(filter).SortBy(x => x.Id).Limit(limit).ToListAsync(ct);
            return Results.Ok(tasks.Select(x => x.Response()));
        });
        group.MapGet("/tasks/{taskId}", async (string taskId, string userId, IMongoDatabase db, CancellationToken ct) =>
        {
            var task = await FindAsync(taskId, userId, db, ct);
            return task is null ? Results.NotFound() : Results.Ok(task.Response());
        });
        group.MapPost("/tasks/{taskId}/reminder-sent", async (string taskId, ReminderSent request, IMongoDatabase db, TimeProvider clock, CancellationToken ct) =>
        {
            var task = await FindAsync(taskId, request.UserId, db, ct);
            if (task is null) return Results.NotFound();
            var at = Round(request.ReminderAt.UtcDateTime);
            await TaskEndpoints.Tasks(db).UpdateOneAsync(x => x.Id == task.Id && x.UserId == task.UserId &&
                x.Reminder.At == at && x.Reminder.SentAt == null,
                Builders<TaskDocument>.Update.Set(x => x.Reminder.SentAt, clock.GetUtcNow().UtcDateTime), cancellationToken: ct);
            return Results.Ok();
        });
        group.MapPost("/tasks/{taskId}/recurrence-instances", RepeatAsync);
    }

    static async Task<IResult> RepeatAsync(string taskId, RepeatInstance request, IMongoDatabase db, TaskEvents events, TimeProvider clock, CancellationToken ct)
    {
        var original = await FindAsync(taskId, request.UserId, db, ct);
        if (original is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200) return Results.BadRequest();
        var collection = TaskEndpoints.Tasks(db);
        var existing = await collection.Find(x => x.RecurrenceKey == request.IdempotencyKey && x.UserId == original.UserId).FirstOrDefaultAsync(ct);
        if (existing is not null) return Results.Ok(new { id = existing.Id.ToString() });
        var dueAt = Round(request.DueAt.UtcDateTime);
        if (original.Recurrence is null || original.DueAt is null || dueAt <= original.DueAt ||
            original.Recurrence.EndAt < dueAt || request.IdempotencyKey != $"recurrence:{original.Id}:{Round(original.DueAt.Value):O}")
            return Results.Conflict();
        var now = clock.GetUtcNow().UtcDateTime;
        var next = new TaskDocument
        {
            Id = ObjectId.GenerateNewId(),
            UserId = original.UserId,
            ListId = original.ListId,
            Title = original.Title,
            Description = original.Description,
            Tags = original.Tags,
            Priority = original.Priority,
            DueAt = dueAt,
            Recurrence = original.Recurrence,
            RecurrenceKey = request.IdempotencyKey,
            CreatedAt = now,
            UpdatedAt = now,
            Reminder = original.Reminder?.At is { } at ? new Reminder { At = dueAt + (at - original.DueAt.Value) } : null
        };
        try
        {
            await collection.InsertOneAsync(next, cancellationToken: ct);
            await events.ChangedAsync(next, ct);
            return Results.Ok(new { id = next.Id.ToString() });
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            existing = await collection.Find(x => x.RecurrenceKey == request.IdempotencyKey && x.UserId == original.UserId).FirstOrDefaultAsync(ct);
            return existing is null ? Results.Conflict() : Results.Ok(new { id = existing.Id.ToString() });
        }
    }

    static async Task<TaskDocument> FindAsync(string taskId, string userId, IMongoDatabase db, CancellationToken ct)
    {
        if (!ObjectId.TryParse(taskId, out var id) || !ObjectId.TryParse(userId, out var owner)) return null;
        return await TaskEndpoints.Tasks(db).Find(x => x.Id == id && x.UserId == owner).FirstOrDefaultAsync(ct);
    }

    static DateTime Round(DateTime date) => new(date.Ticks - date.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    sealed record InternalKey(byte[] Hash);
    sealed record ReminderSent(string UserId, DateTimeOffset ReminderAt);
    sealed record RepeatInstance(string UserId, DateTimeOffset DueAt, string IdempotencyKey);
}
