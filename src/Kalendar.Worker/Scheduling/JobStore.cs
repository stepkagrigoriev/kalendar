using MongoDB.Bson;
using MongoDB.Driver;

namespace Kalendar.Worker.Scheduling;

sealed class JobStore(IMongoDatabase database, TimeProvider clock, ILogger<JobStore> logger)
{
    readonly IMongoCollection<BackgroundJob> jobs = database.GetCollection<BackgroundJob>("backgroundJobs");
    static readonly FilterDefinitionBuilder<BackgroundJob> Filter = Builders<BackgroundJob>.Filter;
    static readonly UpdateDefinitionBuilder<BackgroundJob> Update = Builders<BackgroundJob>.Update;
    static FilterDefinition<BackgroundJob> Unfinished => Filter.In(x => x.Status, ["pending", "processing"]);

    public async Task PlanAsync(ScheduledTask task, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var kind in new[] { "reminder", "recurrence" })
        {
            DateTime? target = kind == "reminder"
                ? task.Status == "open" && task.Reminder?.SentAt is null && task.Reminder?.At >= now.AddHours(-24) ? task.Reminder.At : null
                : task.Recurrence is not null && task.DueAt is { } due && RecurrenceDates.Next(due, task.Recurrence) is not null ? task.DueAt : null;
            var userId = ObjectId.Parse(task.UserId);
            var taskId = ObjectId.Parse(task.Id);
            var filter = Filter.Eq(x => x.TaskId, taskId) & Filter.Eq(x => x.UserId, userId) & Filter.Eq(x => x.Kind, kind) & Unfinished;
            if (target is { } at) filter &= Filter.Ne(x => x.TargetAt, RecurrenceDates.Milliseconds(at));
            await jobs.UpdateManyAsync(filter, CancelUpdate(now), cancellationToken: ct);
            if (target is null) continue;
            var rounded = RecurrenceDates.Milliseconds(target.Value);
            try
            {
                await jobs.InsertOneAsync(new BackgroundJob
                {
                    Id = ObjectId.GenerateNewId(),
                    UserId = userId,
                    TaskId = taskId,
                    Kind = kind,
                    TargetAt = rounded,
                    ScheduledAt = rounded,
                    IdempotencyKey = RecurrenceDates.Key(kind, task.Id, rounded),
                    CreatedAt = now,
                    UpdatedAt = now
                }, cancellationToken: ct);
            }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
        }
    }

    public Task CancelAsync(string taskId, string userId, CancellationToken ct) => jobs.UpdateManyAsync(
        Filter.Eq(x => x.TaskId, ObjectId.Parse(taskId)) & Filter.Eq(x => x.UserId, ObjectId.Parse(userId)) & Unfinished,
        CancelUpdate(clock.GetUtcNow().UtcDateTime), cancellationToken: ct);

    static UpdateDefinition<BackgroundJob> CancelUpdate(DateTime now) => Update.Set(x => x.Status, "cancelled")
        .Set(x => x.LeaseOwner, null).Set(x => x.LeaseUntil, null).Set(x => x.UpdatedAt, now);

    public Task<BackgroundJob> ClaimAsync(int leaseSeconds, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var filter = (Filter.Eq(x => x.Status, "pending") & Filter.Lte(x => x.ScheduledAt, now)) |
            (Filter.Eq(x => x.Status, "processing") & Filter.Lt(x => x.LeaseUntil, now));
        return jobs.FindOneAndUpdateAsync(filter, Update.Set(x => x.Status, "processing")
            .Set(x => x.LeaseOwner, Guid.NewGuid().ToString()).Set(x => x.LeaseUntil, now.AddSeconds(leaseSeconds)).Set(x => x.UpdatedAt, now),
            new FindOneAndUpdateOptions<BackgroundJob> { ReturnDocument = ReturnDocument.After, Sort = Builders<BackgroundJob>.Sort.Ascending(x => x.ScheduledAt) }, ct);
    }

    public async Task<bool> FinishAsync(BackgroundJob job, string status, CancellationToken ct, int attempts = 0, string error = null, DateTime? retryAt = null)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var update = Update.Set(x => x.Status, status).Set(x => x.Attempts, attempts).Set(x => x.LastError, error)
            .Set(x => x.LeaseOwner, null).Set(x => x.LeaseUntil, null).Set(x => x.UpdatedAt, now)
            .Set(x => x.CompletedAt, status == "completed" ? now : (DateTime?)null);
        if (retryAt.HasValue) update = update.Set(x => x.ScheduledAt, retryAt.Value);
        var result = await jobs.UpdateOneAsync(x => x.Id == job.Id && x.Status == "processing" && x.LeaseOwner == job.LeaseOwner,
            update, cancellationToken: ct);
        if (result.MatchedCount == 0) logger.LogWarning("Аренда задания {JobId} потеряна", job.Id);
        return result.MatchedCount > 0;
    }

    public Task<List<BackgroundJob>> PendingAsync(CancellationToken ct) => jobs.Find(Unfinished).ToListAsync(ct);
}
